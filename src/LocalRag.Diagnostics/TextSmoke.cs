using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

internal static class TextSmoke
{
    public static async Task<int> RunAsync(string chat, string embedding, string root, string? tools, CancellationToken token)
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
        var settings = new AppSettings
        {
            Folders = [new(inputs)], Ai = new() { ChatModel = chat, EmbeddingModel = embedding },
            Database = new() { Port = 18503 }
        };
        await new SettingsStore(data).SaveAsync(settings, token);
        await using var manager = new SurrealServerManager(data, tools);
        try
        {
            await manager.EnsureRunningAsync(settings.Database, new Progress<string>(Console.WriteLine), token);
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token);
            using var ai = new LemonadeTextAi(settings);
            var traced = new TracingAi(ai, Path.Combine(run, "synthetic-model-responses.jsonl"));
            using var service = new TextRagService(repository, traced, settings, data);
            var cycle = await service.ScanAsync(new Progress<string>(Console.WriteLine), token);
            Console.WriteLine("Text cycle: " + JsonSerializer.Serialize(cycle));
            var documents = await repository.DocumentsAsync(token);
            Console.WriteLine($"Diagnostic data: {run}");
            foreach (var document in documents)
                Console.WriteLine($"Document: {Path.GetFileName(document.Path)}, status={document.Status}, error={document.ErrorCode}, category={document.CategoryId}, paragraphs={document.Paragraphs?.Count}, chunks={document.Chunks?.Count}, embedded={document.EmbeddedChunks}");
            if (cycle.Indexed != 3 || cycle.Failed != 0) return 2;
            var hits = await service.SearchAsync("夏の鉢植えには、いつどのように水をあげればよいですか？", 3, token);
            var nearest = hits.FirstOrDefault();
            Console.WriteLine($"Japanese search: hits={hits.Count}, first={Path.GetFileName(nearest?.SourcePath)}, decision={nearest?.Decision}");
            var documentId = documents.Single(d => d.Path == guide).DocumentId;
            var fetched = await service.FetchAsync(documentId, token);
            Console.WriteLine($"Public fetch: decision={fetched?.Decision}, length={fetched?.Content?.Length ?? 0}");
            var secretId = documents.Single(d => Path.GetFileName(d.Path) == "restricted.txt").DocumentId;
            var denied = await service.FetchAsync(secretId, token);
            Console.WriteLine($"Restricted fetch: decision={denied?.Decision}, content-null={denied?.Content is null}, source-null={denied?.SourcePath is null}");
            await File.WriteAllTextAsync(guide, "公開資料：更新された内容です。", token);
            var stale = await service.FetchAsync(documentId, token);
            Console.WriteLine($"Modified-before-scan: omitted={stale is null}");
            Console.WriteLine($"Diagnostic data: {run}");
            return nearest?.DocumentId == documentId && nearest.Decision == DisclosureDecision.Allowed &&
                fetched?.Content is not null && denied?.Decision == DisclosureDecision.Withheld && denied.Content is null && denied.SourcePath is null && stale is null ? 0 : 2;
        }
        finally { await manager.ShutdownAsync(true); }
    }

    private sealed class TracingAi(ITextRagAi inner, string trace) : ITextRagAi
    {
        public async Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            var result = await inner.CompleteAsync(system, input, token);
            // This diagnostic only owns generated synthetic examples; the production service never logs model bodies.
            await File.AppendAllTextAsync(trace, JsonSerializer.Serialize(new { system, response = result }) + Environment.NewLine, token);
            return result;
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => inner.EmbedAsync(inputs, token);
    }
}
