using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class FeedbackTests
{
    private static AppSettings Settings(bool enabled = true, bool acknowledged = true) => new()
    {
        Management = ManagementSettings.CreateDefault(),
        UsageStatistics = new() { Enabled = enabled, NoticeAcknowledged = acknowledged },
        Ai = new() { ChatModel = "Gemma-3-4b-it-mm-NPU", EmbeddingModel = "nomic-embed-text-v2-moe-GGUF" }
    };

    private static UsageStatisticsAccumulator Accumulator(AppSettings? settings = null)
    {
        var stats = new UsageStatisticsAccumulator();
        stats.Configure((settings ?? Settings()).UsageStatistics);
        return stats;
    }

    private static UsageStatisticsTransportSettings Transport() =>
        new(new("https://telemetry.example:8443/api/v1/reports"), "0.26.6", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Theory]
    [InlineData("improvement")][InlineData("bug")][InlineData("other")]
    public void MessageOnlySubmissionContainsNoAutomaticallyCollectedUsage(string category)
    {
        var batch = FeedbackBatch.Create(category, "  改善をお願いします。\r\n  ");
        using var json = JsonDocument.Parse(batch.Json);
        Assert.Equal(new[] { "schemaVersion", "appVersion", "category", "message", "usage" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("usage").ValueKind);
        Assert.Equal("改善をお願いします。", batch.Report.Message);
        Assert.Equal(ProductInfo.Version, batch.Report.AppVersion);
        Assert.Equal(category, batch.Report.Category);
        Assert.Matches("^[0-9a-f]{32}$", batch.Nonce);
    }

    [Theory]
    [InlineData("")][InlineData(" \r\n\t ")]
    public void BlankFeedbackIsRejected(string message) =>
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", message));

    [Fact]
    public void MessageLengthIsBoundedByUtf16AndUnknownCategoryIsRejected()
    {
        var maximum = string.Concat(Enumerable.Repeat("😀", 4000));
        Assert.Equal(maximum, FeedbackBatch.Create("other", " " + maximum + " ").Report.Message);
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("other", maximum + "a"));
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("privateCategory", "message"));
    }

    [Fact]
    public void MessageRejectsControlCharactersButRetainsNormalLineBreaksAndTabs()
    {
        Assert.Equal("a\r\n\tb", FeedbackBatch.Create("bug", "a\r\n\tb").Report.Message);
        foreach (var c in new[] { '\u0000', '\u0008', '\u000B', '\u000C', '\u000E', '\u001F', '\u007F' })
            Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", "a" + c + "b"));
    }

    [Theory]
    [InlineData(false, true)][InlineData(true, false)][InlineData(false, false)]
    public void ExplicitAttachmentWorksWithNoHistoryAndDoesNotEnableAutomaticCollection(bool enabled, bool acknowledged)
    {
        var settings = Settings(enabled, acknowledged);
        var stats = Accumulator(settings);
        stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        var usage = stats.SnapshotForFeedback(settings, ProductInfo.Version,
            new("AMD Ryzen AI 5 PRO 340", "AMD Radeon 840M", "NPU Compute Accelerator Device", 32L * 1024 * 1024 * 1024));
        Assert.Empty(usage.Processing);
        Assert.Equal("AMD Ryzen AI 5 PRO 340", usage.Hardware.Cpu);
        Assert.Equal("Gemma-3-4b-it-mm-NPU", usage.Models.Language);
        Assert.False(UsageStatisticsPrivacy.ValidReport(usage));
        Assert.True(UsageStatisticsPrivacy.ValidReport(usage, allowEmptyProcessing: true));
        Assert.Empty(FeedbackBatch.Create("bug", "synthetic report", usage).Report.Usage!.Processing);
        stats.Record(".pdf", "ready", TimeSpan.FromSeconds(1));
        Assert.Empty(stats.GetCounters());
        Assert.Null(stats.Capture(settings, ProductInfo.Version));
    }

    [Fact]
    public void SnapshotIncludesAvailableCountsWithoutConsumingOrChangingAutomaticPendingBatch()
    {
        var settings = Settings(); var stats = Accumulator();
        stats.Record(".pdf", "ready", TimeSpan.FromMilliseconds(100));
        var automatic = stats.Capture(settings, ProductInfo.Version)!;
        var originalBytes = automatic.Json.ToArray();
        stats.Record(".pdf", "failed", TimeSpan.FromMilliseconds(300));
        var usage = stats.SnapshotForFeedback(settings, ProductInfo.Version);
        var row = Assert.Single(usage.Processing);
        Assert.Equal(1, row.Completed); Assert.Equal(1, row.Failed); Assert.Equal(200, row.AverageMilliseconds);
        Assert.Same(automatic, stats.Capture(settings, ProductInfo.Version));
        Assert.Equal(originalBytes, automatic.Json);
        Assert.Equal(row, Assert.Single(stats.GetCounters()));
        stats.Acknowledge(automatic);
        var remaining = Assert.Single(stats.GetCounters());
        Assert.Equal(0, remaining.Completed); Assert.Equal(1, remaining.Failed);
        Assert.Equal(300, remaining.TotalMilliseconds);
    }

    [Fact]
    public void AttachmentDoesNotCopyPathsPrivateModelsHardwareOrPromptText()
    {
        const string privateValue = "PRIVATE_EMPLOYEE_AND_CUSTOMER_NAME";
        var settings = Settings() with
        {
            DataSourceName = privateValue, Folders = [new(@"C:\" + privateValue)],
            Ai = new() { ChatModel = privateValue, EmbeddingModel = privateValue },
            Management = ManagementSettings.CreateDefault() with
            {
                UserPrompts = [new(privateValue, privateValue, PromptPurpose.Format, privateValue)],
                Categories = [new(privateValue, privateValue, privateValue)]
            }
        };
        var stats = Accumulator();
        stats.Record("." + privateValue, "ready", TimeSpan.Zero);
        var usage = stats.SnapshotForFeedback(settings, ProductInfo.Version, new(privateValue, privateValue, "Driver NPU", 0));
        var batch = FeedbackBatch.Create("improvement", "User-authored message", usage);
        Assert.DoesNotContain(privateValue, Encoding.UTF8.GetString(batch.Json));
        Assert.Equal("other", batch.Report.Usage!.Models.Language);
        Assert.Equal("unknown", batch.Report.Usage.Hardware.Cpu);
        Assert.Equal("other", Assert.Single(batch.Report.Usage.Processing).Extension);
        Assert.True(batch.Report.Usage.Settings.CategoriesChanged);
    }

    [Fact]
    public void PreconstructedInvalidAttachmentCannotBypassPrivacyOrVersionValidation()
    {
        var usage = Accumulator().SnapshotForFeedback(Settings(), ProductInfo.Version);
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", "message",
            usage with { Hardware = usage.Hardware with { Cpu = "PRIVATE_CUSTOM_NAME" } }));
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", "message",
            usage with { Models = usage.Models with { Language = "PRIVATE_MODEL" } }));
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", "message", usage with { AppVersion = "0.26.5" }));
        Assert.Throws<ArgumentException>(() => FeedbackBatch.Create("bug", "message", usage with
        { Processing = [new(".private", 1, 0, 0, 1, 1)] }));
    }

    [Fact]
    public void BatchFreezesUsageAndMessageBytesForRetries()
    {
        var settings = Settings(); var stats = Accumulator();
        stats.Record(".txt", "ready", TimeSpan.FromSeconds(1));
        var usage = stats.SnapshotForFeedback(settings, ProductInfo.Version);
        var processing = usage.Processing.ToArray();
        var batch = FeedbackBatch.Create("other", "original", usage with { Processing = processing });
        var original = batch.Json;
        processing[0] = new(".private", 42, 0, 0, 42, 1);
        var publicBytes = batch.Json; publicBytes[0] = 0;
        Assert.Equal(original, batch.Json);
        Assert.Equal(".txt", Assert.Single(batch.Report.Usage!.Processing).Extension);
        Assert.NotEqual(batch.Nonce, FeedbackBatch.Create("other", "original", usage).Nonce);
    }

    [Fact]
    public void MaximumMessageAndBoundedUsageFitFeedbackTransport()
    {
        var usage = Accumulator().SnapshotForFeedback(Settings(), ProductInfo.Version);
        var extensions = new[] { ".txt", ".md", ".markdown", ".doc", ".docx", ".docm", ".pdf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv",
            ".cs", ".py", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".c", ".h", ".rs", ".java", ".kt", ".kts", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".toml", ".ini", ".sql", ".rst", ".tex", ".tsv", "other" };
        usage = usage with
        {
            Processing = extensions.Select(e => new UsageStatisticsProcessing(e, 99998, 1, 1, 8640000000000, 86400000)).ToArray(),
            Settings = usage.Settings with { Routes = Enumerable.Range(0, 32).Select(_ =>
                new UsageStatisticsRoute("TypeScript", "Llm", "Structure", "TextOrSummary", true, true, true, 2)).ToArray() }
        };
        var batch = FeedbackBatch.Create("improvement", new string('漢', FeedbackSender.MaximumMessageCharacters), usage);
        Assert.InRange(batch.Json.Length, 48000, FeedbackSender.MaximumBodyBytes);
        Assert.Equal(48, batch.Report.Usage!.Processing.Count);
    }

    [Fact]
    public async Task ExactFeedbackPathIsSignedAndExplicitRetriesReuseNonceAndBody()
    {
        var configuration = Transport(); var clock = new Clock(); var handler = new Handler();
        using var sender = new FeedbackSender(configuration, handler, clock);
        var batch = FeedbackBatch.Create("bug", "test report", Accumulator().SnapshotForFeedback(Settings(), ProductInfo.Version));
        Assert.True(await sender.SendAsync(batch)); Assert.True(await sender.SendAsync(batch));
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        var request = handler.Requests[0];
        Assert.Equal("https://telemetry.example:8443/api/v1/feedback", request.Uri);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body))).ToLowerInvariant();
        var signed = $"v1\nPOST\n/api/v1/feedback\n{configuration.ReleaseKeyId}\n{request.Timestamp}\n{request.Nonce}\n{hash}";
        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(configuration.SigningKey, Encoding.UTF8.GetBytes(signed))).ToLowerInvariant(), request.Signature);
        clock.Now = clock.Now.AddMinutes(10);
        Assert.True(await sender.SendAsync(batch));
        Assert.Equal(request.Nonce, handler.Requests[2].Nonce); Assert.Equal(request.Body, handler.Requests[2].Body);
        Assert.NotEqual(request.Timestamp, handler.Requests[2].Timestamp);
        clock.Now = clock.Now.AddDays(8);
        Assert.True(sender.IsExpired(batch)); Assert.False(await sender.SendAsync(batch)); Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(200)][InlineData(301)][InlineData(400)][InlineData(401)][InlineData(409)][InlineData(429)][InlineData(500)]
    public async Task RejectionIsNotSuccessAndNeverAutomaticallyRetries(int status)
    {
        var handler = new Handler { Status = (HttpStatusCode)status };
        using var sender = new FeedbackSender(Transport(), handler, new Clock());
        Assert.False(await sender.SendAsync(FeedbackBatch.Create("other", "synthetic")));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://telemetry.example/api/v1/reports")][InlineData("https://localhost/api/v1/reports")]
    [InlineData("https://telemetry.example/api/v1/reports?secret=x")][InlineData("https://alice:secret@telemetry.example/api/v1/reports")]
    [InlineData("https://telemetry.example/api/v1/feedback")][InlineData("https://telemetry.example/api/v1/reports#fragment")]
    public void EndpointCannotBeRedirectedOrReconfiguredToAnUnrelatedPath(string value) =>
        Assert.Throws<ArgumentException>(() => new FeedbackSender(new(new(value), "0.26.6", new byte[32])));

    [Fact]
    public async Task CallerCancellationStopsRequestAndIsNotReportedAsAnOrdinaryFailure()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var handler = new Handler(); using var sender = new FeedbackSender(Transport(), handler, new Clock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(FeedbackBatch.Create("bug", "test"), cancelled.Token));
        Assert.Empty(handler.Requests);
        using var during = new CancellationTokenSource();
        var waiting = new WaitingHandler(); using var pendingSender = new FeedbackSender(Transport(), waiting, new Clock());
        var operation = pendingSender.SendAsync(FeedbackBatch.Create("bug", "test"), during.Token);
        await waiting.Started.Task; during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task NetworkFailureAndTimeoutPreserveBatchForExplicitRetry(bool timeout)
    {
        using var sender = new FeedbackSender(Transport(), new FailingHandler(timeout), new Clock());
        var batch = FeedbackBatch.Create("bug", "test"); var bytes = batch.Json;
        Assert.False(await sender.SendAsync(batch));
        Assert.Equal(bytes, batch.Json);
    }

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record Recorded(string Uri, string Body, string Timestamp, string Nonce, string Signature);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization); Assert.Empty(request.Headers.UserAgent); Assert.Null(request.Headers.Referrer);
            Requests.Add(new(request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.GetValues("X-LocalRag-Timestamp").Single(), request.Headers.GetValues("X-LocalRag-Nonce").Single(),
                request.Headers.GetValues("X-LocalRag-Signature").Single()));
            return new(Status);
        }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.Accepted);
        }
    }
    private sealed class FailingHandler(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(timeout ? new TaskCanceledException("synthetic timeout") : new HttpRequestException("synthetic failure"));
    }
}
