using LocalRag.Core;
using LocalRag.Infrastructure;
using SurrealDb.Net.Models.Response;

namespace LocalRag.Tests;

public sealed class LegacyRevisionConflictTests
{
    [ServerFact]
    public async Task BusyLegacyRevisionsDoNotBlockLaterPagesOrDocumentsAndAreRetriedInPlace()
    {
        const string source = "legacy-retry-PC";
        const string busy = "legacy-000";
        const string moved = "legacy-001";
        await using var fixture = await ServerFixture.StartAsync();
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, dataSourceName: source, deferAutomaticRetention: true);
        await using var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application");
        var keys = Enumerable.Range(0, 68).Select(i => $"legacy-{i:D3}").ToArray();
        var parameters = new Dictionary<string, object?> { ["source"] = source, ["keys"] = keys, ["move"] = moved };
        (await db.RawQuery("""
            FOR $key IN $keys { CREATE type::record('text_revision',$key) SET data_source_name=$source, source_key_version=1 RETURN NONE; };
            """, parameters)).EnsureAllOks();
        await repo.SaveAsync(new TextDocumentState { Key = "today", DataSourceName = source, RevisionId = "current", Status = "processing" }, default);

        var documentVisited = false;
        var released = false;
        var attempts = 0;
        Task<SurrealDbResponse> Query(string phase, string sql, Dictionary<string, object?>? args, CancellationToken token)
        {
            if (phase == "sweep_document") documentVisited = true;
            if (phase == "delete_revisions" && !released &&
                ((string[])args!["revisions"]!).Any(key => key == busy || key == moved))
            {
                attempts++;
                throw new TextRagRepository.RetentionQueryFailure(phase, "conflict");
            }
            return repo.RetentionQueryAsync(phase, sql, args, token);
        }

        async Task Delay(TimeSpan _, CancellationToken token)
        {
            if (!documentVisited || released) return;
            var remaining = await db.RawQuery("SELECT VALUE record::id(id) FROM text_revision WHERE data_source_name=$source;", parameters, token);
            remaining.EnsureAllOks();
            Assert.Equal([busy, moved], remaining.GetValue<string[]>(0)!.Order(StringComparer.Ordinal).ToArray());
            // A reclassified record must survive a later retry of the old owner's queue.
            (await db.RawQuery("UPDATE type::record('text_revision',$move) SET data_source_name='Other';", parameters, token)).EnsureAllOks();
            released = true;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await repo.RunAutomaticRetentionAsync(timeout.Token, null, Query, Delay);
        Assert.True(released);
        Assert.True(attempts > 3);
        var final = await db.RawQuery("""
            SELECT VALUE record::id(id) FROM text_revision;
            SELECT VALUE complete FROM text_source_metadata WHERE data_source_name=$source AND complete=true;
            """, parameters);
        final.EnsureAllOks();
        Assert.Equal([moved], final.GetValue<string[]>(0)!);
        Assert.Equal([true], final.GetValue<bool[]>(1)!);
    }
}
