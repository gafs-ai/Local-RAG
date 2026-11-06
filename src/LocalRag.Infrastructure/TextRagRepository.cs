using System.Text.Json;
using LocalRag.Core;
using SurrealDb.Net;

namespace LocalRag.Infrastructure;

public sealed record StoredTextChunk(string DocumentKey, string RevisionId, int Ordinal, int Start, int End, string Text, string? ParentId = null, string EmbeddingKind = "child");

public sealed partial class TextRagRepository : IAsyncDisposable, IMonitoringCheckpointStore
{
    private readonly SurrealDbClient _db;
    private TextRagRepository(SurrealDbClient db) => _db = db;

    public static async Task<TextRagRepository> OpenAsync(ServerConnection connection, CancellationToken token = default)
    {
        var db = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        try
        {
            (await db.RawQuery("DEFINE TABLE IF NOT EXISTS text_document SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_revision SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_space SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_monitor SCHEMALESS; DEFINE TABLE IF NOT EXISTS text_ingestion_history SCHEMALESS;", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
            return new(db);
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<TextDocumentState>> DocumentsAsync(CancellationToken token)
    {
        var result = await _db.RawQuery("SELECT VALUE payload FROM text_document;", cancellationToken: token).ConfigureAwait(false);
        result.EnsureAllOks();
        return (result.GetValue<string[]>(0) ?? []).Select(x => JsonSerializer.Deserialize<TextDocumentState>(x) ?? throw new InvalidDataException("文書状態が不正です。")).ToArray();
    }

    // The management list must not download every original, parent and code unit after
    // each completed file. Fetch only the fields displayed by the list.
    public async Task<IReadOnlyList<TextDocumentState>> DocumentStatusesAsync(CancellationToken token)
    {
        var result = await _db.RawQuery("SELECT VALUE { Path: Path, Status: Status, ConfigurationId: ConfigurationId, ErrorCode: ErrorCode, ErrorStage: ErrorStage, ErrorDetailCode: ErrorDetailCode, WithdrawalReason: WithdrawalReason, ProcessingNotices: ProcessingNotices } FROM (SELECT VALUE encoding::json::decode(payload) FROM text_document);",
            cancellationToken: token).ConfigureAwait(false);
        result.EnsureAllOks();
        return result.GetValue<TextDocumentState[]>(0) ?? [];
    }

    public async Task<TextDocumentState?> GetAsync(string key, CancellationToken token)
    {
        var result = await _db.RawQuery("SELECT VALUE payload FROM type::record('text_document', $key);", new Dictionary<string, object?> { ["key"] = key }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        var json = result.GetValue<string[]>(0)?.SingleOrDefault();
        return json is null ? null : JsonSerializer.Deserialize<TextDocumentState>(json);
    }

    public async Task SaveAsync(TextDocumentState state, CancellationToken token)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["key"] = state.Key, ["revision"] = state.RevisionId,
            ["payload"] = JsonSerializer.Serialize(state)
        };
        // Current pointer and stage checkpoint move together. Completed revision history is kept.
        (await _db.RawQuery("BEGIN TRANSACTION; UPSERT type::record('text_document', $key) SET payload = $payload; UPSERT type::record('text_revision', $revision) SET payload = $payload; COMMIT TRANSACTION;", parameters, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public async Task EnsureSpaceAsync(string space, int dimension, CancellationToken token)
    {
        var table = Table(space);
        if (dimension is < 1 or > 8192) throw new InvalidDataException("Embedding次元数が対応範囲外です。");
        var result = await _db.RawQuery($"DEFINE TABLE IF NOT EXISTS {table} SCHEMALESS; DEFINE INDEX IF NOT EXISTS embedding_idx ON {table} FIELDS embedding DISKANN DIMENSION {dimension} DIST COSINE TYPE F32; UPSERT type::record('text_space', $space) SET dimension = $dimension;",
            new Dictionary<string, object?> { ["space"] = space, ["dimension"] = dimension }, token).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public async Task StageChunkAsync(TextDocumentState state, int ordinal, float[] embedding, CancellationToken token)
    {
        var chunk = state.Chunks![ordinal];
        var payload = JsonSerializer.Serialize(new StoredTextChunk(state.Key, state.RevisionId, ordinal, chunk.Start, chunk.End, chunk.Text, chunk.ParentId, chunk.EmbeddingKind));
        var result = await _db.RawQuery($"UPSERT type::record('{Table(state.SpaceId!)}', $key) SET document_key=$document, revision=$revision, configuration=$configuration, active=false, payload=$payload, embedding=$embedding;",
            new Dictionary<string, object?>
            {
                ["key"] = state.RevisionId + "_" + ordinal, ["document"] = state.Key, ["revision"] = state.RevisionId,
                ["configuration"] = state.ConfigurationId, ["payload"] = payload, ["embedding"] = embedding
            }, token).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public async Task DeactivateAsync(TextDocumentState state, CancellationToken token)
    {
        // The document state is checked again by retrieval, independently of index eligibility.
        await SaveAsync(state with { Status = "processing" }, token).ConfigureAwait(false);
        if (state.SpaceId is not null)
            (await _db.RawQuery($"UPDATE {Table(state.SpaceId)} SET active=false WHERE document_key=$document;",
                new Dictionary<string, object?> { ["document"] = state.Key }, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public async Task PublishAsync(TextDocumentState state, CancellationToken token)
    {
        if (state.SpaceId is null || state.Chunks is null || state.EmbeddedChunks != state.Chunks.Count)
            throw new InvalidDataException("未完了の文書は公開できません。");
        if (state.Parents is not null && (state.Parents.Select(p => p.Id).Distinct().Count() != state.Parents.Count ||
            state.Chunks.Any(c => !state.Parents.Any(p => p.Id == c.ParentId && p.Start <= c.Start && p.End >= c.End))) ||
            state.Parents is null && state.Chunks.Any(c => c.ParentId is not null))
            throw new InvalidDataException("親子チャンクの対応が不正です。");
        var ready = state with { Status = "ready", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null };
        var result = await _db.RawQuery($"BEGIN TRANSACTION; UPDATE {Table(state.SpaceId)} SET active=false WHERE document_key=$document; UPDATE {Table(state.SpaceId)} SET active=true WHERE revision=$revision; UPSERT type::record('text_document', $document) SET payload=$payload; UPSERT type::record('text_revision', $revision) SET payload=$payload; COMMIT TRANSACTION;",
            new Dictionary<string, object?> { ["document"] = state.Key, ["revision"] = state.RevisionId, ["payload"] = JsonSerializer.Serialize(ready) }, token).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public async Task<IReadOnlyList<StoredTextChunk>> SearchAsync(string space, string configuration, float[] query, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        // K and EF are bounded integers, the table is a validated digest; all user text is bound as parameters.
        var result = await _db.RawQuery($"SELECT VALUE payload FROM (SELECT payload, vector::distance::knn() AS distance FROM {Table(space)} WHERE embedding <|{limit},{Math.Max(64, limit)}|> $query AND active=true AND configuration=$configuration ORDER BY distance);",
            new Dictionary<string, object?> { ["query"] = query, ["configuration"] = configuration }, token).ConfigureAwait(false);
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
        var result = await _db.RawQuery("SELECT VALUE payload FROM text_monitor:current;", cancellationToken: token).ConfigureAwait(false);
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
        (await _db.RawQuery("UPSERT text_monitor:current SET payload=$payload;",
            new Dictionary<string, object?> { ["payload"] = JsonSerializer.Serialize(checkpoint) }, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
