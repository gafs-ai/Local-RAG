using SurrealDb.Net;

namespace LocalRag.Infrastructure;

public sealed record DatabaseProbeResult(string EngineVersion, bool PersistencePassed, bool DiskAnnPassed, string Detail);

public static class DatabaseProbe
{
    public static async Task<DatabaseProbeResult> RunAsync(ServerConnection connection, CancellationToken cancellationToken = default)
    {
        // Isolate diagnostics from the application database and from concurrent diagnostic runs.
        var database = "probe_" + Guid.NewGuid().ToString("N");
        var marker = Guid.NewGuid().ToString("N");
        string version;
        await using (var db = await SurrealConnection.OpenAsync(connection, database, cancellationToken).ConfigureAwait(false))
        {
            version = await db.Version(cancellationToken).ConfigureAwait(false);
            (await db.RawQuery("CREATE marker:one SET value = $value;",
                new Dictionary<string, object?> { ["value"] = marker }, cancellationToken).ConfigureAwait(false)).EnsureAllOks();
        }

        await using var reopened = await SurrealConnection.OpenAsync(connection, database, cancellationToken).ConfigureAwait(false);
        var read = await reopened.RawQuery("SELECT VALUE value FROM marker:one;", cancellationToken: cancellationToken).ConfigureAwait(false);
        read.EnsureAllOks();
        var persisted = read.GetValue<string[]>(0)?.SingleOrDefault() == marker;
        if (!persisted) return new(version, false, false, "再接続後に保存内容を確認できませんでした。");

        try
        {
            (await reopened.RawQuery("DEFINE TABLE vector_probe SCHEMALESS; DEFINE INDEX probe_vector ON vector_probe FIELDS embedding DISKANN DIMENSION 3 DIST COSINE TYPE F32;",
                cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureAllOks();
            (await reopened.RawQuery("CREATE vector_probe:one SET name = 'nearest', embedding = $a; CREATE vector_probe:two SET name = 'other', embedding = $b;",
                new Dictionary<string, object?> { ["a"] = new float[] { 1, 0, 0 }, ["b"] = new float[] { 0, 1, 0 } }, cancellationToken).ConfigureAwait(false)).EnsureAllOks();
            await WaitForNearestAsync(reopened, "nearest", cancellationToken).ConfigureAwait(false);
            (await reopened.RawQuery("UPDATE vector_probe:one SET name = 'updated';", cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureAllOks();
            await WaitForNearestAsync(reopened, "updated", cancellationToken).ConfigureAwait(false);
            (await reopened.RawQuery("DELETE vector_probe:one;", cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureAllOks();
            await WaitForNearestAsync(reopened, "other", cancellationToken).ConfigureAwait(false);
            return new(version, true, true, "再接続後の保存内容、DiskANNの作成・検索・更新・削除を確認しました（実文書の検索品質は未検証）。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        finally
        {
            (await reopened.RawQuery($"REMOVE DATABASE {database};", cancellationToken: CancellationToken.None).ConfigureAwait(false)).EnsureAllOks();
        }
    }

    private static async Task WaitForNearestAsync(SurrealDbClient db, string expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var result = await db.RawQuery("SELECT VALUE name FROM vector_probe WHERE embedding <|1,64|> $q;",
                new Dictionary<string, object?> { ["q"] = new float[] { 1, 0, 0 } }, cancellationToken).ConfigureAwait(false);
            result.EnsureAllOks();
            if (result.GetValue<string[]>(0)?.SingleOrDefault() == expected) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidDataException("DiskANNの検索結果を確認できませんでした。");
    }
}
