using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalRag.Infrastructure;

/// <summary>Fixed wheels, no pip, no user site packages; keep package licenses alongside bindings.</summary>
public static class TreeSitterRuntime
{
    private sealed record Package(string Name, string Version, string FileName, string Url, string Sha256);
    public static async Task<List<string>> EnsureAsync(string toolsDirectory, string grammar, CancellationToken token)
    {
        using var manifest = typeof(TreeSitterRuntime).Assembly.GetManifestResourceStream("LocalRag.Infrastructure.TreeSitterPackages.json")!;
        var packages = JsonSerializer.Deserialize<List<Package>>(manifest)!;
        var paths = new List<string>();
        foreach (var package in packages.Where(p => p.Name == "tree-sitter" || p.Name == "tree-sitter-" + grammar))
        {
            var root = Path.GetFullPath(Path.Combine(toolsDirectory, "tree-sitter", package.Name + "-" + package.Version));
            Directory.CreateDirectory(root);
            await using var installationLock = await SurrealServerInstaller.AcquireLockAsync(root + ".lock", token).ConfigureAwait(false);
            var archive = root + ".whl";
            if (!File.Exists(archive))
            {
                var temporary = archive + ".download";
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using (var target = File.Create(temporary))
                    {
                        var buffer = new byte[81920];
                        var total = 0;
                        int count;
                        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            total += count;
                            if (total > 8 * 1024 * 1024) throw new InvalidDataException("構文解析パッケージのサイズが不正です。");
                            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        }
                    }
                    if (!await SurrealServerInstaller.HashMatchesAsync(temporary, package.Sha256, token).ConfigureAwait(false))
                        throw new InvalidDataException("構文解析パッケージの検証に失敗しました。");
                    File.Move(temporary, archive);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!await SurrealServerInstaller.HashMatchesAsync(archive, package.Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("構文解析パッケージが変更されています。");
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("構文解析パッケージのパスが不正です。");
                if (entry.Name.Length == 0) continue;
                await using var input = entry.Open();
                var expected = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                if (File.Exists(destination))
                {
                    if (!await SurrealServerInstaller.HashMatchesAsync(destination, expected, token).ConfigureAwait(false))
                        throw new InvalidDataException("構文解析ライブラリが変更されています。");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + ".tmp";
                    try { entry.ExtractToFile(temporary, overwrite: true); File.Move(temporary, destination); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            paths.Add(root);
        }
        if (paths.Count != 2) throw new InvalidDataException("対応する構文解析ライブラリがありません。");
        return paths;
    }
}
