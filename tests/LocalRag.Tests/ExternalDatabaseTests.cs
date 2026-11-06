using System.Net;
using System.Net.Sockets;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class ExternalDatabaseTests
{
    [Theory]
    [InlineData("ws://db.example:8000", true)]
    [InlineData("wss://db.example/rpc", true)]
    [InlineData("http://db.example:8000", false)]
    [InlineData("ws://user:password@db.example", false)]
    [InlineData("wss://db.example?token=secret", false)]
    [InlineData("wss://db.example/private", false)]
    public void ExternalEndpointValidation(string endpoint, bool valid)
    {
        Assert.Equal(valid, new DatabaseSettings { ExternalEndpoint = endpoint }.Validate().Count == 0);
        Assert.True(new DatabaseSettings().AutoStartServer);
    }

    [Fact]
    public async Task DatabaseSettingsRoundTripWithoutChangingIngestionConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        var original = new AppSettings();
        var external = original with { Database = new() { ExternalEndpoint = "wss://db.example/rpc", AutoStartServer = false } };
        await store.SaveAsync(external);
        var loaded = await store.LoadAsync();
        Assert.Equal(external.Database, loaded.Database);
        Assert.Equal(original.PipelineConfigurationId(), external.PipelineConfigurationId());
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":1,\"Database\":{\"Port\":18123}}");
        var legacy = await store.LoadAsync();
        Assert.True(legacy.Database.AutoStartServer);
        Assert.False(legacy.Database.UsesExternalEndpoint);
        Assert.Equal(18123, legacy.Database.Port);
    }

    [Fact]
    public async Task CredentialsAreSeparatedByExternalServerAndFromLocalServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var local = new ServerCredentialStore(root);
        var first = new ServerCredentialStore(root, new("wss://db.example:8000"));
        var same = new ServerCredentialStore(root, new("wss://db.example:8000/rpc"));
        var other = new ServerCredentialStore(root, new("wss://other.example:8000"));
        await local.SaveAsync(new("local", "local-secret"));
        await first.SaveAsync(new("external", "external-secret"));
        Assert.Equal("local", (await local.LoadAsync())!.Username);
        Assert.Equal("external", (await same.LoadAsync())!.Username);
        Assert.Null(await other.LoadAsync());
    }

    [Fact]
    public async Task DisabledAutoStartDoesNotDownloadOrStartServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        await using var manager = new SurrealServerManager(root);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync(new() { Port = port, AutoStartServer = false }));
        Assert.Contains("自動起動は無効", error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "tools")));
        Assert.Null(manager.Connection);
    }

    [ServerFact]
    public async Task WebSocketExternalConnectionSupportsDiskAnnAndNeverStopsServer()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var endpoint = new Uri($"ws://127.0.0.1:{fixture.Settings.Port}/rpc");
        var root = fixture.Root + "-external";
        var credentials = await new ServerCredentialStore(fixture.Root).LoadAsync();
        await new ServerCredentialStore(root, endpoint).SaveAsync(credentials!);
        await using var manager = new SurrealServerManager(root);
        var settings = new DatabaseSettings { ExternalEndpoint = endpoint.ToString(), AutoStartServer = true, StopOnExit = true };
        var status = await manager.EnsureRunningAsync(settings);
        Assert.False(status.ManagedByApplication);
        Assert.Equal(endpoint, manager.Connection!.Endpoint);
        var probe = await DatabaseProbe.RunAsync(manager.Connection);
        Assert.True(probe.DiskAnnPassed);
        Assert.True(probe.PersistencePassed);
        await manager.ShutdownAsync(true);
        await SurrealServerManager.VerifyConnectionAsync(fixture.Manager.Connection!, default);
        Assert.False(File.Exists(Path.Combine(root, "server", "ownership.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "tools")));
    }
}
