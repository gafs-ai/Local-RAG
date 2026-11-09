using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class IngestionRetryIntegrationTests
{
    private static AppSettings Settings(string folder) => TextRagTests.Settings(folder) with
    {
        TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false, ChunkEnabled = false,
            ParentMode = ParentChunkMode.None, ParentEmbedding = ParentEmbeddingMode.None }
    };

    [ServerFact]
    public async Task ReadFailuresArePersistedFilteredFromQueueAndReleasedByRelevantSettings()
    {
        await using var f = await ServerFixture.StartAsync();
        var folder = Path.Combine(f.Root, "input"); Directory.CreateDirectory(folder);
        var bad = Path.Combine(folder, "too-large.txt");
        await File.WriteAllTextAsync(bad, new string('x', 200));
        await File.WriteAllTextAsync(Path.Combine(folder, "small.txt"), "good text");
        var settings = Settings(folder) with { TextPipeline = Settings(folder).TextPipeline with { MaxDocumentCharacters = 100 } };
        var clock = new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        await using (var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!))
        using (var service = new TextRagService(repo, new TestAi(), settings, Path.Combine(f.Root, "data"), clock))
        {
            var first = await service.ScanAsync();
            Assert.Equal(1, first.Failed); Assert.Equal(1, first.Indexed);
            var failed = (await repo.DocumentsAsync(default)).Single(d => d.Path == bad);
            Assert.Null(failed.Failure!.NextAttemptAtUtc);
            Assert.Single(await repo.FailuresAsync(default));
            Assert.NotNull((await repo.DocumentStatusesAsync(default)).Single(d => d.Path == bad).Failure);
        }
        // New connection and service: the persisted decision, not an in-memory flag, controls the queue.
        await using var reopened = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using (var again = new TextRagService(reopened, new TestAi(), settings, Path.Combine(f.Root, "data"), clock))
        {
            var waiting = new List<string>();
            again.ActivityChanged += a => waiting.AddRange(a.Waiting.Select(x => x.Path));
            Assert.Equal(0, (await again.ScanAsync()).Failed);
            Assert.DoesNotContain(bad, waiting);
            Assert.Single(await reopened.HistoryAsync(), h => h.Result == "failed");
        }
        var changed = settings with { TextPipeline = settings.TextPipeline with { MaxDocumentCharacters = 300 } };
        using var fixedService = new TextRagService(reopened, new TestAi(), changed, Path.Combine(f.Root, "data"), clock);
        Assert.Equal(1, (await fixedService.ScanAsync()).Indexed);
        Assert.Empty(await reopened.FailuresAsync(default));
    }

    [ServerFact]
    public async Task InvalidLlmOutputStopsAfterThreeFailedAttemptsAndContentChangeStartsNewBudget()
    {
        await using var f = await ServerFixture.StartAsync();
        var folder = Path.Combine(f.Root, "input"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "license.txt"); await File.WriteAllTextAsync(path, "license text");
        var options = Settings(folder).TextPipeline with { ClassifyEnabled = true,
            Categories = [new() { Id = "general" }, new() { Id = "excluded", Excluded = true }] };
        var settings = Settings(folder) with { TextPipeline = options };
        var clock = new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var ai = new TestAi { UnknownCategory = true };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
            using var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data"), clock);
            Assert.Equal(1, (await service.ScanAsync()).Failed);
            Assert.Equal(attempt, Assert.Single(await repo.DocumentsAsync(default)).Failure!.LlmFailures);
            Assert.Equal(0, (await service.ScanAsync()).Failed); // Including manual checks during backoff.
            Assert.Equal(attempt * 2, ai.Calls);
            clock.Now = clock.Now.AddMinutes(5);
        }
        clock.Now = clock.Now.AddDays(1);
        await using var finalRepo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using var finalService = new TextRagService(finalRepo, ai, settings, Path.Combine(f.Root, "data"), clock);
        Assert.Equal(0, (await finalService.ScanAsync()).Failed);
        Assert.Equal(6, ai.Calls);
        Assert.Equal(3, (await finalRepo.HistoryAsync()).Count(h => h.Result == "failed"));
        Assert.Contains("再試行停止", (await finalRepo.HistoryAsync())[0].ResultDisplay);
        await File.AppendAllTextAsync(path, " changed");
        Assert.Equal(1, (await finalService.ScanAsync()).Failed);
        Assert.Equal(1, Assert.Single(await finalRepo.DocumentsAsync(default)).Failure!.LlmFailures);
    }

    [ServerFact]
    public async Task TemporaryConnectionFailureWaitsThenResumesWithoutReclassifying()
    {
        await using var f = await ServerFixture.StartAsync();
        var folder = Path.Combine(f.Root, "input"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "license text");
        var settings = Settings(folder) with { TextPipeline = Settings(folder).TextPipeline with { ClassifyEnabled = true } };
        var clock = new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var ai = new TestAi { ConnectionFailure = true };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data"), clock);
        Assert.Equal(1, (await service.ScanAsync()).Failed);
        ai.ConnectionFailure = false;
        Assert.Equal(0, (await service.ScanAsync()).Indexed);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Equal(1, ai.Calls);
        Assert.Null(Assert.Single(await repo.DocumentsAsync(default)).Failure);
        Assert.Empty(await repo.FailuresAsync(default));
    }

    private sealed class TestAi : ITextRagAi
    {
        public bool UnknownCategory { get; init; }
        public bool ConnectionFailure { get; set; }
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string s, string i, CancellationToken t)
        { Calls++; return Task.FromResult(JsonSerializer.Serialize(new { category = UnknownCategory ? "others" : "general" })); }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken t) =>
            ConnectionFailure ? throw new HttpRequestException("private response") :
                Task.FromResult(new VectorBatch(3, inputs.Select(_ => new float[] { 1, 0, 0 }).ToArray()));
    }
}

internal sealed class RetryTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
