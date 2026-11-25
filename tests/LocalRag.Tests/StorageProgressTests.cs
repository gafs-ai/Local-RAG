using LocalRag.Core;
using LocalRag.Infrastructure;
using Xunit.Abstractions;

namespace LocalRag.Tests;

public class StorageProgressTests(ITestOutputHelper log)
{
    [Fact]
    public void OnlyExplicitRetriableCommitConflictsTriggerTheRetryPolicy()
    {
        const string conflict = "Cannot COMMIT: Transaction conflict: Transaction write conflict. This transaction can be retried";
        const string rollback = "The query was not executed due to a failed transaction";
        Assert.True(TextRagRepository.IsTransactionConflict([rollback, conflict]));
        Assert.False(TextRagRepository.IsTransactionConflict([rollback]));
        Assert.False(TextRagRepository.IsTransactionConflict([conflict, "Permission denied"]));
        Assert.False(TextRagRepository.IsTransactionConflict([conflict, null]));
        Assert.Equal("transient", IngestionRetry.Kind(new PipelineValidationException("db_transaction_conflict", "conflict")));
    }

    [ServerFact]
    public async Task PublishingSurvivesDeferredVectorDeletionButStillReportsEssentialWriteErrors()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var connection = fixture.Manager.Connection!;
        await using var db = await SurrealConnection.OpenAsync(connection, "application");
        await using var repo = await TextRagRepository.OpenAsync(connection);
        var space = TextRagSettings.Hash("publish-with-deferred-deletion");
        var table = TextRagRepository.Table(space);
        await repo.EnsureSpaceAsync(space, 2, default);
        var state = new TextDocumentState { Key = "doc", Original = "message", RevisionId = "first",
            DocumentId = Guid.NewGuid(), Dimension = 2, SpaceId = space, Chunks = [new(1, 1, "text")], EmbeddedChunks = 1 };
        await repo.StageChunkAsync(state, 0, [1, 0], default);
        await repo.PublishAsync(state, default);
        async Task Run(string sql) => (await db.RawQuery(sql)).EnsureAllOks();
        async Task<string[]> Revisions(string filter)
        {
            var r = await db.RawQuery($"SELECT VALUE revision FROM {table} {filter};");
            r.EnsureAllOks();
            return r.GetValue<string[]>(0)!;
        }
        await Run($"DEFINE EVENT no_delete ON TABLE {table} WHEN $event='DELETE' THEN {{ THROW 'simulated cleanup failure' }};");
        var replacement = state with { RevisionId = "second" };
        await repo.StageChunkAsync(replacement, 0, [1, 0], default);
        await repo.PublishAsync(replacement, default);
        Assert.Equal("second", (await repo.GetAsync(state.Key, default))!.RevisionId);
        Assert.Equal(["second"], await Revisions("WHERE active=true"));
        Assert.Equal(["first"], await Revisions("WHERE active=false"));
        await Run($"REMOVE EVENT no_delete ON TABLE {table};");
        await repo.ReclaimUnusedDataAsync();
        Assert.Empty(await Revisions("WHERE active=false"));

        await Run("DEFINE EVENT reject_publication ON TABLE text_document WHEN $event='UPDATE' THEN { THROW 'simulated publication failure' };");
        var refused = state with { RevisionId = "third" };
        await repo.StageChunkAsync(refused, 0, [1, 0], default);
        await Assert.ThrowsAsync<SurrealDb.Net.Exceptions.Response.ResponseUnsuccessfulException>(() => repo.PublishAsync(refused, default));
        Assert.Equal(["second"], await Revisions("WHERE active=true"));
        Assert.Equal("second", (await repo.GetAsync(state.Key, default))!.RevisionId);
    }

    [ServerFact]
    public async Task DescriptionAndWordCheckpointsAppendOnlyNewOutputAndRestoreInOrder()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var connection = fixture.Manager.Connection!;
        var body = new string('x', 50_000);
        var function = new SourceCodeUnit("f", null, "function", "f", 1, 1, body);
        var parent = new TextParentChunk("p", 1, 5, body, "chapter");
        var snapshot = new TextDocumentState { Key = "deltas-test", RevisionId = "revision",
            Original = body, CodeUnits = [function], Parents = [parent], ParentEmbeddings = [new(parent.Id)] };
        string first = new('a', 1000);
        string second = new('b', 1000);
        await using (var repo = await TextRagRepository.OpenAsync(connection))
        {
            await repo.SaveAsync(snapshot, default);
            var described = function with { Descriptions = [first], DescribedCharacters = 5 };
            await repo.SaveCodeProgressAsync(snapshot, 0, 0, function, described, default);
            await repo.SaveCodeProgressAsync(snapshot, 0, 1, described, described with { Descriptions = [first, second],
                DescribedCharacters = 10, DescriptionCompleted = true }, default);
            var initial = snapshot.ParentEmbeddings[0];
            var step = initial with { ReductionInput = body, Reduced = [first], Offset = 100 };
            await repo.SaveParentProgressAsync(snapshot, 0, 0, parent, initial, step, default);
            await repo.SaveParentProgressAsync(snapshot, 0, 1, parent, step, step with { Reduced = [first, second], Offset = 200 }, default);
            WordRepairProgress word = new(2, [first], []);
            await repo.SaveWordProgressAsync(snapshot, null, word, default);
            await repo.SaveWordProgressAsync(snapshot, word, new(4, [first, second], []), default);
        }
        await using var raw = await SurrealConnection.OpenAsync(connection, "application");
        var rows = await raw.RawQuery("SELECT VALUE payload FROM text_work_progress;");
        rows.EnsureAllOks();
        var deltas = rows.GetValue<string[]>(0)!;
        Assert.Equal(6, deltas.Length);
        Assert.All(deltas, row => { Assert.DoesNotContain(body, row); Assert.False(row.Contains(first) && row.Contains(second)); });
        await using var reopened = await TextRagRepository.OpenAsync(connection);
        var restored = (await reopened.GetAsync(snapshot.Key, default))!;
        Assert.Equal([first, second], Assert.Single(restored.CodeUnits!).Descriptions);
        Assert.True(restored.CodeUnits![0].DescriptionCompleted);
        Assert.Equal([first, second], Assert.Single(restored.ParentEmbeddings).Reduced);
        Assert.Equal(body, restored.ParentEmbeddings[0].ReductionInput);
        Assert.Equal([first, second], restored.WordRepair!.Blocks);
        Assert.Equal(4, restored.WordRepair.NextBlock);
        await reopened.SaveAsync(restored, default);
        var cleared = await raw.RawQuery("SELECT VALUE payload FROM text_work_progress;");
        cleared.EnsureAllOks();
        Assert.Empty(cleared.GetValue<string[]>(0)!);
    }

    [ServerFact]
    public async Task StagingMoreVectorsKeepsLargeDocumentSnapshotUnchangedAndCanResumeAndPublish()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var connection = fixture.Manager.Connection!;
        await using var raw = await SurrealConnection.OpenAsync(connection, "application");
        var snapshot = new TextDocumentState
        {
            Original = new string('x', 200_000), Chunks = Enumerable.Range(1, 24).Select(n => new TextChunk(n,n,$"paragraph {n}")).ToList(),
            DocumentId = Guid.NewGuid(), Key = "progress-test", RevisionId = "revision-one", Path = "example.txt", CategoryId = "general"
        };
        var space = TextRagSettings.Hash("test-vector-space");
        async Task<string[]> Values(string sql)
        {
            var response = await raw.RawQuery(sql);
            response.EnsureAllOks();
            return response.GetValue<string[]>(0) ?? [];
        }
        const string document = "SELECT VALUE payload FROM text_document;";
        const string deltas = "SELECT VALUE payload FROM text_work_progress;";
        await using (var repo = await TextRagRepository.OpenAsync(connection))
        {
            await repo.SaveAsync(snapshot, default);
            var baseline = Assert.Single(await Values(document));
            Assert.True(baseline.Length > 200_000);
            await repo.EnsureSpaceAsync(space, 2, default);
            for (var i = 0; i < snapshot.Chunks!.Count - 1; i++)
                await repo.StageChunkAsync(snapshot with { Dimension = 2, SpaceId = space }, i, [1f,0f], checkpoint: true, default);
            Assert.Equal(baseline, Assert.Single(await Values(document)));
            var progress = Assert.Single(await Values(deltas));
            Assert.InRange(progress.Length, 1, 256);
            log.WriteLine($"23 vectors: unchanged document snapshot {System.Text.Encoding.UTF8.GetByteCount(baseline)} bytes, current checkpoint {System.Text.Encoding.UTF8.GetByteCount(progress)} bytes");
            Assert.Equal(snapshot.Chunks.Count - 1, Assert.Single(await repo.DocumentsAsync(default)).EmbeddedChunks);
        }
        await using (var repo = await TextRagRepository.OpenAsync(connection))
        {
            var restored = (await repo.GetAsync(snapshot.Key, default))!;
            Assert.Equal(23, restored.EmbeddedChunks);
            Assert.Equal(space, restored.SpaceId);
            Assert.Equal(2, restored.Dimension);
            await repo.StageChunkAsync(restored, restored.EmbeddedChunks, [1f,0f], checkpoint: true, default);
            await repo.PublishAsync(restored with { EmbeddedChunks = 24 }, default);
            Assert.Empty(await Values(deltas));
            Assert.Equal("ready", (await repo.GetAsync(snapshot.Key, default))!.Status);
            Assert.NotEmpty(await repo.SearchAsync(space, snapshot.ConfigurationId, [1f,0f], 5, default));
        }
    }
}
