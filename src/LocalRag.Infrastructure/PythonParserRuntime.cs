using System.IO.Compression;
using System.Security.Cryptography;

namespace LocalRag.Infrastructure;

/// <summary>Private, pinned CPython runtime used only for ast.parse. Never installs site packages.</summary>
public sealed class PythonParserRuntime(string toolsDirectory)
{
    public const string Version = "3.14.10";
    public const string Sha256 = "90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159";
    public const string DownloadUrl = "https://www.python.org/ftp/python/3.14.10/python-3.14.10-embed-amd64.zip";
    public async Task<string> EnsureAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine(toolsDirectory, "python-parser", Version));
        Directory.CreateDirectory(root);
        await using var installationLock = await SurrealServerInstaller.AcquireLockAsync(root + ".lock", token).ConfigureAwait(false);
        var archive = root + ".zip";
        if (!File.Exists(archive))
        {
            var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > 32 * 1024 * 1024) throw new InvalidDataException("Python解析用ファイルのサイズが不正です。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                if (!await SurrealServerInstaller.HashMatchesAsync(temporary, Sha256, token).ConfigureAwait(false))
                    throw new InvalidDataException("Python解析用ファイルの検証に失敗しました。");
                File.Move(temporary, archive);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!await SurrealServerInstaller.HashMatchesAsync(archive, Sha256, token).ConfigureAwait(false))
            throw new InvalidDataException("Python解析用アーカイブが変更されています。");
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Python解析用アーカイブのパスが不正です。");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            await using var source = entry.Open();
            var expected = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
            if (File.Exists(target))
            {
                if (!await SurrealServerInstaller.HashMatchesAsync(target, expected, token).ConfigureAwait(false))
                    throw new InvalidDataException("Python解析用ファイルが変更されています。");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".tmp";
                try
                {
                    entry.ExtractToFile(temporary, overwrite: true);
                    File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return Path.Combine(root, "python.exe");
    }
}
