using System.Text.RegularExpressions;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    // Caller must stop ingestion, monitoring and retrieval before resetting this database.
    // Do not remove the database/namespace: an external server can contain unrelated tables.
    public async Task ResetIngestionDataAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var info = await _db.RawQuery("INFO FOR DB;", cancellationToken: token).ConfigureAwait(false);
        info.EnsureAllOks();
        var tables = info.GetValue<ResetDatabaseInfo>(0)?.tables ?? throw new InvalidDataException("DBのテーブル一覧を取得できません。");
        // Vector spaces are shared across sources. Retain the tables/indexes and space registry;
        // remove only rows owned by this source, including interrupted staging rows.
        var vectors = tables.Keys.Where(name => Regex.IsMatch(name, "^text_vectors_[0-9a-f]{64}$", RegexOptions.CultureInvariant)).ToArray();
        var query = "BEGIN TRANSACTION; " + string.Join(" ", vectors.Select(name => $"DELETE {name} WHERE data_source_name=$source;")) +
            " DELETE text_document WHERE data_source_name=$source; DELETE text_revision WHERE data_source_name=$source; DELETE text_monitor WHERE data_source_name=$source; DELETE text_ingestion_history WHERE data_source_name=$source; COMMIT TRANSACTION;";
        (await _db.RawQuery(query, new Dictionary<string, object?> { ["source"] = DataSourceName }, token).ConfigureAwait(false)).EnsureAllOks();
        Interlocked.Exchange(ref _historyWrites, 0);
    }

    private sealed record ResetDatabaseInfo(Dictionary<string, string> tables);
}
