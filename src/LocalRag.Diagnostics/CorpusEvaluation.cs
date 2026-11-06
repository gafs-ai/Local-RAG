using System.Diagnostics;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Diagnostics;

/// <summary>Explicit evaluation corpus only; source contents and model traces stay in the chosen run directory.</summary>
internal static class CorpusEvaluation
{
    private sealed record Query(string Text, string File, string[] Evidence, string Split = "calibration");
    private sealed record Manifest(string Corpus, Query[] Queries);
    public static async Task<int> RunAsync(string manifestPath, string runDirectory, string aiSettingsDirectory, string? tools, CancellationToken token, bool compareRetrieval = false)
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
            var comparison = compareRetrieval ? await CompareAsync(repository, ai, settings, data, manifest.Queries, token) : null;
            var queries = new List<object>();
            foreach (var query in compareRetrieval ? [] : manifest.Queries)
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
                    Parents = d.Parents?.Count, Chunks = d.Chunks?.Count, d.ProcessingNotices }), Queries = queries, Fetches = fetches,
                Comparison = comparison
            };
            await File.WriteAllTextAsync(Path.Combine(run, compareRetrieval ? "comparison.json" : "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"Complete: indexed={cycle.Indexed}, failed={cycle.Failed}, excluded={cycle.Excluded}; seconds={ingestionSeconds:F1}; chat calls={ingestionCalls}");
            return cycle.Failed == 0 ? 0 : 2;
        }
        finally
        {
            try { await server.ShutdownAsync(true); }
            finally { await lemonade.ShutdownAsync(true); }
        }
    }

    private sealed record EvaluationHit(int Rank, string? File, string? Kind, string? Content, StructuralSearchScore? Ranking, bool Relevant);
    private sealed record EvaluationQuery(string Text, string File, string Split, int? EvidenceRank, int? FileRank,
        int Characters, int UniqueFiles, double Seconds, EvaluationHit[] Hits);
    private static async Task<object> CompareAsync(TextRagRepository repository, ITextRagAi inner, AppSettings settings,
        string data, Query[] queries, CancellationToken token)
    {
        var management = settings.Management ?? ManagementSettings.FromLegacy(settings.TextPipeline, settings.Confidentiality);
        var baseline = management.Retrieval;
        var variants = new[] {
            (Name: "legacy", Options: baseline with { HierarchySearchEnabled = false }),
            (Name: "default_60_20_20", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .6, HierarchyParentWeight = .2, HierarchyChildWeight = .2 }),
            (Name: "self_80_10_10", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .8, HierarchyParentWeight = .1, HierarchyChildWeight = .1 }),
            (Name: "self_80_parent_20", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .8, HierarchyParentWeight = .2, HierarchyChildWeight = 0 }),
            (Name: "self_only", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = 1, HierarchyParentWeight = 0, HierarchyChildWeight = 0 }),
            (Name: "self_40_parent_20_child_40", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .4, HierarchyParentWeight = .2, HierarchyChildWeight = .4 }),
            (Name: "self_30_parent_10_child_60", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .3, HierarchyParentWeight = .1, HierarchyChildWeight = .6 }),
            (Name: "self_20_parent_20_child_60", Options: baseline with { HierarchySearchEnabled = true, HierarchySelfWeight = .2, HierarchyParentWeight = .2, HierarchyChildWeight = .6 }) };
        var cache = new QueryCacheAi(inner);
        foreach (var query in queries)
            await cache.EmbedAsync([EmbeddingInput.Query(settings.Ai.EmbeddingModel, query.Text)], token);
        Console.WriteLine($"Prepared {cache.Calls} query vectors; all variants reuse these exact vectors.");
        var reports = new List<object>();
        foreach (var variant in variants)
        {
            using var search = new TextRagService(repository, cache, settings with { Management = management with { Retrieval = variant.Options } }, data);
            var rows = new List<EvaluationQuery>();
            foreach (var query in queries)
            {
                var watch = Stopwatch.StartNew();
                var hits = await search.SearchAsync(query.Text, 5, token);
                var ranked = hits.Select((h, i) => new EvaluationHit(i + 1, Path.GetFileName(h.SourcePath), h.ResultUnitKind ?? h.SourceKind,
                    h.Content, h.Ranking, Path.GetFileName(h.SourcePath) == query.File &&
                    query.Evidence.All(e => h.Content?.Contains(e, StringComparison.OrdinalIgnoreCase) == true))).ToArray();
                rows.Add(new(query.Text, query.File, query.Split, ranked.FirstOrDefault(h => h.Relevant)?.Rank,
                    ranked.FirstOrDefault(h => h.File == query.File)?.Rank, ranked.Sum(h => h.Content?.Length ?? 0),
                    ranked.Select(h => h.File).Distinct().Count(), watch.Elapsed.TotalSeconds, ranked));
                Console.WriteLine($"{variant.Name} [{query.Split}] {rows.Count}/{queries.Length}: evidence rank={rows[^1].EvidenceRank?.ToString() ?? "missing"}");
            }
            var metrics = rows.GroupBy(q => q.Split).Select(g => new { Split = g.Key, Count = g.Count(),
                EvidenceAt1 = g.Count(q => q.EvidenceRank == 1), EvidenceAt5 = g.Count(q => q.EvidenceRank is not null),
                FileAt1 = g.Count(q => q.FileRank == 1), MrrAt5 = g.Average(q => q.EvidenceRank is { } rank ? 1d / rank : 0),
                AverageCharacters = g.Average(q => q.Characters), AverageUniqueFiles = g.Average(q => q.UniqueFiles) }).ToArray();
            reports.Add(new { variant.Name, variant.Options.HierarchySelfWeight, variant.Options.HierarchyParentWeight,
                variant.Options.HierarchyChildWeight, Metrics = metrics, Queries = rows });
        }
        return new { StructuralRanking.ParentPreferenceMargin, QueryEmbeddingCalls = cache.Calls, Variants = reports };
    }

    // Used only after ingestion, so the cache contains query vectors, not document text.
    private sealed class QueryCacheAi(ITextRagAi inner) : ITextRagAi
    {
        private readonly Dictionary<string, VectorBatch> _vectors = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => inner.CompleteAsync(system, input, token);
        public Task<string> CompleteBoundedAsync(string system, string input, int outputTokens, CancellationToken token) => inner.CompleteBoundedAsync(system, input, outputTokens, token);
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => inner.DocumentEmbeddingBudgetAsync(token);
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => inner.ChatInputBudgetAsync(system, outputTokens, token);
        public async Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            if (inputs.Count != 1) throw new InvalidOperationException("Query-only cache");
            token.ThrowIfCancellationRequested();
            if (_vectors.TryGetValue(inputs[0], out var cached)) return cached;
            Calls++;
            return _vectors[inputs[0]] = await inner.EmbedAsync(inputs, token);
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
