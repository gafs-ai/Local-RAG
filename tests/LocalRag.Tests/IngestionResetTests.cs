using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class IngestionResetTests
{
    [ServerFact]
    public async Task ResetRemovesAllIngestionStateAndSpacesButPreservesSourcesSettingsAndUnrelatedTables()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var source = Path.Combine(input, "guide.md");
        const string original = "## 公開資料\n\n花壇の水やりは朝に行います。";
        await File.WriteAllTextAsync(source, original);
        await File.WriteAllTextAsync(Path.Combine(input, "calculator.cs"), "class C { int F() => 1; }");
        var data = Path.Combine(fixture.Root, "data");
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() };
        var store = new SettingsStore(data); await store.SaveAsync(settings);
        var settingsBefore = await File.ReadAllTextAsync(store.FilePath);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var first = new TextRagService(repo, new FakeTextAi(), settings, data))
            Assert.Equal(2, (await first.ScanAsync()).Indexed);
        var documents = await repo.DocumentsAsync(default);
        Assert.Contains(documents, d => d.CodeUnits is { Count: > 0 });
        Assert.Contains(documents, d => d.ParentEmbeddings.Count > 0);
        var oldId = documents[0].DocumentId;
        var pending = documents[0] with { Key = TextRagSettings.Hash("pending"), RevisionId = TextRagSettings.Hash("pending-revision"), Status = "failed" };
        await repo.SaveAsync(pending, default);
        await repo.SaveMonitoringAsync(new(MonitoringCheckpoint.ScopeFor(settings), false, DateTimeOffset.UtcNow), default);
        await repo.EnsureSpaceAsync(TextRagSettings.Hash("old-model"), 4, default);
        var orphan = TextRagRepository.Table(TextRagSettings.Hash("orphan"));
        await using var raw = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application");
        (await raw.RawQuery($"DEFINE TABLE {orphan} SCHEMALESS; CREATE {orphan}:one SET value='orphan'; CREATE app_notes:keep SET value='preserved'; CREATE text_vectors_keep:one SET value='preserved';")).EnsureAllOks();
        await using var other = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "unrelated");
        (await other.RawQuery("CREATE text_document:keep SET value='other database';")).EnsureAllOks();
        Assert.NotEmpty(await repo.HistoryAsync());

        await repo.ResetIngestionDataAsync();

        Assert.Empty(await repo.DocumentsAsync(default));
        Assert.Empty(await repo.HistoryAsync());
        Assert.Null(await repo.LoadMonitoringAsync(default));
        var remaining = await raw.RawQuery("INFO FOR DB; SELECT VALUE payload FROM text_revision; SELECT VALUE record::id(id) FROM text_space; SELECT VALUE value FROM app_notes; SELECT VALUE value FROM text_vectors_keep;");
        remaining.EnsureAllOks();
        Assert.Contains(orphan, remaining.GetValue<DatabaseInfo>(0)!.tables.Keys); // Unowned rows and shared table definitions are retained.
        Assert.Empty(remaining.GetValue<string[]>(1)!);
        Assert.NotEmpty(remaining.GetValue<string[]>(2)!); // Shared embedding-space metadata is retained.
        Assert.Equal("preserved", Assert.Single(remaining.GetValue<string[]>(3)!));
        Assert.Equal("preserved", Assert.Single(remaining.GetValue<string[]>(4)!));
        var otherData = await other.RawQuery("SELECT VALUE value FROM text_document;");
        otherData.EnsureAllOks();
        Assert.Equal("other database", Assert.Single(otherData.GetValue<string[]>(0)!));
        Assert.Equal(original, await File.ReadAllTextAsync(source));
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(store.FilePath));
        await repo.ResetIngestionDataAsync(); // Already empty is safe.
        using var restarted = new TextRagService(repo, new FakeTextAi(), settings, data);
        Assert.Null(await restarted.FetchAsync(oldId));
        Assert.Empty(await restarted.SearchAsync("水やり"));
        Assert.Equal(2, (await restarted.ScanAsync()).Indexed);
        Assert.NotEmpty(await restarted.SearchAsync("水やり"));
        Assert.All(await repo.DocumentsAsync(default), d => Assert.NotEqual(oldId, d.DocumentId));
    }

    [ServerFact]
    public async Task CancelledResetDoesNotEraseData()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var state = new TextDocumentState { Key = TextRagSettings.Hash("test"), RevisionId = TextRagSettings.Hash("revision"), Original = "preserve" };
        await repo.SaveAsync(state, default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ResetIngestionDataAsync(cancelled.Token));
        Assert.Equal("preserve", Assert.Single(await repo.DocumentsAsync(default)).Original);
    }

    private sealed record DatabaseInfo(Dictionary<string, string> tables);
}
