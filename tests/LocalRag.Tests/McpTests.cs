using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalRag.Tests;

public class McpTests
{
    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    internal static Task<McpClient> ConnectAsync(int port, string secret) => McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new($"http://127.0.0.1:{port}/mcp"),
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
    }));
    internal static JsonElement Body(CallToolResult result) => JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement.Clone();

    [Fact]
    public async Task RealMcpClientDiscoversOnlyReadToolsAndFetchesFullText()
    {
        var stub = new Stub();
        var port = FreePort(); var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var host = await LocalMcpHost.StartAsync(stub, port, secret);
        await using var client = await ConnectAsync(port, secret);
        var tools = await client.ListToolsAsync();
        Assert.Equal(["fetch", "search"], tools.Select(t => t.Name).Order().ToArray());
        var search = Body(await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "public" }));
        var hit = Assert.Single(search.GetProperty("results").EnumerateArray());
        Assert.Equal(stub.Id.ToString("D"), hit.GetProperty("id").GetString());
        var fetched = Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = stub.Id.ToString("D") }));
        Assert.Equal("full original text", fetched.GetProperty("text").GetString());
        Assert.Equal("public.md", fetched.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(DisclosureDecision.Withheld)]
    [InlineData(DisclosureDecision.Pending)]
    public async Task ClosedResponsesCannotLeakEvenIfAdapterSuppliesContent(DisclosureDecision decision)
    {
        var stub = new Stub { Decision = decision };
        using var calls = new LocalRetrievalCalls(stub);
        var fetch = Body(await calls.CallAsync("fetch", null, 0, stub.Id.ToString("D"), default));
        Assert.Equal(["decision", "id"], fetch.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var hits = Body(await calls.CallAsync("search", "text", 5, null, default)).GetProperty("results");
        Assert.DoesNotContain("private", hits.ToString());
        Assert.DoesNotContain("original", hits.ToString());
    }

    [Fact]
    public async Task StdioBridgeUsesProtectedLocalTokenAndForwardsReadTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        await new SettingsStore(root).SaveAsync(new AppSettings { Mcp = new() { Port = port } });
        var secret = await new McpTokenStore(root).GetOrCreateAsync();
        Assert.Equal(secret, await new McpTokenStore(root).LoadAsync());
        var stub = new Stub();
        await using var host = await LocalMcpHost.StartAsync(stub, port, secret);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [Path.Combine(AppContext.BaseDirectory, "LocalRag.Mcp.dll"), "--data-dir", root]
        }));
        Assert.Equal(2, (await client.ListToolsAsync()).Count);
        var result = Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = stub.Id.ToString("D") }));
        Assert.Equal("full original text", result.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("search", "", 5, null)]
    [InlineData("search", "ok", 0, null)]
    [InlineData("search", "ok", 11, null)]
    [InlineData("fetch", null, 0, "C:\\private\\secret.txt")]
    [InlineData("delete", null, 0, null)]
    public async Task InvalidArgumentsDoNotReachRetrieval(string name, string? query, int limit, string? id)
    {
        var stub = new Stub();
        using var calls = new LocalRetrievalCalls(stub);
        var result = await calls.CallAsync(name, query, limit, id, default);
        Assert.True(result.IsError); Assert.Equal(0, stub.Calls);
    }

    [Theory]
    [InlineData(false, null, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, "https://evil.invalid", null, HttpStatusCode.Forbidden)]
    [InlineData(true, null, "evil.invalid", HttpStatusCode.Forbidden)]
    public async Task HttpRejectsMissingTokenBrowserOriginsAndForeignHosts(bool authenticated, string? origin, string? hostHeader, HttpStatusCode expected)
    {
        var port = FreePort(); var secret = new string('a', 64);
        await using var host = await LocalMcpHost.StartAsync(new Stub(), port, secret);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        if (authenticated) request.Headers.Authorization = new("Bearer", secret);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (hostHeader is not null) request.Headers.Host = hostHeader;
        using var response = await http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ShutdownCancelsInFlightRetrievalAndDrainsBeforeReturning()
    {
        var stub = new BlockingStub(); var port = FreePort();
        var host = await LocalMcpHost.StartAsync(stub, port, new string('b', 64));
        await using var client = await ConnectAsync(port, new string('b', 64));
        var call = client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "wait" }).AsTask();
        await stub.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.DisposeAsync();
        Assert.True(stub.Exited);
        Assert.True((await call).IsError);
    }

    [Fact]
    public async Task BusyAndExceptionResponsesContainNoProviderDetails()
    {
        var stub = new BlockingStub();
        using var calls = new LocalRetrievalCalls(stub);
        using var cancel = new CancellationTokenSource();
        var one = calls.CallAsync("search", "wait", 5, null, cancel.Token);
        var two = calls.CallAsync("search", "wait", 5, null, cancel.Token);
        Assert.Equal("busy", Body(await calls.CallAsync("search", "wait", 5, null, default)).GetProperty("error").GetString());
        cancel.Cancel(); await Task.WhenAll(one, two);
        using var broken = new LocalRetrievalCalls(new Stub { Fails = true });
        var error = Body(await broken.CallAsync("search", "wait", 5, null, default));
        Assert.Equal("retrieval_unavailable", error.GetProperty("error").GetString());
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [ServerFact]
    public async Task ActualDbAndMcpHideOriginalSecretsAndRejectChangedAndDeletedDocuments()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-input"; Directory.CreateDirectory(input);
        var file = Path.Combine(input, "public.txt");
        await File.WriteAllTextAsync(file, "moon guide\n\npublic launch");
        await File.WriteAllTextAsync(Path.Combine(input, "secret.md"), "社外秘\n\nmoon private design");
        var settings = new AppSettings { Folders = [new(input)], Ai = new() { ChatModel = "test", EmbeddingModel = "test" } };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, fixture.Root);
        Assert.Equal(2, (await service.ScanAsync()).Indexed);
        var docs = await repo.DocumentsAsync(default);
        var publicId = docs.Single(d => d.Path == file).DocumentId.ToString("D");
        var secretId = docs.Single(d => d.Path != file).DocumentId.ToString("D");
        var port = FreePort(); var secret = new string('c', 64);
        await using var host = await LocalMcpHost.StartAsync(service, port, secret);
        await using var client = await ConnectAsync(port, secret);
        var hits = Body(await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "moon", ["limit"] = 10 })).GetProperty("results");
        Assert.Contains(hits.EnumerateArray(), h => h.GetProperty("id").GetString() == publicId && h.TryGetProperty("text", out _));
        var denied = Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = secretId }));
        Assert.Equal("withheld", denied.GetProperty("decision").GetString()); Assert.Equal(2, denied.EnumerateObject().Count());
        await File.WriteAllTextAsync(file, "new text");
        Assert.True((await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = publicId })).IsError);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Equal("new text", Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = publicId })).GetProperty("text").GetString());
        File.Delete(file);
        Assert.True((await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = publicId })).IsError);
        Assert.Equal(1, (await service.ScanAsync()).Deleted);
    }

    private sealed class Stub : ITextRetrieval
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DisclosureDecision Decision { get; init; } = DisclosureDecision.Allowed;
        public bool Fails { get; init; }
        public int Calls { get; private set; }
        private TextSearchResult Result() => new(Id, Decision, "full original text", "C:\\private\\public.md", null, null);
        public Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
        { Calls++; if (Fails) throw new IOException("PRIVATE SECRET PATH"); return Task.FromResult<IReadOnlyList<TextSearchResult>>([Result()]); }
        public Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default) { Calls++; return Task.FromResult<TextSearchResult?>(Result()); }
    }
    private sealed class BlockingStub : ITextRetrieval
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Exited { get; private set; }
        public async Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
        { Entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); return []; } finally { Exited = true; } }
        public Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default) => throw new NotSupportedException();
    }
}
