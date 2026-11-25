using LocalRag.Core;
using LocalRag.Infrastructure;
using SurrealDb.Net;
using SurrealDb.Net.Models.Errors;
using SurrealDb.Net.Models.Response;

namespace LocalRag.Tests;

public sealed class RetentionConflictRetryTests
{
    private const string Source = "retry-PC";
    private const string Busy = "a-busy";
    private const string Next = "b-next";
    private static readonly string Space = TextRagSettings.Hash("retry-space");
    private static readonly string Table = TextRagRepository.Table(Space);

    [Theory]
    [InlineData(RpcErrorKind.Query, "Cannot COMMIT: Transaction conflict: Transaction write conflict. This transaction can be retried", "conflict")]
    [InlineData(RpcErrorKind.Query, "There was a problem with the key-value store: Transaction conflict: Transaction write conflict. This transaction can be retried", "conflict")]
    [InlineData(RpcErrorKind.Thrown, "Transaction conflict: user-supplied text", "thrown")]
    [InlineData(RpcErrorKind.Validation, "Cannot COMMIT: Transaction conflict: user-supplied text", "validation")]
    [InlineData(RpcErrorKind.Query, "Error while processing event sample: Transaction conflict", "event")]
    public void RecognizesDatabaseConflictsWithoutRetryingUserSuppliedErrors(RpcErrorKind kind, string detail, string expected) =>
        Assert.Equal(expected, TextRagRepository.RetentionErrorCategory(kind, detail));

    [ServerFact]
    public Task TransientConflictRetriesTheBatchBeforeMovingToTheNextDocument() => WithIndexAsync(async (repo, db) =>
    {
        var successfulSweeps = new List<string>();
        var injected = false;
        var waits = new List<TimeSpan>();
        async Task<SurrealDbResponse> Query(string phase, string sql, Dictionary<string, object?>? p, CancellationToken ct)
        {
            if (phase != "sweep_document") return await repo.RetentionQueryAsync(phase, sql, p, ct);
            var document = (string)p!["document"]!;
            if (!injected)
            {
                Assert.Equal(Busy, document);
                injected = true;
                throw Conflict(phase);
            }
            var result = await repo.RetentionQueryAsync(phase, sql, p, ct);
            successfulSweeps.Add(document);
            return result;
        }

        await repo.RunAutomaticRetentionAsync(default, null, Query, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        Assert.Equal([Busy, Next], successfulSweeps);
        Assert.NotEmpty(waits);
        Assert.Equal(TimeSpan.FromMilliseconds(250), waits[0]);
        Assert.Equal(["current"], await RevisionsAsync(db, Busy));
        Assert.Equal(["current"], await RevisionsAsync(db, Next));
        Assert.True(await CompletedAsync(db));
    });

    [ServerFact]
    public Task RepeatedConflictDefersOnlyTheBusyDocumentAndRechecksItsCurrentRevision() => WithIndexAsync(async (repo, db) =>
    {
        const string intermittent = "c-intermittent";
        await AddDocumentAsync(repo, db, intermittent);
        var rounds = 0;
        var busyAttempts = 0;
        var nextSuccessfulSweeps = 0;
        var progress = new DeferredCounts();
        async Task<SurrealDbResponse> Query(string phase, string sql, Dictionary<string, object?>? p, CancellationToken ct)
        {
            if (phase == "sweep_document")
            {
                switch (p!["document"])
                {
                    case Busy:
                        busyAttempts++;
                        if (rounds < 2) throw Conflict(phase);
                        break;
                    case intermittent when rounds == 0: throw Conflict(phase);
                }
            }
            var result = await repo.RetentionQueryAsync(phase, sql, p, ct);
            if (phase == "sweep_document" && Equals(p!["document"], Next)) nextSuccessfulSweeps++;
            return result;
        }

        async Task Delay(TimeSpan wait, CancellationToken ct)
        {
            if (wait < TimeSpan.FromSeconds(30)) return;
            Assert.InRange(++rounds, 1, 2);
            Assert.Equal(rounds * 3, busyAttempts);
            Assert.Equal(1, nextSuccessfulSweeps);
            Assert.Equal(["current"], await RevisionsAsync(db, Next));
            Assert.Equal(["current", "old"], await RevisionsAsync(db, Busy));
            string[] expected = rounds == 1 ? ["current", "old"] : ["current"];
            Assert.Equal(expected, await RevisionsAsync(db, intermittent));
            Assert.False(await CompletedAsync(db));
            if (rounds == 2)
            {
                // The vector previously eligible for deletion now belongs to the current revision.
                await repo.SaveAsync(Document(Busy) with { RevisionId = "old" }, ct);
            }
        }

        await repo.RunAutomaticRetentionAsync(default, progress, Query, Delay);
        Assert.Equal(2, rounds);
        Assert.Equal(1, nextSuccessfulSweeps);
        Assert.Contains(2, progress.Counts);
        Assert.Equal(0, progress.Counts.Last());
        Assert.Equal(["old"], await RevisionsAsync(db, Busy));
        Assert.Equal(["old"], await RevisionsAsync(db, Busy, "other-PC"));
        Assert.True(await CompletedAsync(db));
    });

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellationInterruptsTheShortOrDeferredWaitAndLeavesTheMarkerUnset(bool deferredWait) => WithIndexAsync(async (repo, db) =>
    {
        using var cancel = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SurrealDbResponse> Query(string phase, string sql, Dictionary<string, object?>? p, CancellationToken ct)
        {
            if (phase == "sweep_document" && (string)p!["document"]! == Busy) throw Conflict(phase);
            return repo.RetentionQueryAsync(phase, sql, p, ct);
        }
        Task Delay(TimeSpan time, CancellationToken ct)
        {
            if (deferredWait && time < TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            waiting.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        var run = repo.RunAutomaticRetentionAsync(cancel.Token, null, Query, Delay);
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.False(await CompletedAsync(db));
        Assert.Equal(["current", "old"], await RevisionsAsync(db, Busy));
        string[] expected = deferredWait ? ["current"] : ["current", "old"];
        Assert.Equal(expected, await RevisionsAsync(db, Next));
    });

    [ServerFact]
    public Task PersistentMarkerConflictCapsTheRetryWaitAndRecoversWithoutReopeningTheRepository() => WithIndexAsync(async (repo, db) =>
    {
        var writes = 0;
        var delays = new List<TimeSpan>();
        Task<SurrealDbResponse> Query(string phase, string sql, Dictionary<string, object?>? p, CancellationToken ct)
        {
            // Six full rounds of contention exercise the maximum delay twice.
            if (phase == "write_marker" && ++writes <= 18) throw Conflict(phase);
            return repo.RetentionQueryAsync(phase, sql, p, ct);
        }
        await repo.RunAutomaticRetentionAsync(default, null, Query, (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        });
        Assert.Equal(19, writes);
        Assert.Equal([30, 60, 120, 240, 300, 300],
            delays.Where(wait => wait >= TimeSpan.FromSeconds(30)).Select(wait => (int)wait.TotalSeconds));
        Assert.True(await CompletedAsync(db));
    });

    private static TextRagRepository.RetentionQueryFailure Conflict(string phase) => new(phase, "conflict");
    private static TextDocumentState Document(string key) => new()
    {
        Key = key, DataSourceName = Source, Status = "ready", SpaceId = Space, RevisionId = "current"
    };

    private static async Task WithIndexAsync(Func<TextRagRepository, SurrealDbClient, Task> exercise)
    {
        await using var f = await ServerFixture.StartAsync();
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!, dataSourceName: Source, deferAutomaticRetention: true);
        await using var db = await SurrealConnection.OpenAsync(f.Manager.Connection!, "application");
        await repo.EnsureSpaceAsync(Space, 2, default);
        await AddDocumentAsync(repo, db, Busy);
        await AddDocumentAsync(repo, db, Next);
        (await db.RawQuery($"CREATE {Table}:foreign SET document_key=$key, data_source_name='other-PC', revision='old', embedding=[0f,1f];",
            new Dictionary<string, object?> { ["key"] = Busy })).EnsureAllOks();
        await exercise(repo, db);
    }

    private static async Task AddDocumentAsync(TextRagRepository repo, SurrealDbClient db, string key)
    {
        await repo.SaveAsync(Document(key), default);
        (await db.RawQuery($$"""
            CREATE type::record('{{Table}}',$old) SET document_key=$key, data_source_name=$source, revision='old', embedding=[0f,1f];
            CREATE type::record('{{Table}}',$live) SET document_key=$key, data_source_name=$source, revision='current', embedding=[1f,0f];
            """, new Dictionary<string, object?> { ["old"] = key + "-old", ["live"] = key + "-live", ["key"] = key, ["source"] = Source })).EnsureAllOks();
    }

    private static async Task<string[]> RevisionsAsync(SurrealDbClient db, string key, string source = Source)
    {
        var result = await db.RawQuery($"SELECT VALUE revision FROM {Table} WHERE document_key=$key AND data_source_name=$source;",
            new Dictionary<string, object?> { ["key"] = key, ["source"] = source });
        result.EnsureAllOks();
        return result.GetValue<string[]>(0)!.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<bool> CompletedAsync(SurrealDbClient db)
    {
        var result = await db.RawQuery("SELECT VALUE complete FROM type::record('text_source_metadata',$marker) WHERE data_source_name=$source;",
            new Dictionary<string, object?> { ["marker"] = "retention_v1_" + TextRagSettings.Hash(Source), ["source"] = Source });
        result.EnsureAllOks();
        return result.GetValue<bool[]>(0)?.SingleOrDefault() == true;
    }

    private sealed class DeferredCounts : IProgress<int>
    {
        public List<int> Counts { get; } = [];
        public void Report(int value) => Counts.Add(value);
    }
}
