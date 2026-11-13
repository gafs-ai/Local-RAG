using System.Net;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class AiServerManagerTests
{
    private sealed class Runtime : ILemonadeRuntime
    {
        public bool Available; public bool Busy; public HttpStatusCode? Failure; public int Starts;
        public Owned? Process;
        public Task<IReadOnlyList<LemonadeModel>?> ProbeAsync(AiConnectionSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw new HttpRequestException("failure", null, failure);
            return Task.FromResult<IReadOnlyList<LemonadeModel>?>(Available || Process?.IsAlive == true ? [new("model", "test", ["chat"], true)] : null);
        }
        public Task<bool> PortOpenAsync(Uri endpoint, CancellationToken token) => Task.FromResult(Busy);
        public IOwnedLemonade Start(AiConnectionSettings settings) { Starts++; return Process = new(settings.GetBaseUri()); }
    }
    private sealed class Owned(Uri endpoint) : IOwnedLemonade
    {
        public Uri Endpoint => endpoint; public bool IsAlive { get; private set; } = true;
        public bool OwnsListener => IsAlive; public int Stops; public bool Disposed;
        public Task StopAsync(CancellationToken token) { Stops++; IsAlive = false; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
    private static AiEndpointSettings Endpoint(string id, int port, bool stop = true) => new()
    { Id = id, Name = id, BaseUrl = $"http://localhost:{port}/api/v1/", AutoStartServer = true, StopServerOnExit = stop };

    [Fact]
    public async Task EachConnectionHasIndependentStartupAndExitPreference()
    {
        var runtimes = new Dictionary<string, Runtime>();
        await using var manager = new AiServerManager((e, _) => runtimes[e.Id] = new(), TimeSpan.FromSeconds(1));
        var first = Endpoint("first", 18010); var second = Endpoint("second", 18011, false);
        await manager.EnsureRunningAsync(first, null); await manager.EnsureRunningAsync(second, null);
        await manager.EnsureRunningAsync(first, null);
        Assert.All(runtimes.Values, r => Assert.Equal(1, r.Starts));
        await manager.ShutdownAsync(new() { Connections = [first, second], ChatConnectionId = first.Id, EmbeddingConnectionId = second.Id });
        Assert.Equal(1, runtimes[first.Id].Process!.Stops); Assert.Equal(0, runtimes[second.Id].Process!.Stops);
    }

    [Fact]
    public async Task ExistingRemoteConnectionNeverStartsOrStopsAProcess()
    {
        var runtime = new Runtime { Available = true };
        await using var manager = new AiServerManager((_, _) => runtime, TimeSpan.FromSeconds(1));
        var endpoint = Endpoint("remote", 18001) with { BaseUrl = "https://remote.example/v3/", AutoStartServer = false, Provider = AiProvider.OpenVino };
        Assert.False((await manager.EnsureRunningAsync(endpoint, "key")).ManagedByApplication);
        await manager.ShutdownAsync(new() { Connections = [endpoint], ChatConnectionId = endpoint.Id, EmbeddingConnectionId = endpoint.Id });
        Assert.Equal(0, runtime.Starts); Assert.Null(runtime.Process);
        runtime.Available = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(endpoint, null));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task RemovedProfilesKeepTheirOriginalExitPreference()
    {
        var runtime = new Runtime();
        await using var manager = new AiServerManager((_, _) => runtime, TimeSpan.FromSeconds(1));
        await manager.EnsureRunningAsync(Endpoint("removed", 18001), null);
        await manager.ShutdownAsync(new()); Assert.Equal(1, runtime.Process!.Stops);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailureIsNotTreatedAsStoppedServer(HttpStatusCode failure)
    {
        var runtime = new Runtime { Failure = failure };
        await using var manager = new AiServerManager((_, _) => runtime, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.EnsureRunningAsync(Endpoint("test", 18001), null));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task BusyPortNeverLaunchesAnotherServer()
    {
        var runtime = new Runtime { Busy = true };
        await using var manager = new AiServerManager((_, _) => runtime, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(Endpoint("test", 18001), null));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public void OvmsEnvironmentIsScopedToItsChildProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "python", "Scripts")); Directory.CreateDirectory(Path.Combine(root, "espeak-ng-data"));
        var originalPath = Environment.GetEnvironmentVariable("PATH"); var originalPython = Environment.GetEnvironmentVariable("PYTHONHOME");
        var values = WindowsAiRuntime.OvmsEnvironment(Path.Combine(root, "ovms.exe"));
        Assert.Equal(Path.Combine(root, "python"), values["PYTHONHOME"]);
        Assert.StartsWith(root + Path.PathSeparator, values["PATH"]);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH")); Assert.Equal(originalPython, Environment.GetEnvironmentVariable("PYTHONHOME"));
    }
}
