using System.Text.Json;
using System.Text.Json.Nodes;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class DataSourceTests
{
    [Fact]
    public async Task SettingsDefaultAndUnicodeNameRoundTripWithoutChangingPipelineIdentity()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var initial = await store.LoadAsync();
        Assert.StartsWith("Local-" + Environment.UserName + "-", initial.DataSourceName);
        Assert.Matches("-[A-Z0-9]{4}$", initial.DataSourceName);
        Assert.Equal(initial.DataSourceName, (await store.LoadAsync()).DataSourceName);
        var renamed = initial with { DataSourceName = "開発PC・東京" };
        Assert.Equal(initial.PipelineConfigurationId(), renamed.PipelineConfigurationId());
        await store.SaveAsync(renamed);
        Assert.Equal("開発PC・東京", (await store.LoadAsync()).DataSourceName);
        Assert.Equal("Local-" + Environment.UserName, JsonSerializer.Deserialize<AppSettings>("{}")!.DataSourceName);
        foreach (var name in new[] { "", " ", "x\ny", " trailing ", new string('x', 201), null! })
            Assert.NotEmpty((initial with { DataSourceName = name }).Validate());
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(renamed with { DataSourceOwner = "different-pc-or-user" }));
        var copied = await store.LoadAsync();
        Assert.NotEqual(renamed.DataSourceName, copied.DataSourceName);
        Assert.Matches("-[A-Z0-9]{4}$", copied.DataSourceName);
        Assert.Equal(copied.DataSourceName, (await store.LoadAsync()).DataSourceName);
    }

    [ServerFact]
    public async Task SamePathAcrossSourcesDoesNotOverwriteDeleteSearchOrResetOtherSource()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "a.txt");
        await File.WriteAllTextAsync(path, "First section.\n\nSecond section.");
        var settings = TextRagTests.Settings(input) with { DataSourceName = "PC-A" };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: settings.DataSourceName);
        var otherSettings = settings with { DataSourceName = "PC-B'日本" };
        await using var otherRepo = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: otherSettings.DataSourceName);
        var ai = new FakeTextAi();
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var before = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal("PC-A", before.DataSourceName);
        var calls = (ai.TransformCalls, ai.EmbeddingCalls);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Unchanged);
        Assert.Equal(calls, (ai.TransformCalls, ai.EmbeddingCalls));
        var after = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal("PC-A", after.DataSourceName);
        Assert.Equal(after.DataSourceName, Assert.Single(await repo.DocumentStatusesAsync(default)).DataSourceName);
        using var other = new TextRagService(otherRepo, ai, otherSettings, Path.Combine(f.Root, "data-b"));
        Assert.Equal(1, (await other.ScanAsync()).Indexed);
        var otherDocument = Assert.Single(await otherRepo.DocumentsAsync(default));
        Assert.NotEqual(before.Key, otherDocument.Key);
        Assert.NotEqual(before.DocumentId, otherDocument.DocumentId);
        Assert.NotEqual(before.RevisionId, otherDocument.RevisionId);
        Assert.Null(await other.FetchAsync(before.DocumentId));
        Assert.All(await other.SearchAsync("section"), h => Assert.Equal(otherDocument.DocumentId, h.DocumentId));
        var checkpoint = new MonitoringCheckpoint(MonitoringCheckpoint.ScopeFor(settings), false, DateTimeOffset.UtcNow);
        var otherCheckpoint = checkpoint with { Scope = MonitoringCheckpoint.ScopeFor(otherSettings) };
        await repo.SaveMonitoringAsync(checkpoint, default); await otherRepo.SaveMonitoringAsync(otherCheckpoint, default);
        Assert.Equal(checkpoint, await repo.LoadMonitoringAsync(default));
        File.Delete(path);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Deleted);
        Assert.Equal("ready", Assert.Single(await otherRepo.DocumentsAsync(default)).Status);
        await repo.ResetIngestionDataAsync();
        Assert.Empty(await repo.DocumentsAsync(default));
        Assert.NotEmpty(await otherRepo.HistoryAsync());
        Assert.Equal(otherCheckpoint, await otherRepo.LoadMonitoringAsync(default));
        await File.WriteAllTextAsync(path, "First section.\n\nSecond section.");
        Assert.NotEmpty(await other.SearchAsync("section"));
        Assert.NotNull(await other.FetchAsync(otherDocument.DocumentId));
        await using var db = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        var names = await db.RawQuery("SELECT VALUE data_source_name FROM text_document;"); names.EnsureAllOks();
        Assert.Equal(new[] { otherSettings.DataSourceName }, names.GetValue<string[]>(0));
    }

    [ServerFact]
    public async Task LegacyMigrationPersistsNameInCurrentAndHistoricalRecordsAndPreservesUnknownFields()
    {
        await using var f = await ServerFixture.StartAsync();
        await using var db = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        (await db.RawQuery("DEFINE TABLE text_document SCHEMALESS; DEFINE TABLE text_revision SCHEMALESS;")).EnsureAllOks();
        foreach (var (revision, name) in new[] { ("old", (string?)null), ("current", "Existing source") })
        {
            var json = JsonSerializer.SerializeToNode(new TextDocumentState { Key = "file", RevisionId = revision, Status = "ready" })!.AsObject();
            json.Remove("DataSourceName");
            if (name is not null) json["DataSourceName"] = name;
            json["FutureProperty"] = "preserve";
            (await db.RawQuery("UPSERT type::record('text_revision', $revision) SET payload=$payload; UPSERT text_document:file SET payload=$payload;",
                new Dictionary<string, object?> { ["revision"] = revision, ["payload"] = json.ToJsonString() })).EnsureAllOks();
        }
        for (var i = 0; i < 2; i++) // repeatable migration; no changes on the second open
        {
            await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: "Existing source");
            Assert.Equal("Existing source", (await repo.GetAsync("file", default))!.DataSourceName);
            foreach (var (table, key, expected) in new[] { ("text_document", "file", "Existing source"),
                ("text_revision", "current", "Existing source"), ("text_revision", "old", "Existing source") })
            {
                var result = await db.RawQuery($"SELECT VALUE {{ Name: data_source_name, Payload: payload }} FROM type::record('{table}', $key);", new Dictionary<string, object?> { ["key"] = key });
                result.EnsureAllOks();
                var row = Assert.Single(result.GetValue<SourceRow[]>(0)!);
                Assert.Equal(expected, row.Name);
                var payload = JsonNode.Parse(row.Payload)!;
                Assert.Equal(expected, payload["DataSourceName"]!.GetValue<string>());
                Assert.Equal("preserve", payload["FutureProperty"]!.GetValue<string>());
            }
        }
    }
    private sealed record SourceRow(string Name, string Payload);

    [ServerFact]
    public async Task LegacyPathKeysAndVectorsMigrateWithoutChangingDocumentIdsOrCallingAi()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "legacy.txt"); await File.WriteAllTextAsync(path, "public migration evidence");
        var settings = TextRagTests.Settings(input);
        TextDocumentState before;
        await using (var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!))
        {
            using var service = new TextRagService(repo, new FakeTextAi(), settings, Path.Combine(f.Root, "data"));
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            before = Assert.Single(await repo.DocumentsAsync(default));
        }
        var oldKey = TextRagSettings.Hash(Path.GetFullPath(path).ToUpperInvariant());
        var document = JsonSerializer.SerializeToNode(before)!.AsObject();
        document["Key"] = oldKey; document.Remove("DataSourceName"); document["FutureField"] = "preserved";
        await using var raw = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        (await raw.RawQuery("BEGIN TRANSACTION; DELETE type::record('text_document', $new); CREATE type::record('text_document', $old) SET payload=$payload; UPDATE type::record('text_revision', $revision) SET payload=$payload, data_source_name=NONE, source_key_version=NONE; COMMIT TRANSACTION;",
            new Dictionary<string, object?> { ["new"] = before.Key, ["old"] = oldKey, ["revision"] = before.RevisionId, ["payload"] = document.ToJsonString() })).EnsureAllOks();
        var table = TextRagRepository.Table(before.SpaceId!);
        var vectors = await raw.RawQuery($"SELECT VALUE payload FROM {table};"); vectors.EnsureAllOks();
        foreach (var payload in vectors.GetValue<string[]>(0)!)
        {
            var chunk = JsonNode.Parse(payload)!.AsObject(); chunk["DocumentKey"] = oldKey;
            (await raw.RawQuery($"UPDATE type::record('{table}', $id) SET document_key=$old, payload=$payload, data_source_name=NONE;",
                new Dictionary<string, object?> { ["id"] = before.RevisionId + "_" + chunk["Ordinal"]!.GetValue<int>(), ["old"] = oldKey, ["payload"] = chunk.ToJsonString() })).EnsureAllOks();
        }
        var embeddings = await raw.RawQuery($"SELECT VALUE embedding FROM {table} ORDER BY id;"); embeddings.EnsureAllOks();
        var expectedVectors = JsonSerializer.Serialize(embeddings.GetValue<float[][]>(0));
        for (var i = 0; i < 2; i++)
        {
            await using var migrated = await TextRagRepository.OpenAsync(f.Manager.Connection!);
            var after = Assert.Single(await migrated.DocumentsAsync(default));
            Assert.Equal(before.DocumentId, after.DocumentId); Assert.Equal(before.RevisionId, after.RevisionId);
            Assert.Equal(before.Key, after.Key); Assert.Null(await migrated.GetAsync(oldKey, default));
            var ai = new FakeTextAi();
            using var service = new TextRagService(migrated, ai, settings, Path.Combine(f.Root, "data"));
            Assert.Equal(1, (await service.ScanAsync()).Unchanged);
            Assert.Equal(0, ai.EmbeddingCalls); Assert.Equal(0, ai.TransformCalls);
            Assert.Equal(before.DocumentId, Assert.Single(await service.SearchAsync("evidence", 1)).DocumentId);
            Assert.Equal(before.Original, (await service.FetchAsync(before.DocumentId))!.Content);
        }
        var finalVectors = await raw.RawQuery($"SELECT VALUE embedding FROM {table} ORDER BY id;"); finalVectors.EnsureAllOks();
        Assert.Equal(expectedVectors, JsonSerializer.Serialize(finalVectors.GetValue<float[][]>(0)));
        var preserved = await raw.RawQuery("SELECT VALUE payload FROM text_document;"); preserved.EnsureAllOks();
        Assert.Equal("preserved", JsonNode.Parse(Assert.Single(preserved.GetValue<string[]>(0)!))!["FutureField"]!.GetValue<string>());
    }
}
