using LocalRag.Infrastructure;
using LocalRag.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// The stdio bridge never opens the database or starts a second ingestion worker.
if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1])))
{ Console.Error.WriteLine("Usage: LocalRag.Mcp [--data-dir <absolute-path>]"); return 1; }
var data = args.Length == 2 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRAG");
try
{
    var settings = await new SettingsStore(data).LoadAsync();
    if (!settings.Mcp.Enabled) throw new InvalidOperationException();
    var secret = await new McpTokenStore(data).LoadAsync();
    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(110) };
    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri($"http://127.0.0.1:{settings.Mcp.Port}/mcp"),
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
    }, http);
    await using var client = await McpClient.CreateAsync(transport);
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders(); // stdout is exclusively the MCP protocol.
    builder.Services.AddSingleton<IRetrievalCalls>(new ForwardCalls(client));
    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "LocalRAG", Version = "0.0.0" })
        .WithStdioServerTransport().WithTools<RetrievalTools>();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception) { Console.Error.WriteLine("Local RAG MCP is unavailable. Start the application and check MCP settings."); return 1; }

internal sealed class ForwardCalls(McpClient client) : IRetrievalCalls
{
    public async Task<CallToolResult> CallAsync(string name, string? query, int limit, string? id, CancellationToken token)
    {
        try
        {
            return await client.CallToolAsync(name, name == "search"
                ? new Dictionary<string, object?> { ["query"] = query, ["limit"] = limit }
                : new Dictionary<string, object?> { ["id"] = id }, cancellationToken: token).ConfigureAwait(false);
        }
        catch (Exception) { return LocalRetrievalCalls.Error("retrieval_unavailable"); }
    }
}
