// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

internal static class ChunkProbe
{
    public static async Task<int> RunAsync(string model, string root, CancellationToken token, bool injectInvalidEnd = false)
    {
        // Only these fixed synthetic examples may be traced; no user document input is accepted.
        var run = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        Console.WriteLine($"Diagnostic data: {run}");
        var settings = new AppSettings { Ai = new() { ChatModel = model } };
        using var ai = new LemonadeTextAi(settings);
        var traced = new TracingAi(ai, Path.Combine(run, "synthetic-chunk-responses.jsonl"), injectInvalidEnd);
        var pipeline = new TextAiPipeline(traced, settings.TextPipeline);
        var failed = 0;
        List<TextParagraph>[] examples =
        [
            [new(1, "公開資料：監視試験。花壇の水やりは朝に行います。")],
            [new(3, "公開資料：監視試験。花壇の水やりは朝に行います。")],
            [new(1, "公開資料：植物の世話。"), new(3, "花壇の水やりは朝に行います。")]
        ];
        foreach (var paragraphs in examples)
        {
            var callsBefore = traced.ModelCalls;
            try
            {
                var chunks = await pipeline.ChunkAsync(paragraphs, new(), token);
                Console.WriteLine($"IDs={string.Join(',', paragraphs.Select(p => p.Id))}, real-model-calls={traced.ModelCalls - callsBefore}: {JsonSerializer.Serialize(chunks)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                // Diagnostic contains synthetic inputs only; production never persists arbitrary messages.
                Console.WriteLine($"IDs={string.Join(',', paragraphs.Select(p => p.Id))}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 2;
    }

    private sealed class TracingAi(ITextRagAi inner, string trace, bool injectInvalidEnd) : ITextRagAi
    {
        public int ModelCalls { get; private set; }
        private bool _injected;
        public async Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            var injected = injectInvalidEnd && !_injected;
            string response;
            if (injected)
            {
                _injected = true;
                response = "{\"chunks\":[{\"start\":1,\"end\":2}]}";
                Console.WriteLine("Injected synthetic invalid end=2 to exercise the correction request (not a model response).");
            }
            else
            {
                ModelCalls++;
                response = await inner.CompleteAsync(system, input, token);
            }
            await File.AppendAllTextAsync(trace, JsonSerializer.Serialize(new { system, input, response, injected }) + Environment.NewLine, token);
            return response;
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => inner.EmbedAsync(inputs, token);
    }
}
