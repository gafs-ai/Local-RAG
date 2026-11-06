using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    private int _historyWrites;
    public async Task RecordHistoryAsync(IngestionHistoryEntry entry, CancellationToken token)
    {
        var result = await _db.RawQuery("CREATE type::record('text_ingestion_history', $id) SET completed_ticks = $ticks, payload = $payload, data_source_name=$source;",
            new Dictionary<string, object?> { ["id"] = entry.Id.ToString("N"), ["ticks"] = entry.CompletedAtUtc.UtcTicks,
                ["payload"] = JsonSerializer.Serialize(entry), ["source"] = DataSourceName }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        // Retain about 20,000 checks without running a full retention query for every file.
        if (Interlocked.Increment(ref _historyWrites) % 200 == 1)
            (await _db.RawQuery("DELETE text_ingestion_history WHERE data_source_name=$source AND id NOT IN (SELECT VALUE id FROM text_ingestion_history WHERE data_source_name=$source ORDER BY completed_ticks DESC, id DESC LIMIT 20000);",
                new Dictionary<string, object?> { ["source"] = DataSourceName }, token).ConfigureAwait(false)).EnsureAllOks();
    }

    public async Task<IReadOnlyList<IngestionHistoryEntry>> HistoryAsync(int limit = 1000, CancellationToken token = default)
    {
        if (limit is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = await _db.RawQuery("SELECT payload, completed_ticks, id FROM text_ingestion_history WHERE data_source_name=$source ORDER BY completed_ticks DESC, id DESC LIMIT $limit;",
            new Dictionary<string, object?> { ["limit"] = limit, ["source"] = DataSourceName }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        return (result.GetValue<HistoryRow[]>(0) ?? []).Select(r => JsonSerializer.Deserialize<IngestionHistoryEntry>(r.payload)!
            ?? throw new InvalidDataException("処理履歴が不正です。")).ToArray();
    }
    private sealed record HistoryRow(string payload);
}
