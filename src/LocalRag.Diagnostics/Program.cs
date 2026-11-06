using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Diagnostics;

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args.FirstOrDefault())
    {
        case "code-description-evaluate" when args.Length == 4:
            return await CodeDescriptionEvaluation.RunAsync(args[1], args[2], args[3], cancellation.Token);
        case "corpus-evaluate" when args.Length is 4 or 5:
            return await CorpusEvaluation.RunAsync(args[1], args[2], args[3], args.ElementAtOrDefault(4), cancellation.Token);
        case "corpus-compare" when args.Length is 4 or 5:
            return await CorpusEvaluation.RunAsync(args[1], args[2], args[3], args.ElementAtOrDefault(4), cancellation.Token, compareRetrieval: true);
        case "configured-scan" when args.Length is 2 or 3:
            return await ConfiguredScan.RunAsync(args[1], args.ElementAtOrDefault(2), cancellation.Token);
        case "configured-evaluate" when args.Length is 3 or 4:
            return await ConfiguredScan.RunAsync(args[1], args.ElementAtOrDefault(3), cancellation.Token, args[2]);
        case "mcp-check" when args.Length is 3 or 4:
            return await McpProbe.RunAsync(args[1], args[2], cancellation.Token, args.ElementAtOrDefault(3));
        case "recovery-smoke":
            return await RecoverySmoke.RunAsync(args.ElementAtOrDefault(1) ?? ".local/recovery-smoke", args.ElementAtOrDefault(2), cancellation.Token);
        case "recovery-worker" when args.Length == 4:
            return await RecoverySmoke.WorkerAsync(args[1], args[2], int.Parse(args[3]), cancellation.Token);
        case "mcp-smoke" when args.Length >= 3:
            return await TextSmoke.RunAsync(args[1], args[2], args.ElementAtOrDefault(3) ?? ".local/mcp-smoke", args.ElementAtOrDefault(4), cancellation.Token, includeSingleParagraph: true, viaMcp: true);
        case "chunk-probe" when args.Length >= 2:
            return await ChunkProbe.RunAsync(args[1], args.ElementAtOrDefault(2) ?? ".local/chunk-probe", cancellation.Token);
        case "chunk-repair-probe" when args.Length >= 2:
            return await ChunkProbe.RunAsync(args[1], args.ElementAtOrDefault(2) ?? ".local/chunk-repair-probe", cancellation.Token, injectInvalidEnd: true);
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
            Console.WriteLine("Code description comparison: code-description-evaluate <explicit-samples.json> <new-output.jsonl> <AI-settings-directory> (local model must be running; inputs/outputs are saved for human review)");
            Console.WriteLine("Explicit public corpus evaluation: corpus-evaluate <manifest.json> <dedicated-run-directory> <AI-settings-directory> [tools-directory] (writes corpus/model traces locally)");
            Console.WriteLine("Retrieval weight comparison: corpus-compare <manifest.json> <dedicated-run-directory> <AI-settings-directory> [tools-directory] (reuses the corpus index; caches identical query vectors across variants)");
            Console.WriteLine("Configured evaluation: configured-scan <dedicated-data-directory> [tools-directory] (saved settings required; close the desktop app first)");
            Console.WriteLine("With retrieval: configured-evaluate <dedicated-data-directory> <query> [tools-directory]");
            Console.WriteLine("Usage: LocalRag.Diagnostics db [data-directory] [tools-directory] | models | chat <local-model-id> | embed <local-model-id> [data-directory] [tools-directory] | text-smoke/ chunk-smoke/ mcp-smoke <chat-model-id> <embedding-model-id> [data-directory] [tools-directory] | chunk-probe/ chunk-repair-probe <chat-model-id> [data-directory] | mcp-check <data-directory> <query> [bridge-executable] | recovery-smoke [data-directory] [tools-directory]");
            return 1;
    }
}
catch (Exception ex)
{
    // Diagnostics use only synthetic inputs; do not print arbitrary provider response bodies.
    Console.Error.WriteLine($"Diagnostic failed: {ex.GetType().Name}");
    return 1;
}
