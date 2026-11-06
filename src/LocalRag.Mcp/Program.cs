using LocalRag.Infrastructure;
using LocalRag.Core;
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
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders(); // stdout is exclusively the MCP protocol.
    // Discovery must not depend on the desktop application's startup order.
    builder.Services.AddSingleton<IRetrievalCalls>(new ForwardCalls(data));
    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "LocalRAG", Version = ProductInfo.Version })
        .WithStdioServerTransport().WithTools<RetrievalTools>();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception) { Console.Error.WriteLine("Local RAG MCP is unavailable. Start the application and check MCP settings."); return 1; }

internal sealed class ForwardCalls(string data) : IRetrievalCalls
{
    public async Task<CallToolResult> CallAsync(string name, string? query, int limit, string? id, CancellationToken token)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(110));
            // Reload on each call so app restarts, port changes and disabling MCP take effect
            // without restarting the ChatGPT plugin. Never create credentials or start the DB here.
            var settings = await new SettingsStore(data).LoadMcpAsync(bounded.Token).ConfigureAwait(false);
            if (!settings.Enabled) return LocalRetrievalCalls.Error("retrieval_unavailable");
            var secret = await new McpTokenStore(data).LoadAsync(bounded.Token).ConfigureAwait(false);
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(110) };
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{settings.Port}/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
            }, http);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: bounded.Token).ConfigureAwait(false);
            return await client.CallToolAsync(name, name == "search"
                ? new Dictionary<string, object?> { ["query"] = query, ["limit"] = limit }
                : new Dictionary<string, object?> { ["id"] = id }, cancellationToken: bounded.Token).ConfigureAwait(false);
        }
        catch (Exception) { return LocalRetrievalCalls.Error("retrieval_unavailable"); }
    }
}
