using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class ParentEmbeddingTests
{
    [Fact]
    public async Task DefaultsEnableBothDocumentRoutesAndExistingSettingsRetainChildOnlySearch()
    {
        var initial = ManagementSettings.CreateDefault();
        Assert.Equal(ParentEmbeddingMode.TextOrSummary, initial.Resolve(new(), "a.md").ParentEmbedding);
        Assert.Equal(ParentEmbeddingMode.TextOrSummary, initial.Resolve(new(), "a.txt").ParentEmbedding);
        Assert.Equal(ParentEmbeddingMode.None, initial.Resolve(new(), "a.cs").ParentEmbedding);
        var old = ManagementSettings.FromLegacy(new(), new());
        Assert.Equal(ParentEmbeddingMode.None, old.Resolve(new(), "a.txt").ParentEmbedding);
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var custom = initial with { UserPrompts = [new("custom_parent", "独自の親説明", PromptPurpose.ParentDescription, "custom")],
            Routes = [initial.RouteFor("a.md") with { ParentDescriptionPromptId = "custom_parent" }] };
        await store.SaveAsync(new() { Management = custom });
        var read = await store.LoadAsync();
        Assert.Equal("custom", read.PipelineFor("a.md").ParentDescriptionPrompt);
        Assert.Equal(ParentEmbeddingMode.TextOrSummary, read.PipelineFor("a.md").ParentEmbedding);
    }

    [ServerTheory]
    [InlineData(".md", 4000, "parent_text")]
    [InlineData(".txt", 4000, "parent_text")]
    [InlineData(".md", 120, "parent_summary")]
    [InlineData(".txt", 120, "parent_summary")]
    public async Task ParentAndChildVectorsShareOriginalParentAndDoNotDuplicateResults(string extension, int budget, string kind)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var text = "## Chapter\n\n" + string.Join("\n\n", Enumerable.Range(1, 6).Select(n => $"Paragraph {n}: " + new string((char)('a' + n), 60)));
        var path = Path.Combine(input, "guide" + extension); await File.WriteAllTextAsync(path, text);
        var settings = Settings(input, extension);
        var ai = new SummaryAi(budget);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Contains(document.Chunks!, c => c.EmbeddingKind == "child");
        var parentChunk = Assert.Single(document.Chunks!, c => c.EmbeddingKind == kind);
        Assert.Equal(kind == "parent_text" ? text : "Parent explanation topic", parentChunk.Text);
        if (kind == "parent_text") Assert.Empty(ai.Calls);
        else
        {
            Assert.Contains(ai.Calls, c => c.Stage == "child");
            Assert.Contains(ai.Calls, c => c.Stage == "parent" && c.Fragment.Contains("Child explanation"));
        }
        using var calls = new LocalRetrievalCalls(service);
        var result = McpTests.Body(await calls.CallAsync("search", "chapter", 10, null, default)).GetProperty("results");
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal(text, result[0].GetProperty("text").GetString());
        Assert.Equal(kind, result[0].GetProperty("matched_embedding_kind").GetString());
        Assert.Equal(text, (await service.FetchAsync(document.DocumentId))!.Content);
        var closed = settings with { Management = settings.Management! with { Retrieval = new() { Patterns = ["bbbb"] } } };
        using var blocked = new TextRagService(repo, ai, closed, Path.Combine(fixture.Root, "data"));
        Assert.Null(Assert.Single(await blocked.SearchAsync("chapter", 10)).Content);
    }

    [ServerFact]
    public async Task ParentSummaryCheckpointsResumeWithoutRepeatingCompletedChildDescriptions()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), string.Join("\n\n", Enumerable.Range(0, 5).Select(n => new string((char)('a' + n), 65))));
        var settings = Settings(input, ".txt");
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var interrupted = new SummaryAi(120) { FailCall = 2 };
        using (var first = new TextRagService(repo, interrupted, settings, Path.Combine(fixture.Root, "data")))
            Assert.Equal(1, (await first.ScanAsync()).Failed);
        var partial = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Single(Assert.Single(partial.ParentEmbeddings).Descriptions);
        var resumedAi = new SummaryAi(120);
        using var resumed = new TextRagService(repo, resumedAi, settings, Path.Combine(fixture.Root, "data"),
            new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(6)));
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        Assert.DoesNotContain(resumedAi.Calls, c => c.Stage == "child" && c.Fragment == interrupted.Calls[0].Fragment);
        var ready = Assert.Single(await repo.DocumentsAsync(default));
        Assert.True(ready.ParentEmbeddingsCompleted);
        Assert.Single(ready.Chunks!, c => c.EmbeddingKind == "parent_summary");
        Assert.Equal(ready.Chunks!.Count, ready.EmbeddedChunks);
    }

    [ServerTheory]
    [InlineData(ParentEmbeddingMode.TextOrSummary, true)]
    [InlineData(ParentEmbeddingMode.TextWhenFits, false)]
    public async Task ImpossibleParentEmbeddingLeavesWorkingChildren(ParentEmbeddingMode mode, bool malformed)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), new string('x', 500));
        var settings = Settings(input, ".txt");
        settings = settings with { Management = settings.Management! with { Routes = [settings.Management.Routes![0] with { ParentEmbedding = mode }] } };
        var ai = new SummaryAi(120) { Malformed = malformed };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.All(document.Chunks!, c => Assert.Equal("child", c.EmbeddingKind));
        Assert.True(Assert.Single(document.ParentEmbeddings).Skipped);
        Assert.Contains("parent_embedding_skipped", document.ProcessingNotices);
        Assert.NotEmpty(await service.SearchAsync("chapter"));
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedReductionResumesAndNonShrinkingSummariesAreSkipped(bool echo)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), string.Join("\n\n", Enumerable.Range(0, 40).Select(n => $"{n}:" + new string('x', 60))));
        var settings = Settings(input, ".txt");
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new SummaryAi(120) { Echo = echo, FailCall = echo ? 0 : 42 };
        using (var first = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data")))
        {
            var cycle = await first.ScanAsync();
            Assert.Equal(echo ? 0 : 1, cycle.Failed);
        }
        if (echo)
        {
            var skipped = Assert.Single(await repo.DocumentsAsync(default));
            Assert.Equal("ready", skipped.Status);
            Assert.True(Assert.Single(skipped.ParentEmbeddings).Skipped);
            Assert.All(skipped.Chunks!, c => Assert.Equal("child", c.EmbeddingKind));
        }
        else
        {
            var partial = Assert.Single(await repo.DocumentsAsync(default));
            var checkpoint = Assert.Single(partial.ParentEmbeddings);
            Assert.NotNull(checkpoint.ReductionInput);
            Assert.True(checkpoint.Offset > 0);
            var resumeAi = new SummaryAi(120);
            using var resumed = new TextRagService(repo, resumeAi, settings, Path.Combine(fixture.Root, "data"),
                new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(6)));
            Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
            Assert.All(resumeAi.Calls, call => Assert.Equal("parent", call.Stage));
            Assert.DoesNotContain(resumeAi.Calls, call => call.Fragment == ai.Calls.Last().Fragment);
            Assert.Single(Assert.Single(await repo.DocumentsAsync(default)).Chunks!, c => c.EmbeddingKind == "parent_summary");
        }
    }

    private static AppSettings Settings(string input, string extension) => TextRagTests.Settings(input) with
    {
        TextPipeline = new() { MaxChunkCharacters = 80 },
        Management = ManagementSettings.CreateDefault() with { Routes = [new() { Extensions = [extension], SourceMode = SourceCodeMode.Text,
            ClassifyEnabled = false, FormatPromptId = "", ChildMode = ChildChunkMode.Mechanical,
            ParentMode = ParentChunkMode.Structure, ParentEmbedding = ParentEmbeddingMode.TextOrSummary }],
            Retrieval = new() { ConfidentialityEnabled = false } }
    };

    private sealed class SummaryAi(int embeddingBudget) : ITextRagAi
    {
        public int FailCall { get; init; }
        public bool Malformed { get; init; }
        public bool Echo { get; init; }
        public List<(string Stage, string Fragment)> Calls { get; } = [];
        private int _calls;
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => Task.FromResult(900);
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => Task.FromResult(embeddingBudget);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            if (++_calls == FailCall) throw new HttpRequestException("test interruption");
            using var parsed = JsonDocument.Parse(input);
            Assert.True(TextAiPipeline.InputBytes(parsed.RootElement) <= 740);
            var stage = parsed.RootElement.GetProperty("stage").GetString()!;
            Calls.Add((stage, parsed.RootElement.GetProperty("fragment").GetString()!));
            return Task.FromResult(Malformed ? "{\"description\":42}" : JsonSerializer.Serialize(new { description =
                Echo ? parsed.RootElement.GetProperty("fragment").GetString()! : stage == "child" ? "Child explanation topic" : "Parent explanation topic" }));
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            Assert.All(inputs, text => Assert.True(Encoding.UTF8.GetByteCount(text) <= embeddingBudget));
            return Task.FromResult(new VectorBatch(3, inputs.Select(text => text.EndsWith("Query: chapter", StringComparison.Ordinal) || text.StartsWith("Parent explanation") || text.Length > 120
                ? new float[] { 1, 0, 0 } : new float[] { 0, 1, 0 }).ToArray()));
        }
    }
}
