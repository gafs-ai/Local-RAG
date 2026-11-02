using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class McpProbe
{
    public static async Task<int> RunAsync(string data, string query, CancellationToken token, string? executable = null)
    {
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = executable is null ? "dotnet" : Path.GetFullPath(executable),
            Arguments = executable is null
                ? [Path.Combine(AppContext.BaseDirectory, "LocalRag.Mcp.dll"), "--data-dir", Path.GetFullPath(data)]
                : ["--data-dir", Path.GetFullPath(data)]
        }), cancellationToken: token);
        Console.WriteLine("Tools: " + string.Join(',', (await client.ListToolsAsync(cancellationToken: token)).Select(t => t.Name)));
        var searched = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = query, ["limit"] = 5 }, cancellationToken: token);
        if (searched.IsError == true) { Console.WriteLine("MCP search error (content suppressed)."); return 2; }
        using var json = JsonDocument.Parse(searched.Content.OfType<TextContentBlock>().Single().Text);
        var results = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Console.WriteLine($"Search results: {results.Length}");
        foreach (var hit in results)
        {
            var id = hit.GetProperty("id").GetString();
            var fetched = await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: token);
            if (fetched.IsError == true) return 2;
            using var full = JsonDocument.Parse(fetched.Content.OfType<TextContentBlock>().Single().Text);
            var decision = full.RootElement.GetProperty("decision").GetString();
            var hasText = full.RootElement.TryGetProperty("text", out var text);
            Console.WriteLine($"Fetch: decision={decision}, characters={(hasText ? text.GetString()!.Length : 0)}");
            if (decision is "withheld" or "pending" && (hasText || full.RootElement.EnumerateObject().Count() != 2)) return 2;
        }
        return results.Length > 0 ? 0 : 2;
    }
}
