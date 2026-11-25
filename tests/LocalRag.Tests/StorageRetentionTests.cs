using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class StorageRetentionTests
{
    [Theory]
    [InlineData("Payload too large", "size_limit")]
    [InlineData("Storage: No space left on device (os error 28)", "storage_full")]
    [InlineData("ディスクに十分な空き領域がありません。 (os error 112)", "storage_full")]
    [InlineData("Error while processing event custom: Payload too large", "event")]
    [InlineData("An unexpected value from a document", "query")]
    public void ErrorClassificationUsesFixedCategories(string detail, string expected) =>
        Assert.Equal(expected, TextRagRepository.RetentionErrorCategory(null, detail));

    [ServerFact]
    public async Task VectorCleanupBoundsWritesAndRechecksDocumentBetweenBatches()
    {
        await using var f = await ServerFixture.StartAsync();
        const string source = "batch-PC";
        var space = TextRagSettings.Hash("batch-space");
        var table = TextRagRepository.Table(space);
        var ready = new TextDocumentState { Key = "guide", DataSourceName = source, Status = "ready", SpaceId = space, RevisionId = "published" };
        await using (var initial = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: source))
        {
            await initial.EnsureSpaceAsync(space, 2, default);
            await initial.SaveAsync(ready, default);
        }
        await using var db = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        const int staleRows = 2 * TextRagRepository.RetentionVectorBatchSize + 3;
        var p = new Dictionary<string, object?> { ["source"] = source, ["marker"] = "retention_v1_" + TextRagSettings.Hash(source),
            ["count"] = staleRows, ["inflight"] = JsonSerializer.Serialize(ready with { Status = "processing", RevisionId = "incoming" }),
            ["published"] = JsonSerializer.Serialize(ready) };
        (await db.RawQuery($$"""
            DELETE type::record('text_source_metadata',$marker);
            FOR $i IN 0..$count { CREATE type::record('{{table}}',$i) SET revision='old', document_key='guide', data_source_name=$source, embedding=[0.6f,0.8f] RETURN NONE; };
            CREATE {{table}}:live SET revision='published', document_key='guide', data_source_name=$source, embedding=[1f,0f];
            CREATE {{table}}:foreign SET revision='old', document_key='guide', data_source_name='Other', embedding=[0f,1f];
            """, p)).EnsureAllOks();
        var checks = 0;
        var batchBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var opening = Task.Run(() => TextRagRepository.OpenAsync(f.Manager.Connection!, default, source, () =>
        {
            if (++checks != 3) return true; // Revision lookup, first vector batch, second vector batch.
            batchBoundary.SetResult();
            return resume.Wait(TimeSpan.FromSeconds(30));
        }));
        string rows = $"SELECT VALUE revision FROM {table} WHERE data_source_name=$source;";
        try
        {
            await batchBoundary.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var partial = await db.RawQuery(rows, p);
            partial.EnsureAllOks();
            Assert.Equal(staleRows - TextRagRepository.RetentionVectorBatchSize, partial.GetValue<string[]>(0)!.Count(s => s == "old"));
            (await db.RawQuery("UPDATE text_document:guide SET payload=$inflight;", p)).EnsureAllOks();
        }
        finally { resume.Set(); }
        await using var repo = await opening;
        Assert.Null(repo.AutomaticRetentionNotice);
        var retained = await db.RawQuery(rows, p);
        retained.EnsureAllOks();
        Assert.Equal(staleRows - TextRagRepository.RetentionVectorBatchSize, retained.GetValue<string[]>(0)!.Count(s => s == "old"));
        (await db.RawQuery("UPDATE text_document:guide SET payload=$published;", p)).EnsureAllOks();
        await repo.ReclaimUnusedDataAsync();
        var cleaned = await db.RawQuery(rows + $" SELECT VALUE data_source_name FROM {table}:foreign;", p);
        cleaned.EnsureAllOks();
        Assert.Equal(["published"], cleaned.GetValue<string[]>(0)!);
        Assert.Equal(["Other"], cleaned.GetValue<string[]>(1)!);
    }

    [ServerFact]
    public async Task RejectedAutomaticCleanupKeepsIndexAvailableAndRetriesAfterTheCauseIsResolved()
    {
        await using var f = await ServerFixture.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(f.Root, "docs")).FullName;
        const string text = "花壇の水やりは朝に行います。";
        await File.WriteAllTextAsync(Path.Combine(root, "guide.txt"), text);
        var settings = TextRagTests.Settings(root);
        var source = settings.DataSourceName;
        var ai = new FakeTextAi();
        var data = Path.Combine(f.Root, "data");
        Guid documentId;
        await using (var initial = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: source))
        {
            using var service = new TextRagService(initial, ai, settings, data);
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            documentId = Assert.Single(await initial.DocumentsAsync(default)).DocumentId;
        }
        await using var db = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        var parameters = new Dictionary<string, object?> { ["marker"] = "retention_v1_" + TextRagSettings.Hash(source), ["source"] = source };
        (await db.RawQuery("""
            DELETE type::record('text_source_metadata',$marker);
            CREATE text_revision:legacy SET source_key_version=1, data_source_name=$source, payload='previous';
            DEFINE EVENT reject_deletion ON TABLE text_revision WHEN $event='DELETE' THEN { THROW 'test-only cleanup rejection'; };
            """, parameters)).EnsureAllOks();
        await using (var deferred = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: source))
        {
            Assert.NotNull(deferred.AutomaticRetentionNotice);
            var error = Assert.IsType<TextRagRepository.RetentionQueryFailure>(deferred.AutomaticRetentionError);
            Assert.Equal("retention.delete_revisions.event", error.Code);
            OperationalDiagnostics.Write(data, "rag.cleanup-deferred", error);
            var log = await File.ReadAllTextAsync(Path.Combine(data, "logs", "operations.jsonl"));
            Assert.Contains(error.Code, log);
            Assert.DoesNotContain("test-only cleanup rejection", log);
            using var service = new TextRagService(deferred, ai, settings, data);
            Assert.Equal(documentId, Assert.Single(await service.SearchAsync("水やり", 1)).DocumentId);
            Assert.Equal(text, (await service.FetchAsync(documentId))!.Content);
            // An explicit request for cleanup must still report the refusal to the caller.
            await Assert.ThrowsAsync<TextRagRepository.RetentionQueryFailure>(() => deferred.ReclaimUnusedDataAsync());
        }
        const string progress = "SELECT VALUE payload FROM text_revision:legacy; SELECT VALUE complete FROM type::record('text_source_metadata',$marker);";
        var beforeRetry = await db.RawQuery(progress, parameters);
        beforeRetry.EnsureAllOks();
        Assert.Equal(["previous"], beforeRetry.GetValue<string[]>(0)!);
        Assert.Empty(beforeRetry.GetValue<bool[]>(1)!);
        (await db.RawQuery("REMOVE EVENT reject_deletion ON TABLE text_revision;")).EnsureAllOks();
        await using var retried = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: source);
        Assert.Null(retried.AutomaticRetentionError);
        Assert.Null(retried.AutomaticRetentionNotice);
        var afterRetry = await db.RawQuery(progress, parameters);
        afterRetry.EnsureAllOks();
        Assert.Empty(afterRetry.GetValue<string[]>(0)!);
        Assert.Equal([true], afterRetry.GetValue<bool[]>(1)!);
    }

    [ServerFact]
    public async Task AutoUpgradeDefersBetweenBatchesWithoutBlockingReadsAndResumesOnNextOpen()
    {
        await using var f = await ServerFixture.StartAsync();
        const string source = "constrained-PC";
        await using var raw = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        var state = new TextDocumentState { Key = "kept", DataSourceName = source, RevisionId = "current", Original = "checkpoint" };
        (await raw.RawQuery("""
            CREATE text_document:kept SET payload=$payload, data_source_name=$source, source_key_version=1;
            FOR $key IN $keys { CREATE type::record('text_revision',$key) SET payload='legacy', data_source_name=$source, source_key_version=1 RETURN NONE; };
            """, new Dictionary<string, object?> { ["source"] = source, ["payload"] = JsonSerializer.Serialize(state),
                ["keys"] = Enumerable.Range(0, 70).Select(i => $"stale-{i}").ToArray() })).EnsureAllOks();
        var checks = 0;
        await using (var paused = await TextRagRepository.OpenAsync(f.Manager.Connection!, default, source, () => ++checks == 1))
        {
            Assert.NotNull(paused.AutomaticRetentionNotice);
            Assert.Null(paused.AutomaticRetentionError);
            Assert.Equal("checkpoint", (await paused.GetAsync("kept", default))!.Original);
            await Assert.ThrowsAsync<IOException>(() => paused.ReclaimUnusedDataAsync());
        }
        var partial = await raw.RawQuery("SELECT VALUE record::id(id) FROM text_revision; SELECT VALUE complete FROM text_source_metadata WHERE complete=true;");
        partial.EnsureAllOks(); Assert.Equal(6, partial.GetValue<string[]>(0)!.Length); Assert.Empty(partial.GetValue<bool[]>(1)!);
        await using var resumed = await TextRagRepository.OpenAsync(f.Manager.Connection!, default, source, () => true);
        Assert.Equal("checkpoint", (await resumed.GetAsync("kept", default))!.Original);
        var complete = await raw.RawQuery("SELECT VALUE record::id(id) FROM text_revision; SELECT VALUE complete FROM text_source_metadata WHERE complete=true;");
        complete.EnsureAllOks(); Assert.Empty(complete.GetValue<string[]>(0)!); Assert.Equal([true], complete.GetValue<bool[]>(1)!);
    }

    [ServerFact]
    public async Task ExistingIndexUpgradesWithoutAiAndKeepsUnpublishedAndForeignData()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var root = Path.Combine(fixture.Root, "docs");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "guide.txt");
        await File.WriteAllTextAsync(path, "公開資料：花壇の水やりは朝に行います。");
        var settings = TextRagTests.Settings(root);
        var source = settings.DataSourceName;
        var ai = new FakeTextAi();
        await using var raw = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application");
        TextDocumentState original;
        await using (var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, dataSourceName: source))
        {
            using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            original = Assert.Single(await repo.DocumentsAsync(default));
            var failed = original with { Key = "incomplete", DocumentId = Guid.NewGuid(), Path = Path.Combine(root, "incomplete.txt"),
                RevisionId = "inflight", Status = "failed", ProcessingNotices = ["code_description_structural"] };
            await repo.SaveAsync(failed, default);
            await repo.StageChunkAsync(failed, 0, UnitVector(failed.Dimension), default);
            // Simulate 0.26.9's duplicate checkpoints and inactive historical vector.
            var table = TextRagRepository.Table(original.SpaceId!);
            (await raw.RawQuery($$"""
                DELETE type::record('text_source_metadata',$marker);
                FOR $rid IN $stale { CREATE type::record('text_revision',$rid) SET payload=$payload, data_source_name=$source, source_key_version=1 RETURN NONE; };
                CREATE text_revision:other SET payload='keep', data_source_name='Other source', source_key_version=1;
                CREATE text_document:withdrawn SET payload=$withdrawn, data_source_name=$source, source_key_version=1;
                CREATE {{table}}:old SET document_key=$document, revision='retired', data_source_name=$source, active=false;
                CREATE {{table}}:cache SET document_key='incomplete', revision='previous', data_source_name=$source, active=false;
                CREATE {{table}}:other SET document_key=$document, revision='foreign', data_source_name='Other source', active=false;
                """, new Dictionary<string, object?> { ["marker"] = "retention_v1_" + TextRagSettings.Hash(source),
                    ["stale"] = Enumerable.Range(0, 67).Select(i => $"stale-{i}").ToArray(),
                    ["payload"] = JsonSerializer.Serialize(original), ["document"] = original.Key, ["source"] = source,
                    ["withdrawn"] = JsonSerializer.Serialize(original with { Key = "withdrawn", DocumentId = Guid.NewGuid(), Path = Path.Combine(root, "ignored.txt"),
                        RevisionId = "withdrawn", Status = "deleted", WithdrawalReason = "ignored" }) })).EnsureAllOks();
        }
        var priorCalls = (ai.EmbeddingCalls, ai.TransformCalls);
        await using var upgraded = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, dataSourceName: source);
        var vectorTable = TextRagRepository.Table(original.SpaceId!);
        var retained = await raw.RawQuery($"SELECT VALUE revision FROM {vectorTable}; SELECT VALUE data_source_name FROM text_revision;");
        retained.EnsureAllOks();
        var rows = retained.GetValue<string[]>(0)!;
        Assert.Contains(original.RevisionId, rows);
        Assert.Contains("inflight", rows); Assert.Contains("previous", rows);
        Assert.Contains("foreign", rows); Assert.DoesNotContain("retired", rows);
        Assert.Equal(["Other source"], retained.GetValue<string[]>(1)!);
        var withdrawn = await upgraded.GetAsync("withdrawn", default);
        Assert.Equal("ignored", withdrawn!.WithdrawalReason);
        Assert.Empty(withdrawn.Original); Assert.Null(withdrawn.Chunks);
        var statuses = await upgraded.DocumentStatusesAsync(default);
        Assert.Empty(Assert.Single(statuses, d => d.Status == "deleted").ProcessingNotices);
        Assert.Equal(["code_description_structural"], Assert.Single(statuses, d => d.Status == "failed").ProcessingNotices);
        Assert.All(statuses, d => Assert.Empty(d.Original));
        var recovered = await upgraded.GetAsync("incomplete", default);
        Assert.Equal("failed", recovered!.Status);
        Assert.Equal(original.Original, recovered.Original);
        using var retrieval = new TextRagService(upgraded, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(priorCalls, (ai.EmbeddingCalls, ai.TransformCalls));
        Assert.Equal(original.Original, (await retrieval.FetchAsync(original.DocumentId))!.Content);
        Assert.Equal(original.DocumentId, Assert.Single(await retrieval.SearchAsync("水やり", 1)).DocumentId);
        await upgraded.ReclaimUnusedDataAsync();
    }

    [ServerFact]
    public async Task PublishingReclaimsPriorSpacesAndWithdrawalReleasesContentButPreservesIdentity()
    {
        await using var f = await ServerFixture.StartAsync();
        var root = Path.Combine(f.Root, "docs"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "guide.txt"); await File.WriteAllTextAsync(path, "original guide");
        var settings = TextRagTests.Settings(root);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: settings.DataSourceName);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, Path.Combine(f.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var old = Assert.Single(await repo.DocumentsAsync(default));
        var space = TextRagSettings.Hash("replacement-model");
        await repo.EnsureSpaceAsync(space, old.Dimension, default);
        var replacement = old with { SpaceId = space, RevisionId = "replacement" };
        foreach (var i in Enumerable.Range(0, replacement.EmbeddedChunks)) await repo.StageChunkAsync(replacement, i, UnitVector(old.Dimension), default);
        await repo.PublishAsync(replacement, default);
        await using var raw = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        var before = await raw.RawQuery($"SELECT VALUE revision FROM {TextRagRepository.Table(old.SpaceId!)}; SELECT VALUE revision FROM {TextRagRepository.Table(space)}; SELECT VALUE id FROM text_revision;");
        before.EnsureAllOks(); Assert.Empty(before.GetValue<string[]>(0)!); Assert.Contains("replacement", before.GetValue<string[]>(1)!); Assert.Empty(before.GetValue<string[]>(2)!);
        File.Delete(path);
        Assert.Equal(1, (await service.ScanAsync()).Deleted);
        var withdrawn = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(old.DocumentId, withdrawn.DocumentId); Assert.Equal(path, withdrawn.Path); Assert.Equal("deleted", withdrawn.Status);
        Assert.Empty(withdrawn.Original); Assert.Null(withdrawn.Chunks); Assert.Null(withdrawn.Parents);
        var after = await raw.RawQuery($"SELECT VALUE revision FROM {TextRagRepository.Table(space)};");
        after.EnsureAllOks(); Assert.Empty(after.GetValue<string[]>(0)!);
    }
    private static float[] UnitVector(int dimension) { var vector = new float[dimension]; vector[0] = 1; return vector; }
}
