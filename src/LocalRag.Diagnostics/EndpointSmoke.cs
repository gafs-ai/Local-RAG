using System.Diagnostics;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Diagnostics;

internal static class EndpointSmoke
{
    public static async Task<int> RunAsync(string configuration, string root, string? tools, CancellationToken token)
    {
        var settings = JsonSerializer.Deserialize<AiConnectionSettings>(await File.ReadAllTextAsync(configuration, token))
            ?? throw new InvalidDataException("AI configuration is missing.");
        if (settings.Validate().Count != 0 || settings.Endpoints().Any(e => !e.IsLocal || e.AutoStartServer))
            throw new InvalidDataException("Use explicit loopback test endpoints with automatic startup disabled.");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidDataException("Use a new test directory.");
        Directory.CreateDirectory(root);
        var results = new List<object>();
        var phase = "models";
        var clock = Stopwatch.StartNew();
        var exitCode = 1;
        try
        {
            phase = "existing_server_ownership";
            await using (var lifecycle = new AiServerManager())
            {
                foreach (var endpoint in settings.Endpoints())
                {
                    var status = await lifecycle.EnsureRunningAsync(endpoint, null, token: token);
                    if (status.ManagedByApplication) throw new InvalidDataException("Unexpected ownership of a pre-existing test server.");
                }
                await lifecycle.ShutdownAsync(settings with
                    { Connections = settings.Endpoints().Select(e => e with { StopServerOnExit = true }).ToList() }, token);
            }
            results.Add(new { Phase = phase, Passed = true });
            phase = "models";
            using var chat = AiModelClient.Create(settings.ChatEndpoint());
            using var embedding = AiModelClient.Create(settings.EmbeddingEndpoint());
            var chatModels = await chat.GetModelsAsync(token);
            var embeddingModels = await embedding.GetModelsAsync(token);
            results.Add(new { Phase = phase, ChatModels = chatModels, EmbeddingModels = embeddingModels });
            Console.WriteLine($"Models: chat={chatModels.Count}, embedding={embeddingModels.Count}");
            phase = "chat_json";
            var started = clock.Elapsed.TotalSeconds;
            var response = await chat.CompleteAsync(settings.ChatModel,
                "Return JSON only: {\"status\":\"ok\"}. Do not add explanations. /no_think", "Connection test. /no_think", 256, token, jsonObject: true);
            using var json = TextAiPipeline.ParseResponse(response);
            if (json.RootElement.GetProperty("status").GetString() != "ok") throw new InvalidDataException("JSON assertion failed.");
            results.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds - started, Passed = true });
            Console.WriteLine("JSON generation: passed");
            phase = "embedding";
            started = clock.Elapsed.TotalSeconds;
            var batch = await embedding.EmbedAsync(settings.EmbeddingModel,
                ["search_document: Water garden plants in the morning.", "search_document: Reserve train tickets before travelling."], token);
            if (batch.Vectors.Count != 2 || batch.Dimension == 0) throw new InvalidDataException("Embedding assertion failed.");
            results.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds - started, batch.Dimension, Passed = true });
            Console.WriteLine($"Embedding: count=2, dimension={batch.Dimension}");
            phase = "pipeline_mcp";
            started = clock.Elapsed.TotalSeconds;
            exitCode = await TextSmoke.RunAsync(settings.ChatModel, settings.EmbeddingModel, root, tools, token,
                viaMcp: true, connections: settings);
            results.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds - started, ExitCode = exitCode });
            return exitCode;
        }
        catch (Exception ex)
        {
            results.Add(new { Phase = phase, ErrorType = ex.GetType().Name,
                HttpStatus = ex is HttpRequestException http ? (int?)http.StatusCode : null });
            Console.WriteLine($"Provider test failed in {phase}: {ex.GetType().Name}");
            return 1;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(root, "endpoint-smoke-report.json"),
                JsonSerializer.Serialize(new { Version = ProductInfo.Version, ExitCode = exitCode,
                    TotalSeconds = clock.Elapsed.TotalSeconds, Results = results }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
    }
}
