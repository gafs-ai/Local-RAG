using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class PriorityMonitoringTests
{
    [ServerFact]
    public async Task ManualRefreshDuringOneShotScanAddsHighPriorityFileWithoutInterruptingActiveFile()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var root = fixture.Root + "-input";
        var high = Path.Combine(root, "high"); Directory.CreateDirectory(high);
        var now = DateTime.UtcNow;
        async Task Make(string path, string text, int age)
        { await File.WriteAllTextAsync(path, text); File.SetLastWriteTimeUtc(path, now.AddMinutes(-age)); }
        await Make(Path.Combine(root, "active.txt"), "active", 20);
        await Make(Path.Combine(root, "low.txt"), "low", 30);
        var settings = new AppSettings { Folders = [new(root, Priority: 1), new(high, Priority: 99)],
            Ai = new() { ChatModel = "test", EmbeddingModel = "test" },
            TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false } };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new BlockingAi();
        using var service = new TextRagService(repo, ai, settings, fixture.Root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.False(await service.RefreshFileListAsync(stop.Token));
        var run = service.ScanAsync(token: stop.Token);
        try
        {
            await ai.Started.Task.WaitAsync(stop.Token);
            await Make(Path.Combine(high, "high.txt"), "high", 60);
            Assert.True(await service.RefreshFileListAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(ai.Inputs);
            Assert.False(run.IsCompleted);
        }
        finally { ai.Finish.TrySetResult(); }
        var result = await run;
        Assert.Equal(3, result.Indexed);
        Assert.Equal(["active", "high", "low"], ai.Inputs);
        Assert.False(await service.RefreshFileListAsync(stop.Token));
    }

    [Fact]
    public void CompletedUnchangedFilesDoNotReturnAheadOfWaitingWorkButChangesAndFailuresDo()
    {
        var now = DateTimeOffset.UtcNow;
        var done = new FileWorkItem("done", 1, now.UtcDateTime, 1, now);
        var waiting = new FileWorkItem("waiting", 0, now.UtcDateTime, 1, now);
        FileDiscovery Scan(params FileWorkItem[] items) => new(new(items.Select(x => x.Path).ToArray(), [], 0), items, 0);
        var queue = new FileWorkQueue(); queue.Replace(Scan(done, waiting));
        Assert.Equal(done, queue.Take()); queue.Complete(remember: true);
        queue.Replace(Scan(done, waiting));
        Assert.Equal(new[] { waiting }, queue.Snapshot());
        Assert.Equal(waiting, queue.Take()); queue.Complete(); // failure is retried
        queue.Replace(Scan(done, waiting)); Assert.Single(queue.Snapshot());
        var changed = done with { LastWriteUtc = now.UtcDateTime.AddSeconds(1) };
        queue.Replace(Scan(changed, waiting)); Assert.Equal(changed, queue.Snapshot()[0]);
        queue.Replace(Scan()); queue.Replace(Scan(done)); // restored after exclusion
        Assert.Equal(done, Assert.Single(queue.Snapshot()));
    }

    [Fact]
    public async Task DefaultsAndSavedPriorityProtectionAndExplicitIntervalRoundTrip()
    {
        Assert.Equal(5, new AppSettings().MonitoringIntervalMinutes);
        Assert.Equal(10, new AppSettings().UpdateExclusionMinutes);
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        await store.SaveAsync(new() { MonitoringIntervalMinutes = 15, UpdateExclusionMinutes = 7, Folders = [new(root + "-input", false, 85)] });
        var loaded = await store.LoadAsync();
        Assert.Equal(15, loaded.MonitoringIntervalMinutes);
        Assert.Equal(7, loaded.UpdateExclusionMinutes);
        Assert.Equal(85, Assert.Single(loaded.Folders).Priority);
        Assert.False(loaded.Folders[0].Recursive);
        Assert.NotEmpty((loaded with { UpdateExclusionMinutes = -1 }).Validate());
        Assert.NotEmpty((loaded with { Folders = [new(root, Priority: 101)] }).Validate());
        Assert.NotEqual(MonitoringCheckpoint.ScopeFor(loaded), MonitoringCheckpoint.ScopeFor(loaded with { UpdateExclusionMinutes = 0 }));
        Assert.Equal(loaded.PipelineConfigurationId(), (loaded with { UpdateExclusionMinutes = 0 }).PipelineConfigurationId());
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":1,\"MonitoringIntervalMinutes\":15}");
        var migrated = await store.LoadAsync();
        Assert.Equal(15, migrated.MonitoringIntervalMinutes);
        Assert.Equal(10, migrated.UpdateExclusionMinutes);
    }

    [Fact]
    public async Task DiscoveryAppliesInclusiveAgeBoundaryAndPriorityBeforeNewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "high"));
        var now = DateTimeOffset.UtcNow;
        async Task<string> Make(string name, int age)
        {
            var path = Path.GetFullPath(Path.Combine(root, name)); await File.WriteAllTextAsync(path, name);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-age).UtcDateTime); return path;
        }
        var high = await Make("high/old.txt", 60);
        var edge = await Make("boundary.txt", 10);
        var old = await Make("older.txt", 11);
        var recent = await Make("recent.txt", 9);
        var future = await Make("future.txt", -1);
        WatchedFolder[] folders = [new(root, Priority: 1), new(Path.Combine(root, "high"), Priority: 90)];
        var scan = TextFiles.Scan(folders, root + "-data", default);
        var selection = FileDiscoveryPolicy.Select(scan, folders, now, 10);
        Assert.Equal([high, edge, old], selection.Eligible.Select(f => f.Path).ToArray());
        Assert.Equal(2, selection.Deferred);
        Assert.Equal(90, selection.Eligible[0].Priority);
        Assert.Single(selection.Eligible, f => f.Path == high); // Overlapping roots never duplicate work.
        File.SetLastWriteTimeUtc(edge, now.UtcDateTime);
        Assert.False(FileDiscoveryPolicy.StillEligible(selection.Eligible[1], 10));
        var unprotected = FileDiscoveryPolicy.Select(scan, folders, now, 0);
        Assert.Contains(unprotected.Eligible, f => f.Path == recent);
        Assert.DoesNotContain(unprotected.Eligible, f => f.Path == future);
        Assert.Equal(5, selection.Scan.Files.Count); // Protected files are still seen for deletion reconciliation.
    }

    [Fact]
    public void QueueReordersWaitingWorkWithoutDuplicatingOrReplacingTheActiveFile()
    {
        var now = DateTimeOffset.UtcNow;
        FileWorkItem Item(string path, int priority, int age) => new(path, priority, now.AddMinutes(-age).UtcDateTime, 10, now);
        var active = Item("active", 5, 5); var low = Item("low", 1, 1); var high = Item("high", 99, 50);
        FileDiscovery Snapshot(params FileWorkItem[] items) => new(new(items.Select(x => x.Path).ToArray(), [], 0), items, 0);
        var queue = new FileWorkQueue();
        queue.Replace(Snapshot(active, low));
        Assert.Equal(active, queue.Take());
        queue.Replace(Snapshot(low, active, high, high));
        Assert.Equal(2, queue.Count);
        Assert.Throws<InvalidOperationException>(() => queue.Take());
        queue.Complete();
        Assert.Equal(high, queue.Take());
        queue.Replace(Snapshot(high)); // Removed/protected waiting files disappear from the queue.
        queue.Complete();
        Assert.Null(queue.Take());
    }

    [ServerFact]
    public async Task PeriodicDiscoveryPreemptsOnlyWaitingFilesAndDefersFilesUpdatedWhileQueued()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var root = fixture.Root + "-inputs";
        var high = Path.Combine(root, "high"); Directory.CreateDirectory(high);
        var clock = new Clock();
        async Task Make(string path, string text, int age)
        {
            await File.WriteAllTextAsync(path, text);
            File.SetLastWriteTimeUtc(path, clock.Now.AddMinutes(-age).UtcDateTime);
        }
        await Make(Path.Combine(root, "active.txt"), "active", 10);
        var mutable = Path.Combine(root, "mutable.txt");
        await Make(mutable, "mutable", 20);
        await Make(Path.Combine(root, "low.txt"), "low", 30);
        var settings = new AppSettings
        {
            Folders = [new(root, Priority: 1), new(high, Priority: 99)],
            Ai = new() { ChatModel = "test", EmbeddingModel = "test" },
            TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false }
        };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new BlockingAi();
        using var service = new TextRagService(repo, ai, settings, fixture.Root, clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0; var waits = 0; var indexed = 0;
        var monitor = new PersistentMonitor(repo, MonitoringCheckpoint.ScopeFor(settings), clock, async (_, token) =>
        {
            if (++delays == 1) { await tick.Task.WaitAsync(token); clock.Now += TimeSpan.FromMinutes(5); }
            else await Task.Delay(Timeout.Infinite, token);
        });
        var run = service.RunMonitoringAsync(monitor, null, result =>
        {
            indexed += result.Indexed;
            if (indexed == 3) stop.Cancel();
            return Task.CompletedTask;
        }, ex => Task.FromException(ex), _ =>
        {
            if (++waits == 2) secondCheck.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await ai.Started.Task.WaitAsync(stop.Token);
        await Make(Path.Combine(high, "high.txt"), "high", 60);
        await Make(mutable, "mutable updated", 0);
        Assert.True(await service.RefreshFileListAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(ai.Finish.Task.IsCompleted);
        Assert.Single(ai.Inputs);
        tick.SetResult();
        await secondCheck.Task.WaitAsync(stop.Token); // Check completed while the first embedding is still blocked.
        Assert.False(ai.Finish.Task.IsCompleted);
        Assert.Single(ai.Inputs);
        ai.Finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["active", "high", "low"], ai.Inputs);
        Assert.Equal(3, (await repo.DocumentsAsync(default)).Count);
        Assert.All(await repo.DocumentsAsync(default), d => Assert.Equal("ready", d.Status));
        // The deferred file is picked up after it has settled, including on manual scans.
        clock.Now += TimeSpan.FromMinutes(10);
        var later = await service.ScanAsync();
        Assert.Equal(1, later.Indexed);
        Assert.Equal(3, later.Unchanged);
        Assert.Equal(0, later.Deferred);
    }

    [ServerFact]
    public async Task ProtectedUpdatesDoNotDeleteTheOldStateAndAreNotDisclosedUntilReindexed()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var root = fixture.Root + "-inputs"; Directory.CreateDirectory(root);
        var clock = new Clock(); var file = Path.Combine(root, "guide.txt");
        await File.WriteAllTextAsync(file, "old text");
        File.SetLastWriteTimeUtc(file, clock.Now.AddMinutes(-10).UtcDateTime);
        var settings = TextRagTests.Settings(root) with { UpdateExclusionMinutes = 10 };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, fixture.Root, clock);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var original = Assert.Single(await repo.DocumentsAsync(default));
        await File.WriteAllTextAsync(file, "new text"); File.SetLastWriteTimeUtc(file, clock.Now.UtcDateTime);
        var deferred = await service.ScanAsync();
        Assert.Equal(1, deferred.Deferred);
        Assert.Equal(0, deferred.Deleted);
        Assert.Null(await service.FetchAsync(original.DocumentId));
        Assert.Empty(await service.SearchAsync("old"));
        Assert.Equal(original.RevisionId, Assert.Single(await repo.DocumentsAsync(default)).RevisionId);
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Equal("new text", (await service.FetchAsync(original.DocumentId))!.Content);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class BlockingAi : ITextRagAi
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Inputs { get; } = [];
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException();
        public async Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            Inputs.AddRange(inputs);
            if (Inputs.Count == 1) { Started.SetResult(); await Finish.Task.WaitAsync(token); }
            return new(3, inputs.Select(_ => new float[] { 1, 0.1f, 0 }).ToArray());
        }
    }
}
