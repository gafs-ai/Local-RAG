using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed class SettingsStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        await using var stream = File.OpenRead(FilePath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("設定ファイルが空です。元のファイルを確認してください。");
        Validate(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            finally { _writeLock.Release(); }
        }
    }

    private static void Validate(AppSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
