using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class SettingsTests
{
    [Fact]
    public async Task NewSettingsEnablePatternsAndDisableExperimentalLlmInspection()
    {
        var store = NewStore();
        var settings = await store.LoadAsync();
        Assert.NotNull(settings.Management);
        Assert.False(settings.Management.Retrieval.LlmEnabled);
        Assert.True(settings.DisclosurePolicy().Enabled);
        Assert.True(settings.DisclosurePolicy().PatternsEnabled);
        Assert.False(settings.DisclosurePolicy().LlmEnabled);
        Assert.False(File.Exists(store.FilePath));
        await store.SaveAsync(settings);
        Assert.False((await store.LoadAsync()).DisclosurePolicy().LlmEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedExplicitInspectionChoiceIsPreservedAcrossLegacyMigrationAndReload(bool enabled)
    {
        var store = NewStore();
        await store.SaveAsync(new AppSettings { SchemaVersion = 1, Confidentiality = new() { LlmEnabled = enabled } });
        var migrated = await store.LoadAsync();
        Assert.Equal(enabled, migrated.DisclosurePolicy().LlmEnabled);
        await store.SaveAsync(migrated);
        Assert.Equal(enabled, (await store.LoadAsync()).DisclosurePolicy().LlmEnabled);
    }
    [Theory]
    [InlineData("https://example.com/api/v1")]
    [InlineData("http://localhost:13305/api/v1?token=secret")]
    [InlineData("http://user:password@localhost:13305/api/v1")]
    [InlineData("file:///C:/data")]
    public void RejectsNonLocalOrCredentialBearingEndpoints(string url)
    {
        Assert.NotEmpty(new AiConnectionSettings { BaseUrl = url }.Validate());
    }

    [Fact]
    public async Task CancelledSavePreservesPreviousSettings()
    {
        var store = NewStore();
        await store.SaveAsync(new AppSettings { MonitoringIntervalMinutes = 20 });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new AppSettings { MonitoringIntervalMinutes = 30 }, cancelled.Token));
        Assert.Equal(20, (await store.LoadAsync()).MonitoringIntervalMinutes);
    }

    [Fact]
    public async Task InvalidSaveDoesNotOverwriteExistingSettings()
    {
        var store = NewStore();
        await store.SaveAsync(new AppSettings());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings { MonitoringIntervalMinutes = 0 }));
        Assert.Equal(5, (await store.LoadAsync()).MonitoringIntervalMinutes);
    }

    [Fact]
    public async Task InvalidExistingFileIsNotSilentlyReplacedWithDefaults()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        const string content = "{\"SchemaVersion\":99}";
        await File.WriteAllTextAsync(store.FilePath, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(content, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task SavesUnicodeFoldersAndModelSettings()
    {
        var store = NewStore();
        var expected = new AppSettings
        {
            Folders = [new WatchedFolder(Path.Combine(Path.GetTempPath(), "仕様書"), false)],
            Ai = new AiConnectionSettings { ChatModel = "local-model" },
            Database = new DatabaseSettings { Port = 18123, StopOnExit = false }
        };
        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();
        Assert.Equal(expected.Folders, actual.Folders);
        Assert.Equal(expected.Ai, actual.Ai);
        Assert.Equal(expected.Database, actual.Database);
    }

    [Fact]
    public async Task ExistingSettingsReceiveSafeDatabaseDefaults()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":1,\"MonitoringIntervalMinutes\":17}");
        var settings = await store.LoadAsync();
        Assert.Equal(17, settings.MonitoringIntervalMinutes);
        Assert.False(settings.DisclosurePolicy().LlmEnabled); // Missing fields use the current default.
        Assert.Equal(8000, settings.Database.Port);
        Assert.True(settings.Database.StopOnExit);
        Assert.NotEmpty(new AppSettings { Database = new() { Port = 0 } }.Validate());
    }

    private static SettingsStore NewStore() => new(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
}
