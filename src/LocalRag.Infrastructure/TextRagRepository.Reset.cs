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
        // Include orphaned spaces left by interrupted initialization. Only exact app-generated
        // identifiers are allowed into SQL; registry values and arbitrary table names are never interpolated.
        var vectors = tables.Keys.Where(name => Regex.IsMatch(name, "^text_vectors_[0-9a-f]{64}$", RegexOptions.CultureInvariant)).ToArray();
        var query = "BEGIN TRANSACTION; " + string.Join(" ", vectors.Select(name => $"REMOVE TABLE IF EXISTS {name};")) +
            " DELETE text_document; DELETE text_revision; DELETE text_space; DELETE text_monitor; DELETE text_ingestion_history; COMMIT TRANSACTION;";
        (await _db.RawQuery(query, cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
        Interlocked.Exchange(ref _historyWrites, 0);
    }

    private sealed record ResetDatabaseInfo(Dictionary<string, string> tables);
}
