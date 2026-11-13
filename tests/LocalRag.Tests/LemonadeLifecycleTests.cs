using System.Net;
using System.Net.Sockets;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class LemonadeLifecycleTests
{
    [Fact]
    public async Task DefaultsAndExplicitPreferencesRoundTripWithoutReembedding()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var original = await store.LoadAsync();
        Assert.True(original.Ai.AutoStartServer); Assert.True(original.Ai.StopServerOnExit);
        var settings = original with { Ai = original.Ai with { AutoStartServer = false, StopServerOnExit = false, ServerExecutablePath = @"C:\Apps\LemonadeServer.exe" } };
        await store.SaveAsync(settings);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(settings.Ai), System.Text.Json.JsonSerializer.Serialize((await store.LoadAsync()).Ai));
        Assert.Equal(original.PipelineConfigurationId(), settings.PipelineConfigurationId());
        Assert.NotEmpty((settings.Ai with { ServerExecutablePath = "relative.exe" }).Validate());
        Assert.NotEmpty((settings.Ai with { ServerExecutablePath = @"C:\test.cmd" }).Validate());
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":1,\"Ai\":{}}");
        Assert.True((await store.LoadAsync()).Ai.AutoStartServer);
    }

    [Fact]
    public async Task ExistingServerIsNeverOwnedAndModelsAreRefreshedOnEachCheck()
    {
        var runtime = new Runtime { Available = true };
        await using var manager = Create(runtime);
        var first = await manager.EnsureRunningAsync(new() { AutoStartServer = false });
        var second = await manager.EnsureRunningAsync(new());
        Assert.False(first.ManagedByApplication); Assert.False(second.ManagedByApplication);
        Assert.NotEqual(Assert.Single(first.Models).Id, Assert.Single(second.Models).Id);
        Assert.Equal(0, runtime.Starts);
        await manager.ShutdownAsync(true);
        Assert.Empty(runtime.Processes);
    }

    [Fact]
    public async Task AbsentServerStartsOnceAndStopsOnlyWhenRequested()
    {
        var runtime = new Runtime();
        await using var manager = Create(runtime);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.EnsureRunningAsync(new())));
        Assert.All(results, r => Assert.True(r.ManagedByApplication));
        Assert.Equal(1, runtime.Starts);
        await manager.ShutdownAsync(true);
        Assert.Equal(1, Assert.Single(runtime.Processes).Stops);
        await manager.ShutdownAsync(true);
        Assert.Equal(1, Assert.Single(runtime.Processes).Stops);
    }

    [Fact]
    public async Task LeaveRunningDoesNotTransferOwnershipToNextManager()
    {
        var runtime = new Runtime();
        await using (var manager = Create(runtime))
        {
            await manager.EnsureRunningAsync(new());
            await manager.ShutdownAsync(false);
        }
        Assert.True(Assert.Single(runtime.Processes).IsAlive);
        await using (var next = Create(runtime))
        {
            Assert.False((await next.EnsureRunningAsync(new())).ManagedByApplication);
            await next.ShutdownAsync(true);
        }
        Assert.Equal(0, Assert.Single(runtime.Processes).Stops);
    }

    [Fact]
    public async Task DisabledAutostartOrOccupiedPortNeverLaunches()
    {
        var runtime = new Runtime();
        await using var manager = Create(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(new() { AutoStartServer = false }));
        runtime.PortOpen = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(new()));
        Assert.Equal(0, runtime.Starts);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task HttpErrorDoesNotAuthorizeStartingASecondServer(int status)
    {
        var runtime = new Runtime { ProbeError = new HttpRequestException("test", null, (HttpStatusCode)status) };
        await using var manager = Create(runtime);
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.EnsureRunningAsync(new()));
        Assert.Equal(0, runtime.Starts);
    }

    [LemonadeFact]
    public async Task InstalledLemonadeStartsInIsolatedProfileAndOnlyOwnerStopsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "config.json"), """
            {"offline":true,"no_fetch_executables":true,"auto_check_model_updates":false,
             "auto_update_models":false,"broadcast":false,"telemetry":{"enabled":false}}
            """);
        var settings = new AiConnectionSettings
        {
            BaseUrl = $"http://localhost:{McpTests.FreePort()}/api/v1/",
            ServerExecutablePath = Environment.GetEnvironmentVariable("LOCALRAG_TEST_LEMONADE")!
        };
        var runtime = new WindowsLemonadeRuntime(root);
        await using var owner = new LemonadeServerManager(runtime, TimeSpan.FromSeconds(45));
        try
        {
            Assert.True((await owner.EnsureRunningAsync(settings)).ManagedByApplication);
            await using var other = new LemonadeServerManager();
            Assert.False((await other.EnsureRunningAsync(settings)).ManagedByApplication);
            await other.ShutdownAsync(true);
            Assert.NotNull(await runtime.ProbeAsync(settings, default));
            Assert.True((await owner.EnsureRunningAsync(settings)).ManagedByApplication);
            await owner.ShutdownAsync(true);
            Assert.False(await runtime.PortOpenAsync(settings.GetBaseUri(), default));
        }
        finally
        {
            // Startup failures can occur before the listener is ready; allow bounded cleanup
            // without masking the original failure with an immediate ownership-check failure.
            for (var attempt = 0; ; attempt++)
            {
                try { await owner.ShutdownAsync(true); break; }
                catch (InvalidOperationException) when (attempt < 10) { await Task.Delay(500); }
            }
        }
    }

    [Fact]
    public async Task TimeoutPreservesOwnershipAndRetryDoesNotDuplicate()
    {
        var runtime = new Runtime { ReadyAfterStart = false };
        await using var manager = new LemonadeServerManager(runtime, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.EnsureRunningAsync(new()));
        await Assert.ThrowsAsync<TimeoutException>(() => manager.EnsureRunningAsync(new() { BaseUrl = "http://127.0.0.1:13305/api/v1/" }));
        Assert.Equal(1, runtime.Starts);
        runtime.Available = true;
        Assert.True((await manager.EnsureRunningAsync(new())).ManagedByApplication);
        await manager.ShutdownAsync(true);
        Assert.Equal(1, Assert.Single(runtime.Processes).Stops);
    }

    [Fact]
    public async Task CancellationDuringStartupStillAllowsOwnedCleanup()
    {
        var runtime = new Runtime { ReadyAfterStart = false };
        using var cancelled = new CancellationTokenSource();
        runtime.OnStart = cancelled.Cancel;
        await using var manager = Create(runtime);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureRunningAsync(new(), token: cancelled.Token));
        await manager.ShutdownAsync(true);
        Assert.Equal(1, Assert.Single(runtime.Processes).Stops);
    }

    [Fact]
    public async Task StopFailureRetainsOwnershipForRetryAndChangedEndpointsAreTracked()
    {
        var runtime = new Runtime();
        await using var manager = Create(runtime);
        await manager.EnsureRunningAsync(new());
        runtime.Available = false;
        await manager.EnsureRunningAsync(new() { BaseUrl = "http://127.0.0.1:13306/api/v1/" });
        Assert.Equal(2, runtime.Starts);
        runtime.Processes[0].OwnsListener = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ShutdownAsync(true));
        Assert.All(runtime.Processes, p => Assert.Equal(0, p.Stops));
        runtime.Processes[0].OwnsListener = true;
        await manager.ShutdownAsync(true);
        Assert.All(runtime.Processes, p => Assert.Equal(1, p.Stops));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeListenerOwnershipDistinguishesPidAndFreePort(bool ipv6)
    {
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(WindowsListenerOwner.IsOnlyOwner(port, Environment.ProcessId));
        Assert.False(WindowsListenerOwner.IsOnlyOwner(port, -1));
        listener.Stop();
        Assert.False(WindowsListenerOwner.IsOnlyOwner(port, Environment.ProcessId));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task RealClosedPortIsRecognizedAsAbsent(string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = new AiConnectionSettings { BaseUrl = $"http://{host}:{port}/api/v1/" };
        var runtime = new WindowsLemonadeRuntime();
        Assert.Null(await runtime.ProbeAsync(settings, default));
        Assert.False(await runtime.PortOpenAsync(settings.GetBaseUri(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalhostPortCheckDetectsEitherAddressFamilyWithoutConnecting(bool ipv6)
    {
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new Uri($"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/api/v1/");
        var runtime = new WindowsLemonadeRuntime();
        Assert.True(await runtime.PortOpenAsync(endpoint, default));
        Assert.False(listener.Pending()); // No connection attempt needed for the occupancy check.
        listener.Stop();
        Assert.False(await runtime.PortOpenAsync(endpoint, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.PortOpenAsync(endpoint, cancelled.Token));
    }

    private static LemonadeServerManager Create(Runtime runtime) => new(runtime, TimeSpan.FromSeconds(1));
    private sealed class Runtime : ILemonadeRuntime
    {
        public bool Available { get; set; }
        public bool PortOpen { get; set; }
        public bool ReadyAfterStart { get; set; } = true;
        public Action? OnStart { get; set; }
        public Exception? ProbeError { get; set; }
        public int Starts { get; private set; }
        public int Probes { get; private set; }
        public List<Owned> Processes { get; } = [];
        public Task<IReadOnlyList<LemonadeModel>?> ProbeAsync(AiConnectionSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Probes++;
            if (ProbeError is not null) throw ProbeError;
            return Task.FromResult<IReadOnlyList<LemonadeModel>?>(Available ? [new($"model-{Probes}", "llamacpp", ["chat"], true)] : null);
        }
        public Task<bool> PortOpenAsync(Uri endpoint, CancellationToken token) => Task.FromResult(PortOpen);
        public IOwnedLemonade Start(AiConnectionSettings settings)
        {
            Starts++; Available = ReadyAfterStart;
            var process = new Owned(settings.GetBaseUri()); Processes.Add(process);
            OnStart?.Invoke(); return process;
        }
    }
    private sealed class Owned(Uri endpoint) : IOwnedLemonade
    {
        public Uri Endpoint => endpoint;
        public bool IsAlive { get; private set; } = true;
        public bool OwnsListener { get; set; } = true;
        public int Stops { get; private set; }
        public Task StopAsync(CancellationToken token)
        {
            if (!OwnsListener) throw new InvalidOperationException();
            Stops++; IsAlive = false; return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
