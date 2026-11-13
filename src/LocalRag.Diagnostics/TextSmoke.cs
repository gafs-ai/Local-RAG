using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class TextSmoke
{
    public static async Task<int> RunAsync(string chat, string embedding, string root, string? tools, CancellationToken token, bool includeSingleParagraph = false, bool viaMcp = false, AiConnectionSettings? connections = null)
    {
        // Each diagnostic run owns fresh synthetic files. Never replace or scan user documents.
        var run = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        var inputs = Path.Combine(run, "inputs");
        var data = Path.Combine(run, "data");
        Directory.CreateDirectory(inputs);
        var guide = Path.Combine(inputs, "guide.txt");
        await File.WriteAllTextAsync(guide, "公開資料：庭の植物の水やり\n\n夏の鉢植えには、朝の涼しい時間に水を与えます。土の表面が乾いていることを確認し、鉢底から流れるまでゆっくり水を注ぎます。", token);
        await File.WriteAllTextAsync(Path.Combine(inputs, "train.md"), "公開資料：鉄道旅行の準備\n\n旅行前に列車の出発時刻と乗車する駅を確認します。混雑する時期は座席を予約し、切符と荷物を準備します。", token);
        await File.WriteAllTextAsync(Path.Combine(inputs, "restricted.txt"), "社外秘\n\nこれは機密検査のための架空のテスト文書です。新しい植物の栽培計画についての社内限定情報を記載しています。", token);
        if (includeSingleParagraph)
            await File.WriteAllTextAsync(Path.Combine(inputs, "monitor.txt"), "公開資料：監視試験。花壇の水やりは朝に行います。", token);
        var settings = new AppSettings
        {
            UpdateExclusionMinutes = 0, // Immediate synthetic smoke test.
            ExcludeDotHiddenEntries = false, // This isolated diagnostic intentionally uses .local fixtures.
            Folders = [new(inputs)], Ai = connections ?? new() { ChatModel = chat, EmbeddingModel = embedding },
            AutomaticMonitoring = false,
            UsageStatistics = new() { Enabled = false },
            Database = new() { Port = 18503 },
            // Multi-provider connectivity tests exercise deterministic confidentiality;
            // the original Lemonade diagnostic still exercises experimental LLM inspection.
            Confidentiality = new() { LlmEnabled = connections is null }
        };
        // Also exercise the exact old default stored by existing installations.
        if (includeSingleParagraph) settings = settings with { TextPipeline = settings.TextPipeline with { ChunkPrompt = TextRagSettings.LegacyChunkPrompt } };
        await new SettingsStore(data).SaveAsync(settings, token);
        await using var manager = new SurrealServerManager(data, tools);
        try
        {
            await manager.EnsureRunningAsync(settings.Database, new Progress<string>(Console.WriteLine), token);
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token, settings.DataSourceName);
            using var ai = new LemonadeTextAi(settings);
            var traced = new TracingAi(ai, Path.Combine(run, "synthetic-model-responses.jsonl"));
            using var service = new TextRagService(repository, traced, settings, data);
            TextCycleResult cycle = new(0, 0, 0, 0, 0, 0);
            var monitor = new PersistentMonitor(repository, MonitoringCheckpoint.ScopeFor(settings));
            await monitor.RunCycleAsync(async ct => cycle = await service.ScanAsync(new Progress<string>(Console.WriteLine), ct), token);
            if (viaMcp && cycle.Failed > 0)
            {
                Console.WriteLine("Initial cycle held invalid model output: " + JsonSerializer.Serialize(cycle));
                var initialIndexed = cycle.Indexed;
                // Exercise the same checkpoint recovery as the next scheduled cycle, without a 15-minute test delay.
                await monitor.RunCycleAsync(async ct => cycle = await service.ScanAsync(new Progress<string>(Console.WriteLine), ct), token);
                Console.WriteLine("One retry cycle: " + JsonSerializer.Serialize(cycle));
                cycle = cycle with { Indexed = cycle.Indexed + initialIndexed };
            }
            var secret = viaMcp ? await new McpTokenStore(data).GetOrCreateAsync(token) : "";
            await using var host = viaMcp ? await LocalMcpHost.StartAsync(service, settings.Mcp.Port, secret, token) : null;
            await using var client = viaMcp ? await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet", Arguments = [Path.Combine(AppContext.BaseDirectory, "LocalRag.Mcp.dll"), "--data-dir", data]
            }), cancellationToken: token) : null;
            ITextRetrieval retrieval = client is null ? service : new McpRetrieval(client);
            if (client is not null) Console.WriteLine("MCP stdio bridge -> authenticated HTTP: " + string.Join(",", (await client.ListToolsAsync(cancellationToken: token)).Select(t => t.Name)));
            Console.WriteLine("Text cycle: " + JsonSerializer.Serialize(cycle));
            var documents = await repository.DocumentsAsync(token);
            Console.WriteLine($"Diagnostic data: {run}");
            foreach (var document in documents)
                Console.WriteLine($"Document: {Path.GetFileName(document.Path)}, status={document.Status}, error={document.ErrorCode}, category={document.CategoryId}, paragraphs={document.Paragraphs?.Count}, chunks={document.Chunks?.Count}, embedded={document.EmbeddedChunks}");
            if (cycle.Indexed != (includeSingleParagraph ? 4 : 3) || cycle.Failed != 0) return 2;
            if (includeSingleParagraph)
            {
                var single = documents.Single(d => Path.GetFileName(d.Path) == "monitor.txt");
                var singleFetch = await retrieval.FetchAsync(single.DocumentId, token);
                // The inspection also sees the source path. A generated public body alone does not
                // guarantee disclosure permission; verify either exact allowed data or a closed result.
                var validDisclosure = singleFetch?.DocumentId == single.DocumentId && (singleFetch.Decision switch
                {
                    DisclosureDecision.Allowed => singleFetch.Content == single.Original && singleFetch.SourcePath == single.Path,
                    DisclosureDecision.Withheld or DisclosureDecision.Pending => singleFetch.Content is null && singleFetch.SourcePath == single.Path,
                    _ => false
                });
                Console.WriteLine($"Single paragraph fetch: decision={singleFetch?.Decision}, exact-source={singleFetch?.Content == single.Original}, valid-disclosure={validDisclosure}");
                if (!validDisclosure) return 2;
            }
            var hits = await retrieval.SearchAsync("夏の鉢植えには、いつどのように水をあげればよいですか？", 3, token);
            var nearest = hits.FirstOrDefault();
            Console.WriteLine($"Japanese search: hits={hits.Count}, first={Path.GetFileName(nearest?.SourcePath)}, decision={nearest?.Decision}");
            var documentId = documents.Single(d => d.Path == guide).DocumentId;
            var fetched = await retrieval.FetchAsync(documentId, token);
            Console.WriteLine($"Public fetch: decision={fetched?.Decision}, length={fetched?.Content?.Length ?? 0}");
            var secretId = documents.Single(d => Path.GetFileName(d.Path) == "restricted.txt").DocumentId;
            var denied = await retrieval.FetchAsync(secretId, token);
            Console.WriteLine($"Restricted fetch: decision={denied?.Decision}, content-null={denied?.Content is null}, source-provided={denied?.SourcePath is not null}");
            await File.WriteAllTextAsync(guide, "公開資料：更新された内容です。", token);
            var stale = await retrieval.FetchAsync(documentId, token);
            Console.WriteLine($"Modified-before-scan: omitted={stale is null}");
            Console.WriteLine($"Diagnostic data: {run}");
            return nearest?.DocumentId == documentId && nearest.Decision == DisclosureDecision.Allowed &&
                fetched?.Content is not null && denied?.Decision == DisclosureDecision.Withheld && denied.Content is null &&
                denied.SourcePath == documents.Single(d => d.DocumentId == secretId).Path && stale is null ? 0 : 2;
        }
        finally { await manager.ShutdownAsync(true); }
    }

    private sealed class McpRetrieval(McpClient client) : ITextRetrieval
    {
        public async Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
        {
            var result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = query, ["limit"] = limit }, cancellationToken: token);
            if (result.IsError == true) throw new InvalidOperationException("MCP search failed.");
            return Parse(result).GetProperty("results").EnumerateArray().Select(Convert).ToArray();
        }
        public async Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default)
        {
            var result = await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = documentId.ToString("D") }, cancellationToken: token);
            var json = Parse(result);
            if (result.IsError == true)
            {
                if (json.GetProperty("error").GetString() == "not_available") return null;
                throw new InvalidOperationException("MCP fetch failed.");
            }
            return Convert(json);
        }
        private static JsonElement Parse(CallToolResult result)
        {
            using var json = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
            return json.RootElement.Clone();
        }
        private static TextSearchResult Convert(JsonElement e) => new(e.GetProperty("id").GetGuid(),
            Enum.Parse<DisclosureDecision>(e.GetProperty("decision").GetString()!, true),
            e.TryGetProperty("text", out var text) ? text.GetString() : null,
            e.TryGetProperty("source", out var source) ? source.GetString() : null,
            e.TryGetProperty("start_paragraph", out var start) ? start.GetInt32() : null,
            e.TryGetProperty("end_paragraph", out var end) ? end.GetInt32() : null);
    }

    private sealed class TracingAi(ITextRagAi inner, string trace) : ITextRagAi
    {
        public async Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            var result = await inner.CompleteAsync(system, input, token);
            // This diagnostic only owns generated synthetic examples; the production service never logs model bodies.
            await File.AppendAllTextAsync(trace, JsonSerializer.Serialize(new { system, input, response = result }) + Environment.NewLine, token);
            return result;
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => inner.EmbedAsync(inputs, token);
    }
}
