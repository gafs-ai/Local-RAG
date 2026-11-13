using System.Text.Json;
using System.Text.Json.Nodes;
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

    // The bridge does not interpret ingestion settings. Future pipeline/schema revisions
    // must not prevent an already running desktop server from being reached.
    public async Task<McpSettings> LoadMcpAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(FilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP接続設定が不正です。");
        var entries = document.RootElement.EnumerateObject().Where(p => p.Name == "Mcp").ToArray();
        if (entries.Length != 1 || entries[0].Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP接続設定がありません、または重複しています。");
        var fields = entries[0].Value.EnumerateObject().ToArray();
        if (fields.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("MCP接続設定が重複しています。");
        var settings = entries[0].Value.Deserialize<McpSettings>()!;
        if (settings.Validate().Count != 0) throw new InvalidDataException("MCP接続設定が不正です。");
        return settings;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        JsonObject? stored = null;
        var needsName = !File.Exists(FilePath);
        if (needsName) settings = new AppSettings { Management = ManagementSettings.CreateDefault() };
        else
        {
            await using var stream = File.OpenRead(FilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            settings = document.RootElement.Deserialize<AppSettings>(Options)
                ?? throw new InvalidDataException("設定ファイルが空です。元のファイルを確認してください。");
            stored = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            needsName = !document.RootElement.TryGetProperty("DataSourceName", out _);
        }
        settings = RemoveSearchPrompts(settings);
        Validate(settings);
        settings = Migrate(settings);
        var owner = LocalDataSourceIdentity.Owner();
        var newIdentity = needsName || settings.DataSourceOwner is { } previousOwner && previousOwner != owner;
        if (newIdentity)
            settings = settings with { DataSourceName = LocalDataSourceIdentity.NewName() };
        settings = settings with { DataSourceOwner = owner };
        if (newIdentity)
        {
            if (stored is null) await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            else
            {
                // Persist just the generated identity. Do not implicitly migrate or remove
                // unrelated user settings before an explicit save.
                stored["DataSourceName"] = settings.DataSourceName;
                stored["DataSourceOwner"] = owner;
                await WriteAsync(stored, cancellationToken).ConfigureAwait(false);
            }
        }
        return settings;
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        var migrated = settings with { SchemaVersion = settings.Ai.Connections.Count > 0 ? 3 : 2,
            Management = (settings.Management ?? ManagementSettings.FromLegacy(settings.TextPipeline, settings.Confidentiality)).ToOrderedSettings() };
        Validate(migrated);
        return migrated; // Persist only when the user saves; failed/cancelled edits never rewrite the old file.
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = RemoveSearchPrompts(settings);
        Validate(settings);
        settings = Migrate(settings);
        settings = settings with { DataSourceOwner = LocalDataSourceIdentity.Owner() };
        await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync<T>(T value, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
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
