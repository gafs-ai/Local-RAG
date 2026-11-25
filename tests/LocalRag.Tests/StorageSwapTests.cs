using LocalRag.Infrastructure;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace LocalRag.Tests;

public sealed class StorageSwapTests(ITestOutputHelper output)
{
    [ServerFact]
    public async Task ExistingManagedStoreShrinksAndRemainsSearchableAfterBackupRelease()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var files = Directory.CreateDirectory(Path.Combine(fixture.Root, "docs"));
        var data = Path.Combine(fixture.Root, "data");
        const string original = "公開資料：花壇の水やりは朝に行います。引用符「'」と改行も保持します。\n\n第2段落です。";
        await File.WriteAllTextAsync(Path.Combine(files.FullName, "guide.txt"), original);
        var settings = TextRagTests.Settings(files.FullName);
        Guid documentId;
        await using (var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, dataSourceName: settings.DataSourceName))
        {
            using var service = new TextRagService(repo, new FakeTextAi(), settings, data);
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            documentId = Assert.Single(await repo.DocumentsAsync(default)).DocumentId;
        }
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application"))
        {
            (await db.RawQuery("DEFINE TABLE contents SCHEMALESS; DEFINE INDEX nearest ON contents FIELDS embedding DISKANN DIMENSION 3 DIST COSINE TYPE F32; CREATE contents:one SET title='garden', embedding=[1f,0f,0f];")).EnsureAllOks();
            for (var i = 0; i < 32; i++)
                (await db.RawQuery("UPSERT working:one SET contents=$value RETURN NONE;",
                    new Dictionary<string, object?> { ["value"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(128 * 1024)) })).EnsureAllOks();
            (await db.RawQuery("DELETE working:one;")).EnsureAllOks();
        }
        var r = await fixture.Manager.CompactStorageAsync(fixture.Settings, settings.DataSourceName);
        output.WriteLine($"Managed DB size before/after (bytes): {r.PreviousBytes} / {r.CurrentBytes}");
        Assert.True(r.BackupPending);
        Assert.True(r.PreviousBytes > r.CurrentBytes, $"{r.PreviousBytes} -> {r.CurrentBytes}");
        Assert.True(fixture.Manager.HasCompactBackup);
        async Task CheckSearch()
        {
            await using var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application");
            var response = await db.RawQuery("SELECT VALUE title FROM contents WHERE embedding <|1,64|> [1f,0f,0f];");
            response.EnsureAllOks();
            Assert.Equal("garden", Assert.Single(response.GetValue<string[]>(0)!));
            await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, dataSourceName: settings.DataSourceName);
            using var service = new TextRagService(repo, new FakeTextAi(), settings, data);
            Assert.Equal(documentId, Assert.Single(await service.SearchAsync("水やり", 1)).DocumentId);
            Assert.Equal(original, (await service.FetchAsync(documentId))!.Content);
        }
        await CheckSearch();
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "application"))
        {
            const string marker = "text_source_metadata:storage_maintenance_identity";
            var read = await db.RawQuery($"SELECT VALUE maintenance_id FROM {marker};");
            read.EnsureAllOks();
            var identity = Assert.Single(read.GetValue<string[]>(0)!);
            (await db.RawQuery($"UPDATE {marker} SET maintenance_id=$id;",
                new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("N") })).EnsureAllOks();
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.ReleaseCompactBackupAsync(fixture.Settings, default));
            Assert.True(fixture.Manager.HasCompactBackup);
            (await db.RawQuery($"DELETE {marker};")).EnsureAllOks();
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.ReleaseCompactBackupAsync(fixture.Settings, default));
            Assert.True(fixture.Manager.HasCompactBackup);
            (await db.RawQuery($"CREATE {marker} SET maintenance_id=$id;",
                new Dictionary<string, object?> { ["id"] = identity })).EnsureAllOks();
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        Assert.True(fixture.Manager.HasCompactBackup);
        await fixture.Manager.ReleaseCompactBackupAsync(fixture.Settings, default);
        Assert.False(fixture.Manager.HasCompactBackup);
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await CheckSearch();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedCopyOrActivationRestoresOriginalAndDoesNotTouchSiblings(bool activated)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var s = new StoreSwap(root.FullName);
            CreateStore(s.Data);
            File.WriteAllText(Path.Combine(s.Data, "contents"), "original");
            var sibling = Path.Combine(root.FullName, "unrelated");
            File.WriteAllText(sibling, "keep");
            var id = s.Begin(); CreateStore(s.Stage(id));
            File.WriteAllText(Path.Combine(s.Stage(id), "contents"), "replacement");
            if (activated) s.Activate(id);
            new StoreSwap(root.FullName).Recover();
            Assert.Equal("original", File.ReadAllText(Path.Combine(s.Data, "contents")));
            Assert.Equal("keep", File.ReadAllText(sibling));
            Assert.False(Directory.Exists(s.Stage(id))); Assert.False(Directory.Exists(s.Backup(id)));
            Assert.Null(s.Read());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void InterruptedBetweenDirectoryRenamesRestoresOriginal()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var swap = new StoreSwap(root.FullName);
            CreateStore(swap.Data);
            File.WriteAllText(Path.Combine(swap.Data, "old"), "original");
            var id = swap.Begin();
            CreateStore(swap.Stage(id));
            swap.Activate(id);
            // Recreate the state immediately after the first of the two directory renames.
            Directory.Move(swap.Data, swap.Stage(id));
            new StoreSwap(root.FullName).Recover();
            Assert.Equal("original", File.ReadAllText(Path.Combine(swap.Data, "old")));
            Assert.False(Directory.Exists(swap.Stage(id)));
            Assert.Null(swap.Read());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void OriginalSurvivesRestartUntilExplicitReleaseAndMissingActiveDbBlocksDeletion()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var swap = new StoreSwap(root.FullName);
            CreateStore(swap.Data);
            File.WriteAllText(Path.Combine(swap.Data, "old"), "old-data");
            var id = swap.Begin(); CreateStore(swap.Stage(id));
            File.WriteAllText(Path.Combine(swap.Stage(id), "new"), "active");
            swap.Activate(id); swap.Accept(id);
            var restarted = new StoreSwap(root.FullName);
            restarted.Recover();
            Assert.True(restarted.HasBackup);
            var parked = Path.Combine(root.FullName, "unrelated");
            Directory.Move(swap.Data, parked);
            Assert.ThrowsAny<IOException>(() => restarted.ReleaseBackup(id));
            Assert.True(File.Exists(Path.Combine(swap.Backup(id), "old")));
            CreateStore(swap.Data); // Even a valid-looking different store cannot authorize backup deletion.
            Assert.Throws<InvalidDataException>(() => restarted.ReleaseBackup(id));
            Assert.True(File.Exists(Path.Combine(swap.Backup(id), "old")));
            Directory.Move(swap.Data, Path.Combine(root.FullName, "unverified"));
            Directory.Move(parked, swap.Data);
            restarted.ReleaseBackup(id);
            Assert.False(restarted.HasBackup);
            Assert.False(Directory.Exists(swap.Backup(id)));
            Assert.Equal("active", File.ReadAllText(Path.Combine(swap.Data, "new")));
        }
        finally { root.Delete(true); }
    }
    private static void CreateStore(string directory)
    {
        var manifest = Directory.CreateDirectory(Path.Combine(directory, "manifest"));
        File.WriteAllText(Path.Combine(manifest.FullName, "0000.manifest"), "fixture");
    }

    [Fact]
    public void MissingJournalWithAnOldStoreStopsEmptyDatabaseBootstrap()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var swap = new StoreSwap(root.FullName);
            CreateStore(swap.Backup(Guid.NewGuid()));
            Assert.Throws<InvalidDataException>(swap.CheckForUnjournaledCopy);
            Assert.False(Directory.Exists(swap.Data));
        }
        finally { root.Delete(true); }
    }
}
