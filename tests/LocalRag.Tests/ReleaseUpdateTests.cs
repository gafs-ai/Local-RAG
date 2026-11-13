using System.Net;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class ReleaseUpdateTests
{
    [Theory]
    [InlineData("0.26.01", "0.26.1")]
    [InlineData("v0.26.8", "0.26.8")]
    [InlineData("1.27.10", "1.27.10")]
    public void ParsesReleaseNumberNumerically(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), ReleaseUpdate.ParseVersion(tag));

    [Theory]
    [InlineData("0.26.8-beta")]
    [InlineData("0.26")]
    [InlineData("0.26.8.1")]
    [InlineData(" 0.26.8")]
    [InlineData("0.26.-1")]
    [InlineData("0.26.9999999999999999")]
    [InlineData("0.26.8/../../malicious")]
    [InlineData("https://example.com")]
    public void RejectsUnusableVersionAndLink(string value)
    {
        Assert.Null(ReleaseUpdate.ParseVersion(value));
        Assert.Throws<InvalidDataException>(() => new ReleaseUpdate(value).Page);
    }

    [Fact]
    public async Task GithubRequestIsAnonymousBoundedAndIgnoresResponseUrls()
    {
        using var client = new ReleaseUpdateClient(new Handler(request =>
        {
            Assert.Equal(ReleaseUpdateClient.FeedUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("LocalRAG-UpdateCheck", request.Headers.UserAgent.ToString());
            return Response("""{"tag_name":"0.26.10","draft":false,"prerelease":false,"html_url":"https://evil.example/installer.exe"}""");
        }));
        var latest = await client.GetLatestAsync(default);
        Assert.Equal("https://github.com/gafs-ai/Local-RAG/releases/tag/0.26.10", latest.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"tag_name\":\"0.26.9\",\"draft\":true,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"0.26.9\",\"draft\":false,\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"0.26.9\"}")]
    [InlineData("{\"tag_name\":\"0.26.9-preview\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("[]")]
    public async Task RejectsDraftPrereleaseAndIncompleteMetadata(string json)
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => Response(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(default));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresAreNotAnUpToDateResult(int status)
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => new((HttpStatusCode)status)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync(default));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => Response(new string(' ', 300 * 1024))));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync(default));
    }

    [Fact]
    public async Task WithdrawnReleaseClearsCachedUpdateWithoutPromptingDowngrade()
    {
        var source = new Source("0.26.9"); var clock = new Clock();
        var monitor = new ReleaseUpdateMonitor(source, new Store(), "0.26.8", clock);
        Assert.NotNull((await monitor.CheckAsync(false, default)).Available);
        clock.Advance(TimeSpan.FromHours(24));
        source.Tag = "0.26.7";
        var result = await monitor.CheckAsync(false, default);
        Assert.Null(result.Available);
        Assert.False(result.Notify);
    }

    [Fact]
    public async Task ReadingCachedBannerDoesNotContactServerOrShowPopup()
    {
        var source = new Source("0.26.9");
        var store = new Store { State = new(LatestVersion: "0.26.9") };
        var monitor = new ReleaseUpdateMonitor(source, store, "0.26.8");
        var result = await monitor.CachedAsync(default);
        Assert.Equal(0, source.Calls);
        Assert.NotNull(result.Available);
        Assert.False(result.Notify);
    }

    [Fact]
    public async Task NewReleaseNotifiesOnceAcrossRestartsAndNextReleaseNotifiesAgain()
    {
        var source = new Source("0.26.10"); var store = new Store(); var clock = new Clock();
        var monitor = new ReleaseUpdateMonitor(source, store, "0.26.8", clock);
        var first = await monitor.CheckAsync(false, default);
        Assert.True(first.Notify);
        Assert.Equal("0.26.10", first.Available!.Version);
        Assert.False((await monitor.CheckAsync(false, default)).Notify);
        Assert.Equal(1, source.Calls);
        monitor = new(source, store, "0.26.8", clock);
        Assert.NotNull((await monitor.CachedAsync(default)).Available);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.False((await monitor.CheckAsync(false, default)).Notify);
        source.Tag = "0.26.11";
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await monitor.CheckAsync(false, default)).Notify);
        var upgraded = new ReleaseUpdateMonitor(source, store, "0.26.11", clock);
        Assert.Null((await upgraded.CachedAsync(default)).Available);
    }

    [Theory]
    [InlineData("0.26.01", "0.26.1")]
    [InlineData("0.26.8", "0.26.7")]
    [InlineData("0.26.8", "0.26.8")]
    public async Task OlderAndSameReleasesNeverPrompt(string installed, string latest)
    {
        var monitor = new ReleaseUpdateMonitor(new Source(latest), new Store(), installed);
        var result = await monitor.CheckAsync(false, default);
        Assert.Null(result.Available);
        Assert.False(result.Notify);
    }

    [Fact]
    public async Task AutomaticDailyChecksAndManualThrottleDoNotRepeatNetworkCalls()
    {
        var source = new Source("0.26.8"); var clock = new Clock();
        var monitor = new ReleaseUpdateMonitor(source, new Store(), "0.26.8", clock);
        await monitor.CheckAsync(false, default);
        await monitor.CheckAsync(true, default);
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(TimeSpan.FromHours(12), monitor.NextCheckDelay);
        await monitor.CheckAsync(false, default);
        Assert.Equal(1, source.Calls);
        await monitor.CheckAsync(true, default);
        Assert.Equal(2, source.Calls);
        Assert.Equal(TimeSpan.FromHours(24), monitor.NextCheckDelay);
    }

    [Fact]
    public async Task OfflineOrTimeoutRetainsBannerAndFailureAcrossRestarts()
    {
        var source = new Source("0.26.9"); var store = new Store(); var clock = new Clock();
        var monitor = new ReleaseUpdateMonitor(source, store, "0.26.8", clock);
        await monitor.CheckAsync(false, default);
        clock.Advance(TimeSpan.FromHours(24));
        source.Error = new TaskCanceledException("HTTP timeout");
        var result = await monitor.CheckAsync(false, default);
        Assert.Equal("0.26.9", result.Available!.Version);
        Assert.Contains("確認できません", result.Message);
        Assert.False(result.Notify);
        monitor = new(source, store, "0.26.8", clock);
        var cached = await monitor.CheckAsync(false, default);
        Assert.Contains("確認できません", cached.Message);
        Assert.NotNull(cached.Available);
        Assert.Equal(2, source.Calls);
        clock.Advance(TimeSpan.FromHours(24));
        source.Error = null;
        Assert.DoesNotContain("確認できません", (await monitor.CheckAsync(false, default)).Message);
    }

    [Fact]
    public async Task FirstOfflineCheckDoesNotClaimNoUpdates()
    {
        var monitor = new ReleaseUpdateMonitor(new Source("0.26.8") { Error = new HttpRequestException() }, new Store(), "0.26.8");
        var result = await monitor.CheckAsync(false, default);
        Assert.Contains("確認できません", result.Message);
        Assert.Null(result.Available);
        Assert.False(result.Notify);
    }

    [Fact]
    public async Task SimultaneousChecksShareOneRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelayedSource(entered, finish);
        var monitor = new ReleaseUpdateMonitor(source, new Store(), "0.26.8");
        var automatic = monitor.CheckAsync(false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var manual = monitor.CheckAsync(true, default);
        finish.SetResult();
        var results = await Task.WhenAll(automatic, manual);
        Assert.Equal(1, source.Calls);
        Assert.Single(results, r => r.Notify);
    }

    [Fact]
    public async Task CancellationIsPromptAndNotRecordedAsFailureOrNotification()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelayedSource(entered, new(TaskCreationOptions.RunContinuationsAsynchronously));
        var store = new Store(); var monitor = new ReleaseUpdateMonitor(source, store, "0.26.8");
        using var cancellation = new CancellationTokenSource();
        var check = monitor.CheckAsync(false, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(store.State.LastAttemptUtc);
        Assert.Null(store.State.LastNotifiedVersion);
    }

    [Fact]
    public async Task ClockMovingBackwardsDoesNotPostponeChecksForDays()
    {
        var clock = new Clock(); var source = new Source("0.26.8");
        var monitor = new ReleaseUpdateMonitor(source, new Store { State = new(clock.GetUtcNow().AddDays(10)) }, "0.26.8", clock);
        await monitor.CheckAsync(false, default);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task StateWriteFailureKeepsBannerWithoutRepeatingPopupInSameSession()
    {
        var clock = new Clock(); var monitor = new ReleaseUpdateMonitor(new Source("0.26.9"), new Store { FailWrites = true }, "0.26.8", clock);
        var result = await monitor.CheckAsync(false, default);
        Assert.True(result.Notify);
        Assert.NotNull(result.Available);
        Assert.Contains("保存できません", result.Message);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.False((await monitor.CheckAsync(false, default)).Notify);
    }

    [Fact]
    public async Task StateFileRoundTripsAndRecoversFromCorruptOrOversizedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalRag-update-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReleaseUpdateStateStore(directory);
            Assert.Equal(new(), await store.LoadAsync(default));
            var expected = new ReleaseUpdateState(DateTimeOffset.UtcNow, "0.26.9", "0.26.9");
            await store.SaveAsync(expected, default);
            Assert.Equal(expected, await store.LoadAsync(default));
            await File.WriteAllTextAsync(store.FilePath, "broken json");
            Assert.Equal(new(), await store.LoadAsync(default));
            await File.WriteAllTextAsync(store.FilePath, new string('x', 4097));
            Assert.Equal(new(), await store.LoadAsync(default));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void OldSettingsEnableUpdatesAndExplicitOptOutSurvivesRoundTrip()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.AutomaticUpdateChecks);
        var settings = new AppSettings { AutomaticUpdateChecks = false };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.False(loaded.AutomaticUpdateChecks);
        Assert.Equal(settings.PipelineConfigurationId(), (settings with { AutomaticUpdateChecks = true }).PipelineConfigurationId());
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class Source(string tag) : IReleaseUpdateSource
    {
        public int Calls { get; private set; }
        public string Tag { get; set; } = tag;
        public Exception? Error { get; set; }
        public Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken)
        { Calls++; return Error is null ? Task.FromResult(new ReleaseUpdate(Tag)) : Task.FromException<ReleaseUpdate>(Error); }
    }
    private sealed class DelayedSource(TaskCompletionSource entered, TaskCompletionSource finish) : IReleaseUpdateSource
    {
        public int Calls { get; private set; }
        public async Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken)
        { Calls++; entered.TrySetResult(); await finish.Task.WaitAsync(cancellationToken); return new("0.26.9"); }
    }
    private sealed class Store : IReleaseUpdateStateStore
    {
        public ReleaseUpdateState State { get; set; } = new();
        public bool FailWrites { get; init; }
        public Task<ReleaseUpdateState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(ReleaseUpdateState state, CancellationToken cancellationToken)
        { if (FailWrites) throw new IOException(); State = state; return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}
