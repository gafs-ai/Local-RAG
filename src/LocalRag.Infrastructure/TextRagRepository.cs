using System.Text.Json;
using LocalRag.Core;
using SurrealDb.Net;

namespace LocalRag.Infrastructure;

public sealed record StoredTextChunk(string DocumentKey, string RevisionId, int Ordinal, int Start, int End, string Text, string? ParentId = null, string EmbeddingKind = "child");

public sealed partial class TextRagRepository : IAsyncDisposable, IMonitoringCheckpointStore
{
    private readonly SurrealDbClient _db;
    private readonly Func<bool> _hasMaintenanceHeadroom;
    public string DataSourceName { get; }
    private TextRagRepository(SurrealDbClient db, string dataSourceName, Func<bool> hasMaintenanceHeadroom)
    { _db = db; DataSourceName = dataSourceName; _hasMaintenanceHeadroom = hasMaintenanceHeadroom; }

    public static Task<TextRagRepository> OpenAsync(ServerConnection connection, CancellationToken token = default, string? dataSourceName = null,
        bool deferAutomaticRetention = false) =>
        OpenAsync(connection, token, dataSourceName, connection.HasMaintenanceHeadroom, deferAutomaticRetention);

    internal static async Task<TextRagRepository> OpenAsync(ServerConnection connection, CancellationToken token, string? dataSourceName,
        Func<bool> hasMaintenanceHeadroom, bool deferAutomaticRetention = false)
    {
        if (!AppSettings.ValidDataSourceName(dataSourceName ?? AppSettings.DefaultDataSourceName)) throw new ArgumentException("データソース名が不正です。");
        var db = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        try
        {
            (await db.RawQuery("DEFINE TABLE IF NOT EXISTS text_document SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_revision SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_space SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_monitor SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_ingestion_history SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_work_progress SCHEMALESS; DEFINE INDEX IF NOT EXISTS text_work_owner ON TABLE text_work_progress FIELDS document_key, data_source_name;", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
            var repository = new TextRagRepository(db, dataSourceName ?? AppSettings.DefaultDataSourceName, hasMaintenanceHeadroom);
            await repository.BackfillDataSourceNamesAsync(token).ConfigureAwait(false);
            if (!deferAutomaticRetention)
                await repository.UpgradeRetentionAsync(repository.CreateRetentionRun(RetentionMode.DuringOpen, token), null).ConfigureAwait(false);
            return repository;
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Run optional maintenance after startup. The caller must await completion or cancellation before disposing the repository.</summary>
    public Task RunAutomaticRetentionAsync(CancellationToken token, IProgress<int>? deferredDocuments = null) =>
        RunAutomaticRetentionAsync(token, deferredDocuments, RetentionQueryAsync, Task.Delay);

    public async Task<IReadOnlyList<TextDocumentState>> DocumentsAsync(CancellationToken token)
    {
        return await ReadWorkSnapshotsAsync(null, token).ConfigureAwait(false);
    }

    // The management list must not download every original, parent and code unit after
    // each completed file. Fetch only the fields displayed by the list.
    public async Task<IReadOnlyList<TextDocumentState>> DocumentStatusesAsync(CancellationToken token)
    {
        // Compact deletion records and older snapshots may omit the notices field.
        var result = await _db.RawQuery("SELECT VALUE encoding::json::encode({ State: { Path: Path, DataSourceName: DataSourceName, Status: Status, CategoryId: CategoryId, RevisionId: RevisionId, ConfigurationId: ConfigurationId, ErrorCode: ErrorCode, ErrorStage: ErrorStage, ErrorDetailCode: ErrorDetailCode, Failure: Failure, WithdrawalReason: WithdrawalReason, ProcessingNotices: ProcessingNotices ?? [] }, ProfileId: Processing.ProfileId, Version: Processing.Version }) FROM (SELECT VALUE encoding::json::decode(payload) FROM text_document WHERE data_source_name=$source);",
            new Dictionary<string, object?> { ["source"] = DataSourceName }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        return (result.GetValue<string[]>(0) ?? []).Select(json => JsonSerializer.Deserialize<ProcessingStatusRow>(json)!).Select(r => r.ProfileId is null ? r.State : r.State with
            { Processing = new() { ProfileId = r.ProfileId, Version = r.Version ?? 0 } }).ToArray();
    }

    private sealed record ProcessingStatusRow(TextDocumentState State, string? ProfileId, int? Version);

    public async Task<TextDocumentState?> GetAsync(string key, CancellationToken token)
    {
        return (await ReadWorkSnapshotsAsync(key, token).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task SaveAsync(TextDocumentState state, CancellationToken token)
    {
        if (state.DataSourceName != DataSourceName || !AppSettings.ValidDataSourceName(state.DataSourceName)) throw new InvalidDataException("データソース名が不正です。");
        if (state.Status == "deleted") state = Tombstone(state);
        var vectors = state.Status switch
        {
            "deleted" or "skipped" => PurgeDocumentVectors(await VectorTablesAsync(token).ConfigureAwait(false), null),
            "excluded" => DeactivateDocumentVectors(await VectorTablesAsync(token).ConfigureAwait(false)),
            _ => ""
        };
        var parameters = new Dictionary<string, object?>
        {
            ["document"] = state.Key, ["revision"] = state.RevisionId,
            ["payload"] = JsonSerializer.Serialize(state), ["source"] = state.DataSourceName
        };
        // The current snapshot is the resumable checkpoint. Search and recovery never read historical copies.
        (await _db.RawQuery($"BEGIN TRANSACTION; {vectors} {ClearWorkProgress} UPSERT type::record('text_document', $document) SET payload=$payload, data_source_name=$source, source_key_version=1 RETURN NONE; COMMIT TRANSACTION;", parameters, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public async Task EnsureSpaceAsync(string space, int dimension, CancellationToken token)
    {
        var table = Table(space);
        if (dimension is < 1 or > 8192) throw new InvalidDataException("Embedding次元数が対応範囲外です。");
        var result = await _db.RawQuery($"DEFINE TABLE IF NOT EXISTS {table} SCHEMALESS; DEFINE INDEX IF NOT EXISTS embedding_idx ON {table} FIELDS embedding DISKANN DIMENSION {dimension} DIST COSINE TYPE F32; DEFINE INDEX IF NOT EXISTS reusable_input_idx ON {table} FIELDS document_key, input_hash; UPSERT type::record('text_space', $space) SET dimension = $dimension;",
            new Dictionary<string, object?> { ["space"] = space, ["dimension"] = dimension }, token).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public Task StageChunkAsync(TextDocumentState state, int ordinal, float[] embedding, CancellationToken token) =>
        StageChunkAsync(state, ordinal, embedding, false, token);

    internal async Task StageChunkAsync(TextDocumentState state, int ordinal, float[] embedding, bool checkpoint, CancellationToken token)
    {
        var chunk = state.Chunks![ordinal];
        var payload = JsonSerializer.Serialize(new StoredTextChunk(state.Key, state.RevisionId, ordinal, chunk.Start, chunk.End, chunk.Text, chunk.ParentId, chunk.EmbeddingKind));
        var query = $"UPSERT type::record('{Table(state.SpaceId!)}', $key) SET document_key=$document, revision=$revision, configuration=$configuration, active=false, payload=$payload, embedding=$embedding, input_hash=$input_hash, data_source_name=$source RETURN NONE;";
        var parameters = new Dictionary<string, object?>
        {
            ["source"] = DataSourceName, ["key"] = state.RevisionId + "_" + ordinal, ["document"] = state.Key, ["revision"] = state.RevisionId,
            ["configuration"] = state.ConfigurationId, ["payload"] = payload, ["embedding"] = embedding, ["input_hash"] = TextRagSettings.Hash(chunk.Text)
        };
        if (checkpoint)
        {
            AddProgressParameters(parameters, state, "embedding", 0, new VectorProgress(ordinal + 1, state.Dimension, state.SpaceId!));
            query = "BEGIN TRANSACTION; " + query + WriteWorkProgress + " COMMIT TRANSACTION;";
        }
        var result = await _db.RawQuery(query, parameters, token).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public async Task DeactivateAsync(TextDocumentState state, CancellationToken token)
    {
        // The document state is checked again by retrieval, independently of index eligibility.
        await SaveAsync(state with { Status = "processing" }, token).ConfigureAwait(false);
        if (state.SpaceId is not null)
            (await _db.RawQuery($"UPDATE {Table(state.SpaceId)} SET active=false WHERE document_key=$document AND data_source_name=$source AND active=true RETURN NONE;",
                new Dictionary<string, object?> { ["document"] = state.Key, ["source"] = DataSourceName }, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public async Task PublishAsync(TextDocumentState state, CancellationToken token)
    {
        if (state.DataSourceName != DataSourceName || !AppSettings.ValidDataSourceName(state.DataSourceName)) throw new InvalidDataException("データソース名が不正です。");
        if (state.SpaceId is null || state.Chunks is null || state.EmbeddedChunks != state.Chunks.Count)
            throw new InvalidDataException("未完了の文書は公開できません。");
        if (state.Parents is not null && (state.Parents.Select(p => p.Id).Distinct().Count() != state.Parents.Count ||
            state.Chunks.Any(c => !state.Parents.Any(p => p.Id == c.ParentId && p.Start <= c.Start && p.End >= c.End))) ||
            state.Parents is null && state.Chunks.Any(c => c.ParentId is not null))
            throw new InvalidDataException("親子チャンクの対応が不正です。");
        var ready = state with { Status = "ready", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null, Failure = null };
        var tables = await VectorTablesAsync(token).ConfigureAwait(false);
        var current = Table(state.SpaceId);
        var parameters = new Dictionary<string, object?> { ["document"] = state.Key, ["revision"] = state.RevisionId, ["source"] = state.DataSourceName };
        var publication = new Dictionary<string, object?>(parameters) { ["payload"] = JsonSerializer.Serialize(ready) };
        var deactivate = string.Join(' ', tables.Select(t => $"UPDATE {t} SET active=false WHERE active=true AND document_key=$document AND data_source_name=$source"
            + (t == current ? " AND revision!=$revision RETURN NONE;" : " RETURN NONE;")));
        await CommitPublicationAsync($"BEGIN; {deactivate} UPDATE {current} SET active=true WHERE document_key=$document AND data_source_name=$source AND revision=$revision RETURN NONE; {ClearWorkProgress} UPSERT type::record('text_document',$document) SET payload=$payload, data_source_name=$source, source_key_version=1 RETURN NONE; COMMIT;",
            publication, token).ConfigureAwait(false);
        if (!_hasMaintenanceHeadroom()) return;
        // Stale vectors are already invisible. A failed DELETE must never revoke a successful publication.
        // Recheck the revision in the cleanup transaction so a new writer can safely start in the meantime.
        var purge = PurgeDocumentVectors(tables, current);
        var cleanup = $"BEGIN; LET $current = (SELECT VALUE encoding::json::decode(payload) FROM type::record('text_document',$document) WHERE data_source_name=$source)[0]; IF $current.RevisionId=$revision AND $current.Status='ready' {{ {purge} }}; COMMIT;";
        try
        {
            var result = await _db.RawQuery(cleanup, parameters, token).ConfigureAwait(false);
            if (result.HasErrors) System.Diagnostics.Trace.TraceWarning("旧ベクトルの削除を次の公開またはDB整理に繰り延べました。");
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or TimeoutException or OperationCanceledException or SurrealDb.Net.Exceptions.SurrealDbException)
        { System.Diagnostics.Trace.TraceWarning("DBから応答を得られなかったため旧ベクトルの削除を繰り延べました。"); }
    }

    private async Task CommitPublicationAsync(string query, Dictionary<string, object?> parameters, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await _db.RawQuery(query, parameters, token).ConfigureAwait(false);
            if (!result.HasErrors) return;
            var errors = result.Errors.Select(e => (e as SurrealDb.Net.Models.Response.SurrealDbErrorResult)?.Details).ToArray();
            if (!IsTransactionConflict(errors)) result.EnsureAllOks();
            if (attempt == 2) throw new PipelineValidationException("db_transaction_conflict", "DBの同時更新が競合しました。");
            await Task.Delay(50 << attempt, token).ConfigureAwait(false);
        }
    }

    internal static bool IsTransactionConflict(string?[] details)
    {
        static bool Conflict(string? s) => s is not null && s.StartsWith("Cannot COMMIT: Transaction conflict:", StringComparison.Ordinal)
            && s.EndsWith("This transaction can be retried", StringComparison.Ordinal);
        return details.Any(Conflict) && details.All(s => Conflict(s) || s == "The query was not executed due to a failed transaction");
    }

    public async Task<IReadOnlyList<StoredTextChunk>> SearchAsync(string space, string configuration, float[] query, int limit, CancellationToken token,
        IReadOnlyList<string>? revisions = null)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        // K and EF are bounded integers, the table is a validated digest; all user text is bound as parameters.
        var filter = revisions is null ? "configuration=$configuration" : "revision IN $revisions";
        var result = await _db.RawQuery($"SELECT VALUE payload FROM (SELECT payload, vector::distance::knn() AS distance FROM {Table(space)} WHERE embedding <|{limit},{Math.Max(64, limit)}|> $query AND active=true AND data_source_name=$source AND {filter} ORDER BY distance);",
            new Dictionary<string, object?> { ["source"] = DataSourceName, ["query"] = query, ["configuration"] = configuration, ["revisions"] = revisions?.ToArray() ?? [] }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        return (result.GetValue<string[]>(0) ?? []).Select(x => JsonSerializer.Deserialize<StoredTextChunk>(x) ?? throw new InvalidDataException("チャンクが不正です。")).ToArray();
    }

    public static string Table(string space)
    {
        if (space.Length != 64 || space.Any(ch => !char.IsAsciiHexDigit(ch))) throw new ArgumentException("検索空間IDが不正です。");
        return "text_vectors_" + space.ToLowerInvariant();
    }

    public async Task<MonitoringCheckpoint?> LoadMonitoringAsync(CancellationToken token)
    {
        var result = await _db.RawQuery("SELECT VALUE payload FROM type::record('text_monitor', $source);", new Dictionary<string, object?> { ["source"] = TextRagSettings.Hash(DataSourceName) }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        var json = result.GetValue<string[]>(0)?.SingleOrDefault();
        if (json is null) return null;
        var checkpoint = JsonSerializer.Deserialize<MonitoringCheckpoint>(json) ?? throw new InvalidDataException("監視状態が空です。");
        checkpoint.Validate();
        return checkpoint;
    }

    public async Task SaveMonitoringAsync(MonitoringCheckpoint checkpoint, CancellationToken token)
    {
        checkpoint.Validate();
        (await _db.RawQuery("UPSERT type::record('text_monitor', $source) SET payload=$payload, data_source_name=$name;",
            new Dictionary<string, object?> { ["payload"] = JsonSerializer.Serialize(checkpoint), ["source"] = TextRagSettings.Hash(DataSourceName), ["name"] = DataSourceName }, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
