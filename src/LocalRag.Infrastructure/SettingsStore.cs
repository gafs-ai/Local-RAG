using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed class SettingsStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            foreach (var property in info.Properties)
            {
                if (info.Type == typeof(ManagementSettings) && property.Name is "TextExtensions" or "DefaultTextRoute" or "ExtensionRoutes")
                    property.ShouldSerialize = (owner, _) => ((ManagementSettings)owner).Version == 1;
                if (info.Type == typeof(TextRoute) && property.Name == "Extension")
                    property.ShouldSerialize = (owner, _) => ((TextRoute)owner).Extensions is null;
            }
        });
        return new() { WriteIndented = true, TypeInfoResolver = resolver };
    }
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return Migrate(new AppSettings { Management = ManagementSettings.CreateDefault() });
        await using var stream = File.OpenRead(FilePath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("設定ファイルが空です。元のファイルを確認してください。");
        settings = RemoveSearchPrompts(settings);
        Validate(settings);
        return Migrate(settings);
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        var migrated = settings with { SchemaVersion = 2,
            Management = (settings.Management ?? ManagementSettings.FromLegacy(settings.TextPipeline, settings.Confidentiality)).ToOrderedSettings() };
        Validate(migrated);
        return migrated; // Persist only when the user saves; failed/cancelled edits never rewrite the old file.
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = RemoveSearchPrompts(settings);
        Validate(settings);
        settings = Migrate(settings);
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

    private static AppSettings RemoveSearchPrompts(AppSettings settings) => settings with
    { Management = settings.Management?.WithoutSearchPrompts() };

    private static void Validate(AppSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
