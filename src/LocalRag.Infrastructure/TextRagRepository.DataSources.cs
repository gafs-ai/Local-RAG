using System.Text.Json.Nodes;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    private async Task<string> LegacySourceAsync(CancellationToken token)
    {
        const string read = "SELECT VALUE name FROM text_source_metadata:legacy_owner;";
        (await _db.RawQuery("DEFINE TABLE IF NOT EXISTS text_source_metadata SCHEMALESS;", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
        var existing = await _db.RawQuery(read, cancellationToken: token).ConfigureAwait(false);
        existing.EnsureAllOks();
        if (existing.GetValue<string[]>(0)?.SingleOrDefault() is { } name) return name;
        try
        {
            (await _db.RawQuery("CREATE text_source_metadata:legacy_owner SET name=$name;", new Dictionary<string, object?> { ["name"] = DataSourceName }, token).ConfigureAwait(false)).EnsureAllOks();
            return DataSourceName;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            var raced = await _db.RawQuery(read, cancellationToken: token).ConfigureAwait(false);
            raced.EnsureAllOks();
            return raced.GetValue<string[]>(0)?.SingleOrDefault() ?? throw new InvalidDataException("旧データの所属を確定できません。");
        }
    }

    private async Task BackfillDataSourceNamesAsync(CancellationToken token)
    {
        var legacySource = await LegacySourceAsync(token).ConfigureAwait(false);
        // Upgrade with old clients stopped. Keep revision IDs and vector values unchanged.
        foreach (var table in new[] { "text_revision", "text_document" })
        {
            while (true)
            {
                var result = await _db.RawQuery($"SELECT VALUE payload FROM {table} WHERE source_key_version = NONE LIMIT 32;", cancellationToken: token).ConfigureAwait(false);
                result.EnsureAllOks();
                var payloads = result.GetValue<string[]>(0) ?? [];
                if (payloads.Length == 0) break;
                foreach (var payload in payloads)
                {
                    token.ThrowIfCancellationRequested();
                    var json = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException("文書状態が不正です。");
                    var oldKey = json["Key"]!.GetValue<string>();
                    var revision = json["RevisionId"]!.GetValue<string>();
                    var name = json["DataSourceName"]?.GetValue<string>() ?? legacySource;
                    if (!AppSettings.ValidDataSourceName(name)) throw new InvalidDataException("データソース名が不正です。");
                    var path = json["Path"]?.GetValue<string>();
                    var newKey = !string.IsNullOrEmpty(path) && oldKey == TextRagSettings.Hash(Path.GetFullPath(path).ToUpperInvariant())
                        ? DocumentIdentity.Key(name, path) : oldKey;
                    json["DataSourceName"] = name; json["Key"] = newKey;
                    var parameters = new Dictionary<string, object?> { ["old"] = table == "text_document" ? oldKey : revision,
                        ["key"] = table == "text_document" ? newKey : revision, ["payload"] = json.ToJsonString(), ["source"] = name, ["document"] = newKey };
                    var statements = new List<string> { "BEGIN TRANSACTION;" };
                    if (table == "text_revision" && json["SpaceId"]?.GetValue<string>() is { } space)
                    {
                        var vectorTable = Table(space);
                        var vectors = await _db.RawQuery($"SELECT VALUE payload FROM {vectorTable} WHERE revision=$revision;",
                            new Dictionary<string, object?> { ["revision"] = revision }, token).ConfigureAwait(false);
                        vectors.EnsureAllOks();
                        var index = 0;
                        foreach (var vector in vectors.GetValue<string[]>(0) ?? [])
                        {
                            var chunk = JsonNode.Parse(vector)!.AsObject(); chunk["DocumentKey"] = newKey;
                            parameters["vector" + index] = revision + "_" + chunk["Ordinal"]!.GetValue<int>();
                            parameters["chunk" + index] = chunk.ToJsonString();
                            statements.Add($"UPDATE type::record('{vectorTable}', $vector{index}) SET document_key=$document, data_source_name=$source, payload=$chunk{index};");
                            index++;
                        }
                    }
                    if (table == "text_document" && oldKey != newKey)
                    {
                        statements.Add($"CREATE type::record('{table}', $key) SET payload=$payload, data_source_name=$source, source_key_version=1;");
                        statements.Add($"DELETE type::record('{table}', $old);");
                    }
                    else statements.Add($"UPDATE type::record('{table}', $key) SET payload=$payload, data_source_name=$source, source_key_version=1;");
                    statements.Add("COMMIT TRANSACTION;");
                    (await _db.RawQuery(string.Join(" ", statements), parameters, token).ConfigureAwait(false)).EnsureAllOks();
                }
            }
        }
        (await _db.RawQuery("UPDATE text_ingestion_history SET data_source_name=$source WHERE data_source_name=NONE;",
            new Dictionary<string, object?> { ["source"] = legacySource }, token).ConfigureAwait(false)).EnsureAllOks();
        var oldMonitor = await _db.RawQuery("SELECT VALUE payload FROM text_monitor:current;", cancellationToken: token).ConfigureAwait(false);
        oldMonitor.EnsureAllOks();
        if (oldMonitor.GetValue<string[]>(0)?.SingleOrDefault() is { } checkpoint)
            (await _db.RawQuery("BEGIN TRANSACTION; UPSERT type::record('text_monitor', $source) SET payload=$payload, data_source_name=$name; DELETE text_monitor:current; COMMIT TRANSACTION;",
                new Dictionary<string, object?> { ["source"] = TextRagSettings.Hash(legacySource), ["name"] = legacySource, ["payload"] = checkpoint }, token).ConfigureAwait(false)).EnsureAllOks();
    }
}
