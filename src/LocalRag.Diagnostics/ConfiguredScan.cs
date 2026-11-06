using System.Text.Json;
using LocalRag.Infrastructure;
using LocalRag.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalRag.Diagnostics;

/// <summary>Runs a saved configuration against an explicitly prepared evaluation corpus.</summary>
internal static class ConfiguredScan
{
    public static async Task<int> RunAsync(string dataDirectory, string? toolsDirectory, CancellationToken token, string? query = null)
    {
        var store = new SettingsStore(dataDirectory);
        if (!File.Exists(store.FilePath)) throw new InvalidDataException("Create an evaluation settings file first.");
        var settings = await store.LoadAsync(token);
        if (settings.AutomaticMonitoring || settings.Folders.Count == 0)
            throw new InvalidDataException("Use a dedicated configuration with monitoring off and explicit input folders.");
        await using var manager = new SurrealServerManager(dataDirectory, toolsDirectory);
        try
        {
            await manager.EnsureRunningAsync(settings.Database, token: token);
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token, settings.DataSourceName);
            using var ai = new LemonadeTextAi(settings);
            using var service = new TextRagService(repository, ai, settings, dataDirectory);
            var result = await service.ScanAsync(new Progress<string>(Console.WriteLine), token);
            var documents = await repository.DocumentsAsync(token);
            var retrieval = new List<object>();
            if (query is not null)
            {
                var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                await using var host = await LocalMcpHost.StartAsync(service, settings.Mcp.Port, secret, token);
                await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
                {
                    Endpoint = new($"http://127.0.0.1:{settings.Mcp.Port}/mcp"),
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
                }), cancellationToken: token);
                static JsonElement Body(CallToolResult response)
                {
                    if (response.IsError == true) throw new InvalidDataException("MCP evaluation failed.");
                    using var json = JsonDocument.Parse(response.Content.OfType<TextContentBlock>().Single().Text);
                    return json.RootElement.Clone();
                }
                foreach (var document in documents.Where(d => d.Status == "ready"))
                {
                    Console.WriteLine("MCP全文取得と秘匿検査を確認中…");
                    var fetched = Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = document.DocumentId.ToString("D") }, cancellationToken: token));
                    var hasText = fetched.TryGetProperty("text", out var content);
                    if (hasText && content.GetString() != document.Original)
                        throw new InvalidDataException("Fetched content did not match the original.");
                    if (!hasText && (fetched.EnumerateObject().Any(p => p.Name is not ("id" or "decision" or "source")) ||
                        fetched.GetProperty("source").GetString() != document.Path)) throw new InvalidDataException("Closed response exceeded the path-only contract.");
                    retrieval.Add(new { document.DocumentId, Decision = fetched.GetProperty("decision").GetString(), OriginalMatches = hasText });
                }
                Console.WriteLine("MCP検索と親チャンクの一致を確認中…");
                var hits = Body(await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = query, ["limit"] = 5 }, cancellationToken: token))
                    .GetProperty("results").EnumerateArray().ToArray();
                var parentIds = new HashSet<string>();
                foreach (var hit in hits)
                {
                    if (!hit.TryGetProperty("text", out var content))
                    {
                        var hiddenDocument = documents.Single(d => d.DocumentId.ToString("D") == hit.GetProperty("id").GetString());
                        if (hit.EnumerateObject().Any(p => p.Name is not ("id" or "decision" or "source")) ||
                            hit.GetProperty("source").GetString() != hiddenDocument.Path)
                            throw new InvalidDataException("Closed response exceeded the path-only contract.");
                        continue;
                    }
                    if (!settings.TextPipeline.ParentChunksEnabled) continue;
                    var document = documents.Single(d => d.DocumentId.ToString("D") == hit.GetProperty("id").GetString());
                    var parentId = hit.GetProperty("parent_chunk_id").GetString()!;
                    var parent = document.Parents!.Single(p => document.RevisionId + ":" + p.Id == parentId);
                    if (!parentIds.Add(parentId) || parent.Text != content.GetString()) throw new InvalidDataException("Parent result was duplicated or changed.");
                }
                retrieval.Add(new { ViaMcp = true, SearchCount = hits.Length, VerifiedParentCount = parentIds.Count,
                    Decisions = hits.Select(h => h.GetProperty("decision").GetString()).ToArray() });
            }
            // Do not log source contents, paths, prompts, model responses or arbitrary exception messages.
            var report = new
            {
                result,
                retrieval,
                documents = documents.Select(d => new { d.DocumentId, d.Status, d.ErrorStage, d.ErrorDetailCode,
                    Characters = d.Original.Length, Parents = d.Parents?.Count ?? 0, Chunks = d.Chunks?.Count ?? 0, d.EmbeddedChunks, d.ProcessingNotices })
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "configured-scan-report.json"), json, token);
            Console.WriteLine(json);
            return result.Failed == 0 && result.InaccessibleRoots == 0 ? 0 : 2;
        }
        finally { await manager.ShutdownAsync(true); }
    }
}
