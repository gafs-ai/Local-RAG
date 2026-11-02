namespace LocalRag.Infrastructure;

public static class EmbeddingProbe
{
    public static async Task RunAsync(ServerConnection connection, EmbeddingBatch batch, CancellationToken token)
    {
        var database = "embedding_probe_" + Guid.NewGuid().ToString("N");
        await using var db = await SurrealConnection.OpenAsync(connection, database, token).ConfigureAwait(false);
        try
        {
            (await db.RawQuery($"DEFINE TABLE vectors SCHEMALESS; DEFINE INDEX vector_idx ON vectors FIELDS embedding DISKANN DIMENSION {batch.Dimension} DIST COSINE TYPE F32;", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
            for (var i = 0; i < batch.Vectors.Count; i++)
                (await db.RawQuery("CREATE vectors SET position = $position, embedding = $embedding;",
                    new Dictionary<string, object?> { ["position"] = i, ["embedding"] = batch.Vectors[i] }, token).ConfigureAwait(false)).EnsureAllOks();
            for (var i = 0; i < batch.Vectors.Count; i++)
            {
                var result = await db.RawQuery("SELECT VALUE position FROM vectors WHERE embedding <|1,64|> $q;",
                    new Dictionary<string, object?> { ["q"] = batch.Vectors[i] }, token).ConfigureAwait(false);
                result.EnsureAllOks();
                if (result.GetValue<int[]>(0)?.SingleOrDefault(-1) != i)
                    throw new InvalidDataException("実Embeddingによる完全一致ベクトル検索に失敗しました。");
            }
        }
        finally { (await db.RawQuery($"REMOVE DATABASE {database};", cancellationToken: CancellationToken.None).ConfigureAwait(false)).EnsureAllOks(); }
    }
}
