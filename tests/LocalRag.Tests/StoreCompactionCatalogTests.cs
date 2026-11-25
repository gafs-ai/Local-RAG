using System.Security.Cryptography;
using System.Diagnostics;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class StoreCompactionCatalogTests
{
    [ServerFact]
    public async Task CancelledCandidateOperationReleasesStoreImmediatelyAndOriginalReaderStaysUsable()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var exe = new SurrealServerInstaller(Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!).ExecutablePath;
            var scratch = Directory.CreateDirectory(Path.Combine(root.FullName, "cli"));
            Directory.CreateDirectory(Path.Combine(scratch.FullName, "history.txt"));
            var candidate = Path.Combine(root.FullName, "new");
            using var cancel = new CancellationTokenSource();
            await using var reader = new SurrealStoreCompactor.Shell(exe, Path.Combine(root.FullName, "original"), scratch.FullName);
            Assert.Equal(1, (await reader.ResultAsync("RETURN 1", default)).GetInt32());
            var timer = new Stopwatch();
            await using (var writer = new SurrealStoreCompactor.Shell(exe, candidate, scratch.FullName, candidateWriter: true, token: cancel.Token))
            {
                Assert.Equal(1, (await writer.ResultAsync("RETURN 1", default)).GetInt32());
                var blocked = writer.ResultAsync("SLEEP 45s", cancel.Token);
                await Task.Delay(150);
                Assert.False(blocked.IsCompleted);
                timer.Start();
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
            }
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12), $"Cancellation took {timer.Elapsed}.");
            await using var reopened = new SurrealStoreCompactor.Shell(exe, candidate, scratch.FullName);
            Assert.Equal(1, (await reopened.ResultAsync("RETURN 1", default)).GetInt32());
            Assert.Equal(1, (await reader.ResultAsync("RETURN 1", default)).GetInt32());
        }
        finally { root.Delete(true); }
    }

    [ServerFact]
    public async Task MultiplePagesOfTypedRecordsInAnAdditionalDatabaseSurviveCompaction()
    {
        await using var fixture = await ServerFixture.StartAsync();
        string[] expected;
        const string select = "SELECT VALUE type::string($this) FROM typed ORDER BY id;";
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "probe_test"))
        {
            (await db.RawQuery("""
                FOR $n IN 0..300 {
                    CREATE type::record('typed',$n) SET numbers=[1f, 0.12345dec, $n], reference=other:one,
                        timestamp=d'2025-05-01T01:02:03.000123Z', bytes=b'0102FF', uuid=u'62ad12a3-4438-478c-9d19-08ec3a56b966',
                        raw="日本語\n'\nDELETE typed;\n中文⟨韓⟩";
                };
                """)).EnsureAllOks();
            // The old store must be large enough for the manager to choose the new verified copy.
            for (var n = 0; n < 12; n++)
                (await db.RawQuery("UPSERT scratch:one SET body=$data RETURN NONE;",
                    new Dictionary<string, object?> { ["data"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(128 * 1024)) })).EnsureAllOks();
            (await db.RawQuery("DELETE scratch:one;")).EnsureAllOks();
            var rows = await db.RawQuery(select);
            rows.EnsureAllOks();
            expected = rows.GetValue<string[]>(0)!;
            Assert.Equal(300, expected.Length);
        }
        var result = await fixture.Manager.CompactStorageAsync(fixture.Settings, "Local-CatalogTest");
        Assert.True(result.BackupPending);
        await using var restored = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "probe_test");
        var actual = await restored.RawQuery(select);
        actual.EnsureAllOks();
        Assert.Equal(expected, actual.GetValue<string[]>(0));
    }

    [ServerFact]
    public async Task CustomFieldDefinitionCancelsCompactionWithoutReplacingTheOldDatabase()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "probe_test"))
            (await db.RawQuery("DEFINE TABLE custom SCHEMALESS; DEFINE FIELD title ON custom TYPE string; CREATE custom:keep SET title='retain this';")).EnsureAllOks();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.CompactStorageAsync(fixture.Settings, "Local-CatalogTest"));
        Assert.False(fixture.Manager.HasCompactBackup);
        await using var original = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "probe_test");
        var rows = await original.RawQuery("SELECT VALUE title FROM custom;");
        rows.EnsureAllOks();
        Assert.Equal("retain this", Assert.Single(rows.GetValue<string[]>(0)!));
    }
}
