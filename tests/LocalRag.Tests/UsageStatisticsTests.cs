using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class UsageStatisticsTests
{
    private static AppSettings Settings() => new() { Management = ManagementSettings.CreateDefault(),
        UsageStatistics = new() { Enabled = true, NoticeAcknowledged = true } };
    private static UsageStatisticsAccumulator Accumulator()
    { var stats = new UsageStatisticsAccumulator(); stats.Configure(Settings().UsageStatistics); return stats; }

    [Fact]
    public void DefaultsRequireNoticeAndOptOutClearsAllPendingData()
    {
        var stats = new UsageStatisticsAccumulator();
        Assert.True(new UsageStatisticsSettings().Enabled);
        Assert.False(new UsageStatisticsSettings().NoticeAcknowledged);
        stats.Configure(new()); stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        Assert.Null(stats.Capture(Settings(), "0.26.6"));
        stats.Configure(Settings().UsageStatistics); stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        var pending = Assert.IsType<UsageStatisticsBatch>(stats.Capture(Settings(), "0.26.6"));
        stats.Configure(new() { Enabled = false, NoticeAcknowledged = true });
        Assert.Empty(stats.GetCounters()); Assert.Null(stats.Capture(Settings(), "0.26.6"));
        stats.Configure(Settings().UsageStatistics); stats.Record(".pdf", "ready", TimeSpan.FromSeconds(1));
        stats.Acknowledge(pending);
        Assert.Equal(".pdf", Assert.Single(stats.GetCounters()).Extension);
    }

    [Fact]
    public void AcknowledgementPreservesNewWorkAndCannotApplyTwice()
    {
        var stats = Accumulator(); stats.Record(".txt", "ready", TimeSpan.FromMilliseconds(100));
        var batch = stats.Capture(Settings(), "0.26.6")!;
        stats.Record(".txt", "ready", TimeSpan.FromMilliseconds(300));
        Assert.Same(batch, stats.Capture(Settings(), "0.26.6"));
        Assert.Equal(2, Assert.Single(stats.GetCounters()).Completed);
        Assert.Same(batch.Report, stats.Preview(Settings() with { Ai = new() { ChatModel = "changed" } }, "0.26.6"));
        stats.Acknowledge(batch); stats.Acknowledge(batch);
        var row = Assert.Single(stats.GetCounters());
        Assert.Equal(1, row.Completed); Assert.Equal(300, row.AverageMilliseconds);
    }

    [Fact]
    public void FreeFormSettingsNamesPathsHardwareAndAliasesNeverLeavePrivacyBoundary()
    {
        const string secret = "EMPLOYEE_ALICE_SECRET_ACQUISITION";
        var s = Settings();
        s = s with { DataSourceName = secret, DataSourceOwner = secret, Folders = [new(@"C:\" + secret)],
            GlobalIgnorePatterns = [secret], Ai = s.Ai with { ChatModel = secret, EmbeddingModel = @"C:\" + secret },
            Management = s.Management! with { UserPrompts = [new(secret, secret, PromptPurpose.Classification, secret)],
                Categories = [new(secret, secret, secret)], Routes = [new() { Name = secret, Extensions = ["." + secret],
                    ClassifyPromptId = secret, DefaultCategoryId = secret, EnabledCategoryIds = [secret] }] } };
        var stats = Accumulator(); stats.Record("." + secret, "ready", TimeSpan.FromSeconds(1));
        var report = stats.Capture(s, "0.26.6", new(secret, secret, "Driver NPU", 32L * 1024 * 1024 * 1024))!;
        var json = Encoding.UTF8.GetString(report.Json);
        Assert.DoesNotContain(secret, json); Assert.DoesNotContain("C:\\", json);
        Assert.DoesNotContain("dataSource", json); Assert.DoesNotContain("folder", json);
        Assert.DoesNotContain("timestamp", json); Assert.DoesNotContain("configurationId", json);
        Assert.Equal("other", report.Report.Models.Language);
        Assert.Equal("unknown", report.Report.Hardware.Cpu);
        Assert.Equal("other", Assert.Single(report.Report.Processing).Extension);
        Assert.True(report.Report.Settings.PipelineChanged);
        Assert.True(report.Report.Settings.CategoriesChanged);
        Assert.True(Assert.Single(report.Report.Settings.Routes).CustomPrompts);
    }

    [Fact]
    public void DefaultConfigurationReportsNoCustomizationsAndKnownModelsHaveCanonicalNames()
    {
        var settings = Settings();
        var value = UsageStatisticsPrivacy.Configuration(settings);
        Assert.False(value.PipelineChanged); Assert.False(value.CategoriesChanged); Assert.False(value.RetrievalChanged);
        Assert.All(value.Prompts, p => Assert.False(p.Changed));
        Assert.Equal("Gemma-3-4b-it-mm-NPU", UsageStatisticsPrivacy.Model("gemma-3-4b-it-mm-npu"));
        Assert.Equal("other", UsageStatisticsPrivacy.Model("private-Gemma-3-4b-it-mm-NPU"));
        Assert.Equal("unknown", UsageStatisticsPrivacy.Version("alice 0.26.6"));
        Assert.Equal("0.26.6", UsageStatisticsPrivacy.Version("0.26.6"));
    }

    [Theory]
    [InlineData("unchanged")][InlineData("deferred")][InlineData("deleted")][InlineData("interrupted")][InlineData("private secret")]
    public void NonProcessingEventsAreNotDemandOrTimingSamples(string outcome)
    { var stats = Accumulator(); stats.Record(".txt", outcome, TimeSpan.FromSeconds(100)); Assert.Empty(stats.GetCounters()); }

    [Fact]
    public void ConcurrentCollectionIsBoundedAndDurationsAreClamped()
    {
        var stats = Accumulator();
        Parallel.For(0, 101000, _ => stats.Record(".PDF", "failed", TimeSpan.MaxValue));
        var row = Assert.Single(stats.GetCounters());
        Assert.Equal(UsageStatisticsAccumulator.MaximumSamplesPerExtension, row.Failed);
        Assert.Equal(UsageStatisticsAccumulator.MaximumDurationMilliseconds, row.AverageMilliseconds);
        Assert.Equal(UsageStatisticsAccumulator.MaximumDurationMilliseconds * row.Failed, row.TotalMilliseconds);
        stats.Record(".csv", "ready", TimeSpan.FromSeconds(-1));
        Assert.Single(stats.GetCounters());
    }

    [Theory]
    [InlineData(0, 0)][InlineData(15, 16)][InlineData(31, 32)][InlineData(33, 48)][InlineData(129, 256)][InlineData(300, 512)]
    public void MemoryIsBucketed(int gib, int expected) => Assert.Equal(expected, UsageStatisticsPrivacy.MemoryBucket(gib * 1024L * 1024 * 1024));

    [Fact]
    public async Task PersistenceContainsOnlyValidatedAggregatesAndOptOutRemovesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new UsageStatisticsLocalStore(root); var stats = Accumulator();
        stats.Record(".xlsx", "ready", TimeSpan.FromSeconds(3)); await store.SaveAsync(stats);
        var restored = Accumulator(); Assert.True(await store.LoadAsync(restored));
        Assert.Equal(Assert.Single(stats.GetCounters()), Assert.Single(restored.GetCounters()));
        Assert.False(await store.LoadAsync(restored)); // Never duplicate persisted counters into live counters.
        await File.WriteAllTextAsync(Path.Combine(root, "statistics-counters.json"), "[{\"extension\":\".private\",\"completed\":1,\"failed\":0,\"skipped\":0,\"totalMilliseconds\":1,\"averageMilliseconds\":1}]");
        Assert.False(await store.LoadAsync(Accumulator()));
        await File.WriteAllTextAsync(Path.Combine(root, "statistics-counters.json"), "[null]");
        Assert.False(await store.LoadAsync(Accumulator()));
        await store.ClearAsync(); Assert.False(File.Exists(Path.Combine(root, "statistics-counters.json")));
    }

    [Fact]
    public async Task PendingReportSurvivesRestartWithSameNonceAndBodyAndRejectsPrivateFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new UsageStatisticsLocalStore(root); var stats = Accumulator();
        stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        var pending = stats.Capture(Settings(), "0.26.6")!;
        stats.Record(".txt", "ready", TimeSpan.FromSeconds(2));
        await store.SaveAsync(stats);
        var restored = Accumulator(); Assert.True(await store.LoadAsync(restored));
        var retry = restored.Capture(Settings(), "0.26.6")!;
        Assert.Equal(pending.Json, retry.Json); Assert.Equal(pending.Nonce, retry.Nonce); Assert.Equal(pending.CreatedAt, retry.CreatedAt);
        restored.Acknowledge(retry); Assert.Equal(1, Assert.Single(restored.GetCounters()).Completed);
        var path = Path.Combine(root, "statistics-counters.json");
        var corrupt = (await File.ReadAllTextAsync(path)).Replace("\"cpu\":\"unknown\"", "\"cpu\":\"private-secret\"");
        await File.WriteAllTextAsync(path, corrupt);
        Assert.False(await store.LoadAsync(Accumulator()));
    }

    [Theory]
    [InlineData("Uncatalogued NPU v7")][InlineData("  中文/한국어/日本語 演算装置  ")][InlineData("なし")]
    public async Task ReportPersistenceAndManualAttachmentPreserveReportedNpu(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new UsageStatisticsLocalStore(root); var stats = Accumulator();
        stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        var pending = stats.Capture(Settings(), "0.26.6", new(Npu: name))!;
        Assert.True(UsageStatisticsPrivacy.ValidReport(pending.Report));
        await store.SaveAsync(stats);
        var restored = Accumulator(); Assert.True(await store.LoadAsync(restored));
        var retry = restored.Capture(Settings(), "0.26.6")!;
        Assert.Equal(name, retry.Report.Hardware.Npu);
        Assert.Equal(pending.Json, retry.Json);
        var attachment = restored.SnapshotForFeedback(Settings(), ProductInfo.Version, new(Npu: name));
        Assert.Equal(name, FeedbackBatch.Create("bug", "synthetic", attachment).Report.Usage!.Hardware.Npu);
    }

    [Fact]
    public void MaximumBoundedReportFitsTransportLimit()
    {
        var extensions = new[] { ".txt", ".md", ".markdown", ".doc", ".docx", ".docm", ".pdf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv",
            ".cs", ".py", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".c", ".h", ".rs", ".java", ".kt", ".kts", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".toml", ".ini", ".sql", ".rst", ".tex", ".tsv", "other" };
        var stats = Accumulator();
        Assert.True(stats.RestoreCounters(extensions.Select(e => new UsageStatisticsProcessing(e, 99998, 1, 1, 8640000000000, 86400000)).ToArray()));
        var s = Settings();
        s = s with { Management = s.Management! with { Routes = Enumerable.Range(0, 32).Select(i => new TextRoute
            { SourceMode = (SourceCodeMode)(i % 16), ChildMode = (ChildChunkMode)(i / 16), ParentMode = ParentChunkMode.Structure, Extensions = [".txt"] }).ToList() } };
        var report = stats.Capture(s, "999.99.9999")!;
        Assert.True(report.Json.Length < UsageStatisticsSender.MaximumBodyBytes);
        Assert.True(UsageStatisticsPrivacy.ValidReport(report.Report));
    }

    [Fact]
    public async Task SenderSignsExactBodyAndRetryUsesSameNonceWithFreshTimestampWhenNeeded()
    {
        var settings = new UsageStatisticsTransportSettings(new("https://telemetry.example/api/v1/reports"), "release-0.26.6", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var clock = new Clock(); var handler = new Handler(); using var sender = new UsageStatisticsSender(settings, handler, clock);
        var stats = Accumulator(); stats.Record(".txt", "ready", TimeSpan.FromSeconds(2)); var batch = stats.Capture(Settings(), "0.26.6")!;
        Assert.True(await sender.SendAsync(batch)); Assert.True(await sender.SendAsync(batch));
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        var req = handler.Requests[0];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Body))).ToLowerInvariant();
        var input = $"v1\nPOST\n/api/v1/reports\n{settings.ReleaseKeyId}\n{req.Timestamp}\n{req.Nonce}\n{hash}";
        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(settings.SigningKey, Encoding.UTF8.GetBytes(input))).ToLowerInvariant(), req.Signature);
        clock.Now = clock.Now.AddMinutes(10); Assert.True(await sender.SendAsync(batch));
        Assert.Equal(req.Nonce, handler.Requests[2].Nonce); Assert.Equal(req.Body, handler.Requests[2].Body);
        Assert.NotEqual(req.Timestamp, handler.Requests[2].Timestamp);
        clock.Now = clock.Now.AddDays(8); Assert.True(sender.IsExpired(batch)); Assert.False(await sender.SendAsync(batch)); Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(200)][InlineData(301)][InlineData(400)][InlineData(401)][InlineData(429)][InlineData(500)]
    public async Task SenderDoesNotAcknowledgeUnexpectedOrRedirectResponses(int code)
    {
        var settings = new UsageStatisticsTransportSettings(new("https://telemetry.example/api/v1/reports"), "test", new byte[32]);
        var handler = new Handler { Code = (HttpStatusCode)code }; using var sender = new UsageStatisticsSender(settings, handler, new Clock());
        var stats = Accumulator(); stats.Record(".txt", "ready", TimeSpan.Zero);
        Assert.False(await sender.SendAsync(stats.Capture(Settings(), "0.26.6")!)); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://telemetry.example/api/v1/reports")][InlineData("https://localhost/api/v1/reports")]
    [InlineData("https://telemetry.example/api/v1/reports?secret=x")][InlineData("https://alice:secret@telemetry.example/api/v1/reports")]
    [InlineData("https://telemetry.example/analytics")]
    public void InvalidTransportConfigurationNeverCreatesSender(string uri) =>
        Assert.Throws<ArgumentException>(() => new UsageStatisticsSender(new(new(uri), "test", new byte[32])));

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record Request(string Body, string Timestamp, string Nonce, string Signature);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public HttpStatusCode Code { get; init; } = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("POST", request.Method.Method);
            Assert.Null(request.Headers.Authorization); Assert.Empty(request.Headers.UserAgent); Assert.Null(request.Headers.Referrer);
            Requests.Add(new(await request.Content!.ReadAsStringAsync(token), request.Headers.GetValues("X-LocalRag-Timestamp").Single(),
                request.Headers.GetValues("X-LocalRag-Nonce").Single(), request.Headers.GetValues("X-LocalRag-Signature").Single()));
            return new(Code);
        }
    }
}
