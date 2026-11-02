using System.Text.Json;
using LocalRag.Core;
using SurrealDb.Net;

namespace LocalRag.Infrastructure;

/// <summary>Initial single-writer checkpoint repository. Only the owning host opens this database.</summary>
public sealed class SurrealCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly SurrealDbClient _db;
    private SurrealCheckpointStore(SurrealDbClient db) => _db = db;

    public static async Task<SurrealCheckpointStore> OpenAsync(ServerConnection connection, CancellationToken cancellationToken = default)
    {
        var db = await SurrealConnection.OpenAsync(connection, "application", cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await db.RawQuery("DEFINE TABLE IF NOT EXISTS job_checkpoint SCHEMALESS;",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            result.EnsureAllOks();
            return new(db);
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var result = await _db.RawQuery(
            "UPSERT type::record('job_checkpoint', $key) SET payload = $payload;",
            new Dictionary<string, object?> { ["key"] = checkpoint.JobId.ToString("N"), ["payload"] = JsonSerializer.Serialize(checkpoint) },
            cancellationToken).ConfigureAwait(false);
        result.EnsureAllOks();
    }

    public async Task<JobCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var result = await _db.RawQuery("SELECT VALUE payload FROM type::record('job_checkpoint', $key);",
            new Dictionary<string, object?> { ["key"] = jobId.ToString("N") }, cancellationToken).ConfigureAwait(false);
        result.EnsureAllOks();
        var values = result.GetValue<string[]>(0) ?? throw new InvalidDataException("DB応答を読み取れません。");
        return values.Length == 0 ? null : JsonSerializer.Deserialize<JobCheckpoint>(values[0]);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
