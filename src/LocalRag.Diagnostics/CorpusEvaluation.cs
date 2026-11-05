using System.Diagnostics;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Diagnostics;

/// <summary>Explicit evaluation corpus only; source contents and model traces stay in the chosen run directory.</summary>
internal static class CorpusEvaluation
{
    private sealed record Query(string Text, string File, string[] Evidence);
    private sealed record Manifest(string Corpus, Query[] Queries);
    public static async Task<int> RunAsync(string manifestPath, string runDirectory, string aiSettingsDirectory, string? tools, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, token))!;
        var corpus = Path.GetFullPath(manifest.Corpus, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        var run = Path.GetFullPath(runDirectory);
        Directory.CreateDirectory(run);
        var data = Path.Combine(run, "data");
        var store = new SettingsStore(data);
        // A saved evaluation settings file allows exact baseline/candidate reproduction.
        var aiSettings = (await new SettingsStore(aiSettingsDirectory).LoadAsync(token)).Ai;
        var settings = File.Exists(store.FilePath) ? await store.LoadAsync(token) : new AppSettings
        {
            Ai = aiSettings, Management = ManagementSettings.CreateDefault(), AutomaticMonitoring = false,
            UpdateExclusionMinutes = 0, ExcludeDotHiddenEntries = false, Database = new() { Port = 18645 }
        };
        settings = settings with { Folders = [new(corpus)], AutomaticMonitoring = false, UpdateExclusionMinutes = 0, ExcludeDotHiddenEntries = false };
        await store.SaveAsync(settings, token);
        await using var lemonade = new LemonadeServerManager();
        await using var server = new SurrealServerManager(data, tools);
        try
        {
            var status = await lemonade.EnsureRunningAsync(settings.Ai, new Progress<string>(Console.WriteLine), token);
            Console.WriteLine($"Models: chat={settings.Ai.ChatModel}; embedding={settings.Ai.EmbeddingModel}");
            await server.EnsureRunningAsync(settings.Database, token: token);
            await using var repository = await TextRagRepository.OpenAsync(server.Connection!, token);
            using var inner = new LemonadeTextAi(settings);
            var ai = new MeasuredAi(inner, Path.Combine(run, "trace.jsonl"));
            using var service = new TextRagService(repository, ai, settings, data);
            var timer = Stopwatch.StartNew();
            var cycle = await service.ScanAsync(new Progress<string>(Console.WriteLine), token);
            var ingestionSeconds = timer.Elapsed.TotalSeconds;
            var documents = await repository.DocumentsAsync(token);
            var ingestionCalls = ai.ChatCalls;
            var ingestionEmbeddingCalls = ai.EmbeddingCalls;
            var queries = new List<object>();
            foreach (var query in manifest.Queries)
            {
                var hits = await service.SearchAsync(query.Text, 5, token);
                var ranked = hits.Select((hit, i) => new { Rank = i + 1, File = hit.SourcePath is null ? null : Path.GetFileName(hit.SourcePath),
                    hit.Decision, hit.Content, hit.ParentChunkId }).ToArray();
                var relevant = ranked.FirstOrDefault(h => h.File == query.File && query.Evidence.All(e => h.Content?.Contains(e, StringComparison.OrdinalIgnoreCase) == true));
                queries.Add(new { query.Text, query.File, ExpectedEvidence = query.Evidence, Rank = relevant?.Rank, Results = ranked });
                Console.WriteLine($"Query {queries.Count}: evidence rank={relevant?.Rank.ToString() ?? "missing"}");
            }
            var fetches = new List<object>();
            foreach (var document in documents.Where(d => d.Status == "ready"))
            {
                var fetched = await service.FetchAsync(document.DocumentId, token);
                fetches.Add(new { File = Path.GetFileName(document.Path), fetched?.Decision, OriginalMatches = fetched?.Content == document.Original });
            }
            using var connection = new LemonadeClient(settings.Ai);
            var loadedModels = await connection.GetModelsAsync(token);
            var report = new
            {
                Timestamp = DateTimeOffset.UtcNow, settings.Ai.ChatModel, settings.Ai.EmbeddingModel,
                ChatContext = loadedModels.Single(m => m.Id == settings.Ai.ChatModel).ContextLength,
                EmbeddingContext = loadedModels.Single(m => m.Id == settings.Ai.EmbeddingModel).ContextLength,
                Configuration = settings.PipelineConfigurationId(), IngestionSeconds = ingestionSeconds,
                IngestionChatCalls = ingestionCalls, IngestionEmbeddingCalls = ingestionEmbeddingCalls,
                ai.ChatSeconds, ai.EmbeddingSeconds, cycle,
                Documents = documents.Select(d => new { File = Path.GetFileName(d.Path), d.Status, d.CategoryId, d.ErrorStage, d.ErrorDetailCode,
                    Characters = d.Original.Length, RetainedParagraphCharacters = d.Paragraphs?.Sum(p => p.Text.Length),
                    Parents = d.Parents?.Count, Chunks = d.Chunks?.Count, d.ProcessingNotices }), Queries = queries, Fetches = fetches
            };
            await File.WriteAllTextAsync(Path.Combine(run, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"Complete: indexed={cycle.Indexed}, failed={cycle.Failed}, excluded={cycle.Excluded}; seconds={ingestionSeconds:F1}; chat calls={ingestionCalls}");
            return cycle.Failed == 0 ? 0 : 2;
        }
        finally
        {
            try { await server.ShutdownAsync(true); }
            finally { await lemonade.ShutdownAsync(true); }
        }
    }

    private sealed class MeasuredAi(ITextRagAi inner, string trace) : ITextRagAi
    {
        public int ChatCalls { get; private set; }
        public int EmbeddingCalls { get; private set; }
        public double ChatSeconds { get; private set; }
        public double EmbeddingSeconds { get; private set; }
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => inner.ChatInputBudgetAsync(system, outputTokens, token);
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => inner.DocumentEmbeddingBudgetAsync(token);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => CompleteBoundedAsync(system, input, 512, token);
        public async Task<string> CompleteBoundedAsync(string system, string input, int outputTokens, CancellationToken token)
        {
            var watch = Stopwatch.StartNew(); ChatCalls++;
            try
            {
                var output = await inner.CompleteBoundedAsync(system, input, outputTokens, token);
                await File.AppendAllTextAsync(trace, JsonSerializer.Serialize(new { system, input, output, outputTokens, seconds = watch.Elapsed.TotalSeconds }) + "\n", token);
                Console.WriteLine($"AI call {ChatCalls}: {watch.Elapsed.TotalSeconds:F1}s");
                return output;
            }
            catch (Exception ex)
            {
                await File.AppendAllTextAsync(trace, JsonSerializer.Serialize(new { system, input, error = PipelineErrors.DetailCode(ex),
                    seconds = watch.Elapsed.TotalSeconds }) + "\n", CancellationToken.None);
                Console.WriteLine($"AI call {ChatCalls}: {PipelineErrors.DetailCode(ex)} ({watch.Elapsed.TotalSeconds:F1}s)");
                throw;
            }
            finally { ChatSeconds += watch.Elapsed.TotalSeconds; }
        }
        public async Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            var watch = Stopwatch.StartNew(); EmbeddingCalls++;
            try { return await inner.EmbedAsync(inputs, token); }
            finally { EmbeddingSeconds += watch.Elapsed.TotalSeconds; }
        }
    }
}
