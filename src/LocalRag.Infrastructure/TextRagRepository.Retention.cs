using LocalRag.Core;
using SurrealDb.Net.Exceptions;
using SurrealDb.Net.Models.Errors;
using SurrealDb.Net.Models.Response;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    public string? AutomaticRetentionNotice { get; private set; }
    public Exception? AutomaticRetentionError { get; private set; }

    private const string ClearWorkProgress =
        "DELETE text_work_progress WHERE document_key=$document AND data_source_name=$source;";
    private const string ReadVectorSpaces = "SELECT VALUE record::id(id) FROM text_space;";

    private static TextDocumentState Tombstone(TextDocumentState s) => new()
    {
        Key = s.Key, DataSourceName = s.DataSourceName, Path = s.Path, DocumentId = s.DocumentId,
        RevisionId = s.RevisionId, ConfigurationId = s.ConfigurationId, Status = "deleted",
        CategoryId = s.CategoryId, WithdrawalReason = s.WithdrawalReason
    };

    private async Task<string[]> VectorTablesAsync(CancellationToken token)
    {
        var r = await _db.RawQuery(ReadVectorSpaces, cancellationToken: token).ConfigureAwait(false);
        r.EnsureAllOks();
        return VectorTableNames(r);
    }

    // Never interpolate a table name supplied by the database without validating the digest.
    private static string[] VectorTableNames(SurrealDbResponse result) => (result.GetValue<string[]>(0) ?? []).Select(Table).ToArray();

    internal sealed class RetentionQueryFailure(string phase, string category, Exception? cause = null)
        : IOException("旧索引データを整理できませんでした。診断コード: retention." + phase + "." + category, cause)
    {
        internal string Code { get; } = "retention." + phase + "." + category;
        internal bool IsConflict { get; } = category == "conflict";
    }

    internal async Task<SurrealDbResponse> RetentionQueryAsync(string phase, string sql,
        Dictionary<string, object?>? parameters, CancellationToken token)
    {
        SurrealDbResponse result;
        try { result = await _db.RawQuery(sql, parameters, token).ConfigureAwait(false); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is SurrealDbException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            var category = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge } => "size_limit",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.InsufficientStorage } => "storage_full",
                HttpRequestException => "connection", OperationCanceledException or TimeoutException => "timeout", _ => "rpc"
            };
            throw new RetentionQueryFailure(phase, category, ex);
        }
        if (!result.HasErrors) return result;
        token.ThrowIfCancellationRequested();
        // A rolled-back transaction can report collateral failures before the failing statement.
        // Keep only a fixed error category; server error text can contain document text or paths.
        var errors = result.Errors.OfType<SurrealDbErrorResult>().ToArray();
        var categories = errors.Where(e => !(e.Details.StartsWith("The query was not executed due to", StringComparison.OrdinalIgnoreCase)
            || e.Details.StartsWith("Cannot COMMIT: the transaction was aborted", StringComparison.OrdinalIgnoreCase)))
            .Select(e => RetentionErrorCategory(e.Kind, e.Details)).ToArray();
        // A concurrent write must never mask a permission, validation, or other failure in the same response.
        throw new RetentionQueryFailure(phase, categories.FirstOrDefault(c => c != "conflict") ?? categories.FirstOrDefault() ?? "query");
    }

    internal static string RetentionErrorCategory(RpcErrorKind? kind, string detail) => kind switch
        {
            _ when detail.StartsWith("Error while processing event ", StringComparison.OrdinalIgnoreCase) => "event",
            _ when detail.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("There is not enough space on the disk", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("ディスクに十分な空き領域がありません。", StringComparison.Ordinal) => "storage_full",
            _ when detail.Equals("Payload too large", StringComparison.OrdinalIgnoreCase) || detail.Equals("Request body too large", StringComparison.OrdinalIgnoreCase) => "size_limit",
            _ when kind is not (RpcErrorKind.Thrown or RpcErrorKind.NotAllowed or RpcErrorKind.Validation)
                && (detail.StartsWith("Cannot COMMIT: Transaction conflict", StringComparison.OrdinalIgnoreCase)
                    || detail.StartsWith("Transaction conflict", StringComparison.OrdinalIgnoreCase)
                    || detail.StartsWith("There was a transaction conflict", StringComparison.OrdinalIgnoreCase)
                    || (detail.Contains(": Transaction conflict:", StringComparison.OrdinalIgnoreCase)
                        && detail.EndsWith("This transaction can be retried", StringComparison.OrdinalIgnoreCase))) => "conflict",
            _ when detail.Contains("DiskANN", StringComparison.OrdinalIgnoreCase) => "vector_index",
            RpcErrorKind.Validation => "validation", RpcErrorKind.NotFound => "missing", RpcErrorKind.NotAllowed => "denied",
            RpcErrorKind.Configuration => "configuration", RpcErrorKind.Internal => "internal", RpcErrorKind.Connection => "connection",
            RpcErrorKind.Thrown => "thrown", RpcErrorKind.Serialization => "serialization", RpcErrorKind.AlreadyExists => "exists",
            _ => "query"
        };

    private static string PurgeDocumentVectors(string[] tables, string? keepTable) =>
        string.Join(' ', tables.Select(t => $"DELETE {t} WHERE document_key=$document AND data_source_name=$source" +
            (t == keepTable ? " AND revision!=$revision;" : ";")));

    // Changing category settings is reversible. No new vectors are staged while the category is
    // excluded. Keep the last inputs available to re-enable it, outside the searchable index.
    private static string DeactivateDocumentVectors(string[] tables) => string.Join(' ', tables.Select(t =>
        $"UPDATE {t} SET active=false WHERE document_key=$document AND data_source_name=$source AND active=true RETURN NONE;"));

    private async Task UpgradeRetentionAsync(RetentionRun run, IProgress<int>? deferredDocuments)
    {
        AutomaticRetentionError = null;
        AutomaticRetentionNotice = null;
        try
        {
            var marker = new Dictionary<string, object?> { ["id"] = "retention_v1_" + TextRagSettings.Hash(DataSourceName), ["source"] = DataSourceName };
            var r = await run.QueryAsync("read_marker", "SELECT VALUE complete FROM type::record('text_source_metadata',$id) WHERE data_source_name=$source;", marker).ConfigureAwait(false);
            if (r.GetValue<bool[]>(0)?.SingleOrDefault() == true) return;
            await ReclaimUnusedDataCoreAsync(run, deferredDocuments).ConfigureAwait(false);
            await run.QueryAsync("write_marker", "UPSERT type::record('text_source_metadata',$id) SET data_source_name=$source, complete=true;", marker).ConfigureAwait(false);
        }
        catch (AutomaticRetentionSpaceException)
        {
            AutomaticRetentionNotice = "DBの空き容量を確保するため、旧索引データの自動整理を保留しました。1 GiB以上の空き容量があるときに再試行できます。";
        }
        catch (RetentionQueryFailure ex) when (!run.Token.IsCancellationRequested)
        {
            // Cleanup is optional. Required schema, ownership and normal index reads still report their own errors.
            // The completion marker is written last; every committed batch is safe to replay on the next open.
            AutomaticRetentionError = ex;
            AutomaticRetentionNotice = "DBの応答により旧索引データの自動整理を保留しました。次に索引を開くときに再試行します。";
        }
    }

    /// <summary>Reclaims legacy duplicate snapshots and superseded vectors for this data source.
    /// Incomplete documents keep all staged vectors, including older reusable embeddings.</summary>
    public Task ReclaimUnusedDataAsync(CancellationToken token = default) =>
        ReclaimUnusedDataCoreAsync(CreateRetentionRun(RetentionMode.Manual, token), null);

    private async Task ReclaimUnusedDataCoreAsync(RetentionRun run, IProgress<int>? progress)
    {
        var revisions = new Queue<string>();
        var documents = new Queue<string>();
        await CollectLegacyRevisionsAsync(run, revisions).ConfigureAwait(false);
        var tables = VectorTableNames(await run.QueryAsync("list_spaces", ReadVectorSpaces, null).ConfigureAwait(false));
        // With no vector spaces, deleted documents may still contain a legacy snapshot to shrink.
        var sweeps = tables.Length == 0 ? [VectorCleanupBatch(null)] : tables.Select(VectorCleanupBatch).ToArray();
        async Task CleanDocumentAsync(string key)
        {
            var p = new Dictionary<string, object?> { ["source"] = DataSourceName, ["document"] = key };
            foreach (var sweep in sweeps)
            {
                while (true)
                {
                    run.CheckHeadroom();
                    var result = await run.QueryAsync("sweep_document", sweep, p, rowScoped: true).ConfigureAwait(false);
                    if (result.GetValue<int>(0) < RetentionVectorBatchSize) break;
                }
            }
        }

        var pageParameters = new Dictionary<string, object?> { ["source"] = DataSourceName };
        string? after = null;
        while (true)
        {
            var cursor = after is null ? "" : "AND id > type::record('text_document',$after)";
            pageParameters["after"] = after;
            var page = await run.QueryAsync("list_documents", $"SELECT record::id(id) AS doc_key FROM text_document WHERE data_source_name=$source {cursor} ORDER BY doc_key LIMIT 64;", pageParameters).ConfigureAwait(false);
            var rows = page.GetValue<RetentionKey[]>(0) ?? [];
            if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                try { await CleanDocumentAsync(row.doc_key).ConfigureAwait(false); }
                catch (RetentionQueryFailure ex) when (ex.IsConflict)
                {
                    documents.Enqueue(row.doc_key);
                    progress?.Report(documents.Count);
                }
            }
            after = rows[^1].doc_key;
        }
        var delay = InitialDeferredRetry;
        while (documents.Count != 0 || revisions.Count != 0)
        {
            // A synchronous open and a manual compaction must release their caller on persistent contention.
            if (!run.IsBackground)
                throw new RetentionQueryFailure(documents.Count != 0 ? "sweep_document" : "delete_revisions", "conflict");
            await run.WaitAsync(delay).ConfigureAwait(false);
            await RetryQueueOnceAsync(documents, CleanDocumentAsync, progress).ConfigureAwait(false);
            await RetryQueueOnceAsync(revisions, key => DeleteLegacyRevisionAsync(run, key), null).ConfigureAwait(false);
            delay = NextDeferredRetry(delay);
        }
        run.CheckHeadroom();
    }

    internal const int RetentionVectorBatchSize = 32;

    // Each SurrealQL statement commits independently. Select IDs and mutate in the same statement
    // so a new revision, a changed owner, or a now-incomplete document is rechecked every batch.
    private static string VectorCleanupBatch(string? table)
    {
        var select = table is null ? "[]" : $$"""
            IF $snapshot.Status IN ['ready','deleted','skipped','excluded'] {
                SELECT VALUE id FROM {{table}} WHERE document_key=$document AND data_source_name=$source AND (
                    $snapshot.Status IN ['deleted','skipped'] OR ($snapshot.Status='excluded' AND active=true)
                    OR ($snapshot.Status='ready' AND (revision!=$snapshot.RevisionId OR $snapshot.SpaceId!='{{table["text_vectors_".Length..]}}'))
                ) LIMIT {{RetentionVectorBatchSize}}
            } ELSE { [] }
            """;
        return $$"""
            RETURN {
                LET $snapshot=(SELECT VALUE encoding::json::decode(payload) FROM type::record('text_document',$document) WHERE data_source_name=$source)[0];
                LET $ids={{select}};
                IF $snapshot.Status='excluded' { UPDATE $ids SET active=false RETURN NONE; } ELSE { DELETE $ids RETURN NONE; };
                IF $snapshot.Status='deleted' {
                    LET $tombstone=encoding::json::encode({
                        Key:$document, DataSourceName:$source, Path:$snapshot.Path, DocumentId:$snapshot.DocumentId,
                        RevisionId:$snapshot.RevisionId, ConfigurationId:$snapshot.ConfigurationId, Status:'deleted',
                        CategoryId:$snapshot.CategoryId, WithdrawalReason:$snapshot.WithdrawalReason
                    });
                    UPDATE type::record('text_document',$document) SET payload=$tombstone WHERE payload!=$tombstone RETURN NONE;
                };
                array::len($ids)
            };
            """;
    }

    private sealed record RetentionKey(string doc_key);
}
