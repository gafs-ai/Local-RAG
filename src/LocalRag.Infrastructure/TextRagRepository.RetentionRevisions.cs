namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    private async Task CollectLegacyRevisionsAsync(RetentionRun run, Queue<string> deferredRevisions)
    {
        // Advance past a deferred revision so it cannot occupy every subsequent page.
        string? after = null;
        while (true)
        {
            run.CheckHeadroom();
            var filter = after is null ? "" : "AND id > type::record('text_revision',$after)";
            var page = await run.QueryAsync("list_revisions",
                $"SELECT VALUE record::id(id) FROM (SELECT id FROM text_revision WHERE data_source_name=$source {filter} ORDER BY id LIMIT 64);",
                new() { ["source"] = DataSourceName, ["after"] = after }).ConfigureAwait(false);
            var revisions = page.GetValue<string[]>(0) ?? [];
            if (revisions.Length == 0) return;

            try
            {
                await DeleteLegacyRevisionBatchAsync(run, revisions).ConfigureAwait(false);
            }
            catch (RetentionQueryFailure error) when (error.IsConflict)
            {
                if (revisions.Length == 1) deferredRevisions.Enqueue(revisions[0]);
                else
                {
                    // The transaction rolled back. Resolve its members separately to isolate the busy ones.
                    foreach (var revision in revisions)
                    {
                        try { await DeleteLegacyRevisionAsync(run, revision).ConfigureAwait(false); }
                        catch (RetentionQueryFailure conflict) when (conflict.IsConflict) { deferredRevisions.Enqueue(revision); }
                    }
                }
            }
            after = revisions[^1];
        }
    }

    private Task DeleteLegacyRevisionAsync(RetentionRun run, string revision)
    {
        run.CheckHeadroom();
        return DeleteLegacyRevisionBatchAsync(run, [revision]);
    }

    private Task DeleteLegacyRevisionBatchAsync(RetentionRun run, string[] revisions) =>
        run.QueryAsync("delete_revisions", """
            BEGIN TRANSACTION;
            FOR $revision IN $revisions { DELETE type::record('text_revision',$revision) WHERE data_source_name=$source RETURN NONE; };
            COMMIT TRANSACTION;
            """, new() { ["source"] = DataSourceName, ["revisions"] = revisions }, rowScoped: true);
}
