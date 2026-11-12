using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class IngestionActivityTests
{
    [ServerFact]
    public async Task StatisticsObserverCountsNewCompletionsAndCannotBreakIngestion()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "private-person-name.txt"), "private document body");
        var settings = TextRagTests.Settings(input) with { UsageStatistics = new() { Enabled = true, NoticeAcknowledged = true } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, new FakeTextAi(), settings, Path.Combine(fixture.Root, "data"));
        var statistics = new UsageStatisticsAccumulator(); statistics.Configure(settings.UsageStatistics);
        service.HistoryRecorded += _ => throw new InvalidOperationException("Observer must not affect ingestion.");
        service.HistoryRecorded += entry => statistics.Record(Path.GetExtension(entry.Path), entry.Result,
            entry.CompletedAtUtc - entry.StartedAtUtc);
        await service.ScanAsync();
        await service.ScanAsync(); // unchanged is not a new processing sample
        var report = statistics.Preview(settings, "0.26.6");
        var processing = Assert.Single(report!.Processing);
        Assert.Equal(".txt", processing.Extension);
        Assert.Equal(1, processing.Completed);
        Assert.Equal(0, processing.Failed);
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-person-name", json);
        Assert.DoesNotContain("private document body", json);
        Assert.Equal(2, (await repository.HistoryAsync()).Count);
        Assert.Equal("ready", Assert.Single(await repository.DocumentsAsync(default)).Status);
    }

    [ServerFact]
    public async Task HistoryRecordsRealCompletionOrderAndSurvivesReopeningWithoutSourceContents()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var file = Path.Combine(input, "guide.txt");
        const string source = "private payload never stored in history";
        await File.WriteAllTextAsync(file, source);
        var clock = new Clock(); var settings = TextRagTests.Settings(input);
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        {
            using var service = new TextRagService(repository, new FakeTextAi(), settings, Path.Combine(fixture.Root, "data"), clock);
            var activities = new List<IngestionActivity>(); service.ActivityChanged += activities.Add;
            Assert.Empty(await repository.HistoryAsync());
            await service.ScanAsync(); clock.Now += TimeSpan.FromMinutes(1);
            await service.ScanAsync(); clock.Now += TimeSpan.FromMinutes(1);
            File.Delete(file); await service.ScanAsync();
            var history = await repository.HistoryAsync();
            Assert.Equal(new[] { "deleted", "unchanged", "ready" }, history.Select(h => h.Result));
            Assert.All(history, h => { Assert.Equal(file, h.Path); Assert.True(h.CompletedAtUtc >= h.StartedAtUtc); });
            Assert.DoesNotContain(source, System.Text.Json.JsonSerializer.Serialize(history));
            var metadata = Assert.Single(await repository.DocumentStatusesAsync(default));
            Assert.Equal(file, metadata.Path); Assert.Equal("deleted", metadata.Status);
            Assert.Equal("source_missing", metadata.WithdrawalReason);
            Assert.Empty(metadata.Original); Assert.Null(metadata.Chunks); Assert.Null(metadata.CodeUnits);
            Assert.Equal(settings.PipelineConfigurationId(), metadata.ConfigurationId);
            Assert.Contains(activities, a => a.Waiting.Any(w => w.Path == file) && a.CurrentFile is null);
            Assert.Contains(activities, a => a.CurrentFile == file && a.Waiting.All(w => w.Path != file));
            Assert.Null(activities[^1].CurrentFile); Assert.Empty(activities[^1].Waiting);
        }
        await using var reopened = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        Assert.Equal("deleted", Assert.Single(await reopened.HistoryAsync(1)).Result);
        Assert.Equal(3, (await reopened.HistoryAsync()).Count);
    }

    [ServerFact]
    public async Task FailureAndCancellationHaveHistoryAndClearCurrentFileWithoutDestroyingCheckpoint()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var file = Path.Combine(input, "guide.txt"); await File.WriteAllTextAsync(file, "sample");
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var settings = TextRagTests.Settings(input) with { TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false } };
        using(var failure = new TextRagService(repository, new FailureAi(), settings, Path.Combine(fixture.Root, "data")))
            Assert.Equal(1, (await failure.ScanAsync()).Failed);
        var failed = Assert.Single(await repository.HistoryAsync());
        Assert.Equal("failed", failed.Result); Assert.Equal("connection", failed.ErrorDetailCode);
        using var stop = new CancellationTokenSource();
        using var cancelled = new TextRagService(repository, new FailureAi(stop), settings, Path.Combine(fixture.Root, "data"),
            new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(6)));
        var activities = new List<IngestionActivity>(); cancelled.ActivityChanged += activities.Add;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ScanAsync(token: stop.Token));
        Assert.Equal("interrupted", (await repository.HistoryAsync())[0].Result);
        Assert.Null(activities[^1].CurrentFile); Assert.Empty(activities[^1].Waiting);
        Assert.NotNull((await repository.DocumentsAsync(default))[0].Chunks);
        Assert.Equal(1, (await repository.DocumentsAsync(default))[0].Failure!.TransientFailures);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailureAi(CancellationTokenSource? stop = null) : ITextRagAi
    {
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new NotSupportedException();
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            if(stop is not null) { stop.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new HttpRequestException("DO NOT LOG RAW PROVIDER CONTENT");
        }
    }
}
