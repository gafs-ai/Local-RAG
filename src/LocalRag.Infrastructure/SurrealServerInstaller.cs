using System.Security.Cryptography;

namespace LocalRag.Infrastructure;

public sealed class SurrealServerInstaller(string toolsDirectory)
{
    public const string Version = "3.3.5";
    public const string Sha256 = "fb859638bd875d658d483897d8d0c3eca6215e94a2946ff75693456b45e3bbbc";
    public const string DownloadUrl = "https://github.com/surrealdb/surrealdb/releases/download/v3.3.5/surreal-v3.3.5.windows-amd64.exe";
    public string ExecutablePath { get; } = Path.Combine(Path.GetFullPath(toolsDirectory), "surrealdb", Version, "surreal.exe");

    public async Task<string> EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ExecutablePath)!);
        // Serializes installation even if two app instances use the same tool cache.
        await using var installationLock = await AcquireLockAsync(ExecutablePath + ".lock", token).ConfigureAwait(false);
        if (File.Exists(ExecutablePath))
        {
            if (!await HashMatchesAsync(ExecutablePath, Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("SurrealDB実行ファイルの検証に失敗しました。既存ファイルは実行しません。");
            return ExecutablePath;
        }

        progress?.Report($"SurrealDB {Version} をダウンロードしています（約307 MiB、初回のみ）…");
        var temporary = ExecutablePath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, token).ConfigureAwait(false);
            if (!await HashMatchesAsync(temporary, Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("SurrealDBのダウンロード検証に失敗しました。");
            File.Move(temporary, ExecutablePath);
            return ExecutablePath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(200, token).ConfigureAwait(false); }
        }
    }
}
