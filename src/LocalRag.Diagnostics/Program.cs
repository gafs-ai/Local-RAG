using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args.FirstOrDefault())
    {
        case "chunk-probe" when args.Length >= 2:
            return await ChunkProbe.RunAsync(args[1], args.ElementAtOrDefault(2) ?? ".local/chunk-probe", cancellation.Token);
        case "chunk-smoke" when args.Length >= 3:
            return await TextSmoke.RunAsync(args[1], args[2], args.ElementAtOrDefault(3) ?? ".local/chunk-smoke", args.ElementAtOrDefault(4), cancellation.Token, includeSingleParagraph: true);
        case "text-smoke" when args.Length >= 3:
            return await TextSmoke.RunAsync(args[1], args[2], args.ElementAtOrDefault(3) ?? ".local/text-smoke", args.ElementAtOrDefault(4), cancellation.Token);
        case "db":
        {
            var root = args.ElementAtOrDefault(1) ?? ".local/db-probe";
            await using var manager = new SurrealServerManager(root, args.ElementAtOrDefault(2));
            try
            {
                await manager.EnsureRunningAsync(new DatabaseSettings { Port = 18500 }, new Progress<string>(Console.WriteLine), cancellation.Token);
                var result = await DatabaseProbe.RunAsync(manager.Connection!, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.PersistencePassed && result.DiskAnnPassed ? 0 : 2;
            }
            finally { await manager.ShutdownAsync(true); }
        }
        case "models":
        {
            using var client = new LemonadeClient(new AiConnectionSettings());
            var models = await client.GetModelsAsync(cancellation.Token);
            foreach (var model in models)
                Console.WriteLine($"{model.Id} | {string.Join(",", model.Labels)} | local={model.VerifiedLocal}");
            return 0;
        }
        case "embed" when args.Length >= 2:
        {
            using var client = new LemonadeClient(new AiConnectionSettings());
            var batch = await client.EmbedAsync(args[1], ["Local RAG stores documents on this PC.", "A recipe for apple cake."], cancellation.Token);
            Console.WriteLine($"Embedding: model={batch.ModelId}, count={batch.Vectors.Count}, dimension={batch.Dimension}, all-finite=true");
            if (args.Length >= 3)
            {
                await using var manager = new SurrealServerManager(args[2], args.ElementAtOrDefault(3));
                try
                {
                    await manager.EnsureRunningAsync(new DatabaseSettings { Port = 18501 }, new Progress<string>(Console.WriteLine), cancellation.Token);
                    await EmbeddingProbe.RunAsync(manager.Connection!, batch, cancellation.Token);
                    Console.WriteLine("Real embeddings + DiskANN: exact-vector retrieval passed (semantic search quality is not evaluated).");
                }
                finally { await manager.ShutdownAsync(true); }
            }
            return 0;
        }
        case "chat" when args.Length == 2:
        {
            using var client = new LemonadeClient(new AiConnectionSettings());
            var result = await client.CompleteAsync(args[1], "Reply with the single word OK. Do not add explanations.",
                "Connection test.", 64, cancellation.Token);
            Console.WriteLine($"Chat response: {result}");
            return 0;
        }
        default:
            Console.WriteLine("Usage: LocalRag.Diagnostics db [data-directory] [tools-directory] | models | chat <local-model-id> | embed <local-model-id> [data-directory] [tools-directory] | text-smoke/ chunk-smoke <chat-model-id> <embedding-model-id> [data-directory] [tools-directory] | chunk-probe <chat-model-id> [data-directory]");
            return 1;
    }
}
catch (Exception ex)
{
    // Diagnostics use only synthetic inputs; do not print arbitrary provider response bodies.
    Console.Error.WriteLine($"Diagnostic failed: {ex.GetType().Name}");
    return 1;
}
