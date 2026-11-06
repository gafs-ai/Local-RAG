using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class ServerFactAttribute : FactAttribute
{
    public ServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")))
            Skip = "実SurrealDBテストはLOCALRAG_TEST_TOOLS（検証済みバイナリのtoolsフォルダ）を指定して実行します。";
    }
}

public sealed class ServerTheoryAttribute : TheoryAttribute
{
    public ServerTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")))
            Skip = "実SurrealDBテストはLOCALRAG_TEST_TOOLSを指定して実行します。";
    }
}

internal sealed class ServerFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
    public DatabaseSettings Settings { get; }
    public SurrealServerManager Manager { get; }
    private ServerFixture()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Settings = new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        Manager = new(Root, Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS"));
    }
    public static async Task<ServerFixture> StartAsync()
    {
        var fixture = new ServerFixture();
        try { await fixture.Manager.EnsureRunningAsync(fixture.Settings); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Manager.ShutdownAsync(true); }
        finally { await Manager.DisposeAsync(); }
    }
}

public class ServerTests
{
    [Fact]
    public async Task AlteredInstallerFileIsRejectedWithoutExecutionOrOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var installer = new SurrealServerInstaller(root);
        Directory.CreateDirectory(Path.GetDirectoryName(installer.ExecutablePath)!);
        await File.WriteAllTextAsync(installer.ExecutablePath, "corrupt download");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.EnsureInstalledAsync());
        Assert.Equal("corrupt download", await File.ReadAllTextAsync(installer.ExecutablePath));
    }

    [ServerFact]
    public async Task RealDiskAnnSupportsSearchUpdateAndDelete()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var result = await DatabaseProbe.RunAsync(fixture.Manager.Connection!);
        Assert.True(result.PersistencePassed);
        Assert.True(result.DiskAnnPassed);
    }

    [ServerFact]
    public async Task KeepRunningThenAdoptAndStopOnlyOwnedServer()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var manifest = Path.Combine(fixture.Root, "server", "ownership.json");
        var ownership = JsonSerializer.Deserialize<ServerOwnership>(await File.ReadAllTextAsync(manifest))!;
        using var server = Process.GetProcessById(ownership.ProcessId);
        await fixture.Manager.ShutdownAsync(false);
        Assert.False(server.HasExited);
        var status = await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        Assert.True(status.ManagedByApplication);

        var externalRoot = Path.Combine(fixture.Root, "external-client");
        var credentials = await new ServerCredentialStore(fixture.Root).LoadAsync();
        await new ServerCredentialStore(externalRoot).SaveAsync(credentials!);
        await using (var external = new SurrealServerManager(externalRoot))
        {
            var attached = await external.EnsureRunningAsync(fixture.Settings);
            Assert.False(attached.ManagedByApplication);
            await external.ShutdownAsync(true);
            Assert.False(server.HasExited);
        }
        await fixture.Manager.ShutdownAsync(true);
        Assert.True(server.HasExited);
        Assert.False(File.Exists(manifest));
    }

    [ServerFact]
    public async Task ExistingListenerWithoutCredentialsIsNotReplaced()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new DatabaseSettings { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        await using var manager = new SurrealServerManager(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnsureRunningAsync(settings));
        await manager.ShutdownAsync(true);
        Assert.NotNull(listener.LocalEndpoint);
        Assert.False(File.Exists(Path.Combine(root, "server", "ownership.json")));
    }

    [Fact]
    public async Task CredentialsAreProtectedAndRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new ServerCredentialStore(root);
        var credentials = ServerCredentialStore.Generate();
        await store.SaveAsync(credentials);
        Assert.Equal(credentials, await store.LoadAsync());
        Assert.DoesNotContain(credentials.Password, Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(root, "server", "credentials.dpapi"))));
        Assert.DoesNotContain(credentials.Password, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root, "server", "credentials.dpapi"))));
        Assert.DoesNotContain(credentials.Password, credentials.ToString());
    }

    [Fact]
    public void OwnershipRejectsReusedPidAndDifferentExecutable()
    {
        using var process = Process.GetCurrentProcess();
        var owner = new ServerOwnership(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName, 8000);
        Assert.True(owner.Matches(process));
        Assert.False((owner with { StartUtcTicks = owner.StartUtcTicks - 1 }).Matches(process));
        Assert.False((owner with { ExecutablePath = Path.Combine(Path.GetTempPath(), "other.exe") }).Matches(process));
    }
}
