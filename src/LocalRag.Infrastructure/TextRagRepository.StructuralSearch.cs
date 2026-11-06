using System.Text.Json;

namespace LocalRag.Infrastructure;

public sealed record StoredSearchVector(StoredTextChunk Chunk, float[] Embedding);
public sealed partial class TextRagRepository
{
    public async Task<IReadOnlyList<StoredSearchVector>> SearchVectorSeedsAsync(string space, string configuration, float[] query,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = await _db.RawQuery($"SELECT payload, embedding FROM (SELECT payload, embedding, vector::distance::knn() AS distance FROM {Table(space)} WHERE embedding <|{limit},{Math.Max(64, limit)}|> $query AND active=true AND configuration=$configuration ORDER BY distance);",
            new Dictionary<string, object?> { ["query"] = query, ["configuration"] = configuration }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        return ParseSearchVectors(result.GetValue<SearchVectorRow[]>(0) ?? []);
    }

    public async Task<IReadOnlyList<StoredSearchVector>> RelatedVectorsAsync(string space, string configuration,
        IReadOnlyList<string> keys, CancellationToken token)
    {
        if (keys.Count > 4096) throw new ArgumentOutOfRangeException(nameof(keys));
        var table = Table(space);
        var vectors = new List<StoredSearchVector>();
        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(128))
        {
            var parameters = new Dictionary<string, object?> { ["configuration"] = configuration };
            var records = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                parameters["key" + i] = batch[i];
                records.Add($"type::record('{table}', $key{i})");
            }
            var result = await _db.RawQuery($"SELECT payload, embedding FROM {table} WHERE id IN [{string.Join(",", records)}] AND active=true AND configuration=$configuration;",
                parameters, token).ConfigureAwait(false);
            result.EnsureAllOks();
            vectors.AddRange(ParseSearchVectors(result.GetValue<SearchVectorRow[]>(0) ?? []));
        }
        return vectors;
    }

    private static StoredSearchVector[] ParseSearchVectors(SearchVectorRow[] rows) => rows.Select(row =>
        new StoredSearchVector(JsonSerializer.Deserialize<StoredTextChunk>(row.payload) ?? throw new InvalidDataException("検索チャンクが不正です。"), row.embedding)).ToArray();
    private sealed record SearchVectorRow(string payload, float[] embedding);
}
