using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class ReprocessingTests
{
    private const string Text = "First paragraph describes the moon.\n\nSecond paragraph describes the sea.";
    private static AppSettings Settings(string root, ParentEmbeddingMode embedding = ParentEmbeddingMode.None) => TextRagTests.Settings(root) with
    {
        Management = ManagementSettings.CreateDefault() with
        {
            Routes = [new() { Extensions = [".txt"], SourceMode = SourceCodeMode.Text, ChildMode = ChildChunkMode.Llm,
                ParentMode = ParentChunkMode.Structure, ParentEmbedding = embedding }],
            Retrieval = new() { ConfidentialityEnabled = false }
        }
    };
    private static AppSettings Route(AppSettings s, Func<TextRoute, TextRoute> change) => s with
        { Management = s.Management! with { Routes = s.Management!.Routes!.Select(change).ToList() } };
    private static AppSettings Prompt(AppSettings s, PromptPurpose purpose, string id) => s with
        { Management = s.Management! with { UserPrompts = [.. s.Management!.UserPrompts, new(id, id, purpose, id)] } };
    private static async Task<TextCycleResult> Scan(TextRagRepository repo, AppSettings settings, RecordingAi ai, string root)
    {
        using var service = new TextRagService(repo, ai, settings, Path.Combine(root, "data"));
        return await service.ScanAsync();
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedRouteAndRetrievalChangesKeepExistingDocumentsSearchable(bool hierarchy)
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var changed = initial with { Management = initial.Management! with { Routes = [.. initial.Management.Routes!,
            new() { Extensions = [".md"], SourceMode = SourceCodeMode.Text }],
            Retrieval = initial.Management.Retrieval with { HierarchySearchEnabled = hierarchy, Patterns = ["secret"] } } };
        var ai = new RecordingAi();
        using var search = new TextRagService(repo, ai, changed, Path.Combine(f.Root, "data"));
        Assert.NotEmpty(await search.SearchAsync("moon")); // Before another scan: old global signature differs.
        var before = ai.Embeddings;
        Assert.Equal(1, (await search.ScanAsync()).Unchanged);
        Assert.Empty(ai.Calls);
        Assert.Equal(before, ai.Embeddings);
    }

    [ServerTheory]
    [InlineData("classify")]
    [InlineData("format")]
    public async Task SameStageOutputDoesNotRecomputeDownstream(string stage)
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var old = Assert.Single(await repo.DocumentsAsync(default));
        var changed = Prompt(initial, stage == "classify" ? PromptPurpose.Classification : PromptPurpose.Format, "changed");
        changed = Route(changed, r => stage == "classify" ? r with { ClassifyPromptId = "changed" } : r with { FormatPromptId = "changed" });
        var ai = new RecordingAi();
        using (var search = new TextRagService(repo, ai, changed, Path.Combine(f.Root, "data")))
            Assert.Null(await search.FetchAsync(old.DocumentId)); // Stale method is never exposed.
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Indexed);
        Assert.Equal([stage], ai.Calls);
        Assert.Equal(0, ai.Embeddings);
        var updated = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(old.DocumentId, updated.DocumentId);
        Assert.NotEqual(old.RevisionId, updated.RevisionId);
        Assert.Equal(old.Chunks, updated.Chunks);
        using var retrieval = new TextRagService(repo, ai, changed, Path.Combine(f.Root, "data"));
        Assert.Equal(Text, (await retrieval.FetchAsync(updated.DocumentId))!.Content);
    }

    [ServerFact]
    public async Task ParentGroupingChangeReusesChildGroupingAndVectors()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var ai = new RecordingAi();
        Assert.Equal(1, (await Scan(repo, Route(initial, r => r with { ParentMode = ParentChunkMode.Llm }), ai, f.Root)).Indexed);
        Assert.Equal(["parent"], ai.Calls);
        Assert.Equal(0, ai.Embeddings);
    }

    [ServerTheory]
    [InlineData(ParentEmbeddingMode.None)]
    [InlineData(ParentEmbeddingMode.TextOrSummary)]
    public async Task EmbeddingModelChangeKeepsDescriptionsAndRepacksToNewBudget(ParentEmbeddingMode parents)
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input, parents);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new() { Budget = 60 }, f.Root)).Indexed);
        var original = Assert.Single(await repo.DocumentsAsync(default));
        var changed = initial with { Ai = initial.Ai with { EmbeddingModel = "other-embedding" } };
        var ai = new RecordingAi { Budget = 20, Dimension = 4 };
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Indexed);
        Assert.Empty(ai.Calls);
        Assert.True(ai.Embeddings > 0);
        var current = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(original.PreparedChildren, current.PreparedChildren);
        Assert.Equal(original.Parents, current.Parents);
        Assert.All(current.Chunks!, c => Assert.True(Encoding.UTF8.GetByteCount(c.Text) <= 20));
        Assert.Equal(4, current.Dimension);
        Assert.NotEqual(original.SpaceId, current.SpaceId);
    }

    [ServerFact]
    public async Task OldParentDescriptionRecipeRebuildsOnlyDescriptionsAndDiscardsOldPartialProgress()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var settings = Settings(input, ParentEmbeddingMode.TextOrSummary);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, settings, new() { Budget = 60 }, f.Root)).Indexed);
        var original = Assert.Single(await repo.DocumentsAsync(default));
        var stages = new Dictionary<string, ProcessingReceipt>(original.Processing!.Stages);
        var receipt = stages["parent-description"];
        var oldStep = receipt.Step with { Method = "parent-description/v1" };
        stages["parent-description"] = receipt with { Step = oldStep };
        await repo.SaveAsync(original with {
            Processing = original.Processing with { ProfileId = "old-parent-description", Stages = stages },
            ParentEmbeddings = original.ParentEmbeddings.Select(p => p with { RecipeId = oldStep.Id,
                Completed = false, Offset = 999999, ReductionInput = "obsolete", Reduced = ["obsolete"] }).ToList()
        }, default);
        var ai = new RecordingAi { Budget = 60 };
        Assert.Equal(1, (await Scan(repo, settings, ai, f.Root)).Indexed);
        Assert.Equal(["description"], ai.Calls);
        Assert.Equal(0, ai.Embeddings); // Identical final summary and children keep their vectors.
        var current = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(original.PreparedChildren, current.PreparedChildren);
        Assert.Equal(original.Parents, current.Parents);
        Assert.Equal("parent-description/v2", current.Processing!.Stages["parent-description"].Step.Method);
        Assert.True(Assert.Single(current.ParentEmbeddings).Completed);
    }

    [ServerFact]
    public async Task CodeEmbeddingAndLanguageModelChangesHaveSeparateDependencies()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.cs"), "class C { int F() => 1; }");
        var initial = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        var first = new RecordingAi();
        Assert.Equal(1, (await Scan(repo, initial, first, f.Root)).Indexed);
        Assert.Equal(3, first.Calls.Count(c => c == "code"));
        var changed = initial with { Ai = initial.Ai with { EmbeddingModel = "other" } };
        var embedOnly = new RecordingAi();
        Assert.Equal(1, (await Scan(repo, changed, embedOnly, f.Root)).Indexed);
        Assert.Empty(embedOnly.Calls);
        Assert.True(embedOnly.Embeddings > 0);
        var language = new RecordingAi();
        changed = changed with { Ai = changed.Ai with { ChatModel = "new-language" } };
        Assert.Equal(1, (await Scan(repo, changed, language, f.Root)).Indexed);
        Assert.Equal(3, language.Calls.Count(c => c == "code"));
        Assert.Equal(0, language.Embeddings); // Identical generated descriptions reuse their vectors.
    }

    [ServerFact]
    public async Task ReembeddingFailureResumesWithoutRepeatingCompletedWork()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var changed = initial with { Ai = initial.Ai with { EmbeddingModel = "other" } };
        var failure = new RecordingAi { FailEmbeddingCall = 2 };
        Assert.Equal(1, (await Scan(repo, changed, failure, f.Root)).Failed);
        var state = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(1, state.EmbeddedChunks);
        var resumed = new RecordingAi();
        using var resumedService = new TextRagService(repo, resumed, changed, Path.Combine(f.Root, "data"),
            new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(6)));
        Assert.Equal(1, (await resumedService.ScanAsync()).Indexed);
        Assert.Empty(resumed.Calls);
        Assert.Equal(1, resumed.Embeddings);
    }

    [ServerFact]
    public async Task MatchingLegacyDataIsAdoptedWithoutCallingModels()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var settings = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, settings, new(), f.Root)).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        await repo.SaveAsync(document with { Processing = null, PreparedChildren = null, HierarchyChildren = null }, default);
        var ai = new RecordingAi();
        Assert.Equal(1, (await Scan(repo, settings, ai, f.Root)).Unchanged);
        Assert.Empty(ai.Calls);
        Assert.Equal(0, ai.Embeddings);
        var adopted = Assert.Single(await repo.DocumentsAsync(default));
        Assert.NotNull(adopted.Processing);
        Assert.Equal(document.RevisionId, adopted.RevisionId);
        var changed = Route(Prompt(settings, PromptPurpose.Classification, "new"), r => r with { ClassifyPromptId = "new" });
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Indexed);
        Assert.Equal(["classify"], ai.Calls);
        Assert.Equal(0, ai.Embeddings);
    }

    [Fact]
    public void OnlyEffectiveInputsAffectProfile()
    {
        var s = Route(Settings("C:\\input"), r => r with { ClassifyEnabled = false, FormatPromptId = "", ChildMode = ChildChunkMode.Paragraphs,
            ParentMode = ParentChunkMode.None });
        var plan = ProcessingPlan.Create(s, "a.txt", "general");
        var changed = s with { Ai = s.Ai with { ChatModel = "unused-model" },
            TextPipeline = s.TextPipeline with { ParentHeadingLevel = 5, MaxOutputTokens = 1024, MaxDocumentCharacters = 500000 } };
        changed = Route(changed, r => r with { Name = "display only", MarkdownParentMaxCharacters = 9000 });
        Assert.Equal(plan.Id, ProcessingPlan.Create(changed, "a.txt", "general").Id);
        Assert.NotEqual(plan.Id, ProcessingPlan.Create(s with { Ai = s.Ai with { EmbeddingModel = "changed" } }, "a.txt", "general").Id);
        changed = Route(s, r => r with { CategoryOverrides = [new("meeting", FormatPromptId: "system.format")] });
        Assert.Equal(plan.Id, ProcessingPlan.Create(changed, "a.txt", "general").Id);
    }

    [ServerFact]
    public async Task ParentDescriptionPromptOnlyRecomputesDescriptions()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input, ParentEmbeddingMode.TextOrSummary);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new() { Budget = 60 }, f.Root)).Indexed);
        var changed = Route(Prompt(initial, PromptPurpose.ParentDescription, "new-parent"), r => r with { ParentDescriptionPromptId = "new-parent" });
        var ai = new RecordingAi { Budget = 60 };
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Indexed);
        Assert.NotEmpty(ai.Calls);
        Assert.All(ai.Calls, c => Assert.Equal("description", c));
        Assert.Equal(0, ai.Embeddings);
    }

    [ServerTheory]
    [InlineData(ParentEmbeddingMode.TextOrSummary)]
    [InlineData(ParentEmbeddingMode.TextWhenFits)]
    public async Task SmallerModelOnlyGeneratesMissingParentDescriptionIfRequired(ParentEmbeddingMode mode)
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Settings(input, mode);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var changed = initial with { Ai = initial.Ai with { EmbeddingModel = "smaller" } };
        var ai = new RecordingAi { Budget = 20 };
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Indexed);
        if (mode == ParentEmbeddingMode.TextOrSummary) Assert.NotEmpty(ai.Calls);
        else Assert.Empty(ai.Calls);
        Assert.All(ai.Calls, c => Assert.Equal("description", c));
    }

    [ServerFact]
    public async Task ExclusionAndReenablePreserveReusableArtifacts()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var initial = Route(Settings(input), r => r with { ClassifyEnabled = false });
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, initial, new(), f.Root)).Indexed);
        var changed = initial with { Management = initial.Management! with { Categories = initial.Management.Categories
            .Select(c => c.Id == "general" ? c with { Excluded = true } : c).ToList() } };
        var ai = new RecordingAi();
        using (var search = new TextRagService(repo, ai, changed, Path.Combine(f.Root, "data")))
            Assert.Null(await search.FetchAsync(Assert.Single(await repo.DocumentsAsync(default)).DocumentId));
        Assert.Equal(1, (await Scan(repo, changed, ai, f.Root)).Excluded);
        Assert.Equal(1, (await Scan(repo, initial, ai, f.Root)).Indexed);
        Assert.Empty(ai.Calls);
        Assert.Equal(0, ai.Embeddings);
    }

    [ServerFact]
    public async Task OldDocumentsWithoutMatchingRecipeAreReprocessedConservatively()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.txt"), Text);
        var settings = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        Assert.Equal(1, (await Scan(repo, settings, new(), f.Root)).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        await repo.SaveAsync(document with { Processing = null, ConfigurationId = "unknown-old-settings" }, default);
        var ai = new RecordingAi();
        Assert.Equal(1, (await Scan(repo, settings, ai, f.Root)).Indexed);
        Assert.Equal(new[] { "classify", "format", "children" }, ai.Calls);
    }

    private sealed class RecordingAi : ITextRagAi
    {
        public List<string> Calls { get; } = [];
        public int Embeddings { get; private set; }
        public int Budget { get; init; } = 4000;
        public int Dimension { get; init; } = 3;
        public int FailEmbeddingCall { get; init; }
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => Task.FromResult(Budget);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            using var json = JsonDocument.Parse(input); var root = json.RootElement;
            if (root.TryGetProperty("document", out _)) { Calls.Add("classify"); return Task.FromResult("{\"category\":\"general\"}"); }
            if (root.TryGetProperty("language", out _)) { Calls.Add("code"); return Task.FromResult(JsonSerializer.Serialize(new { description = "description " + root.GetProperty("Kind").GetString() })); }
            if (root.TryGetProperty("fragment", out _)) { Calls.Add("description"); return Task.FromResult("{\"description\":\"Concise summary.\"}"); }
            var paragraphs = root.GetProperty("paragraphs").EnumerateArray().ToArray();
            if (root.TryGetProperty("maxCharacters", out _))
            {
                Calls.Add("children");
                return Task.FromResult(JsonSerializer.Serialize(new { chunks = paragraphs.Select(p => new { start = p.GetProperty("Id").GetInt32(), end = p.GetProperty("Id").GetInt32() }) }));
            }
            if (root.TryGetProperty("responseExample", out _))
            {
                Calls.Add("parent");
                return Task.FromResult(JsonSerializer.Serialize(new { chunks = new[] { new { start = paragraphs[0].GetProperty("Id").GetInt32(), end = paragraphs[^1].GetProperty("Id").GetInt32() } } }));
            }
            Calls.Add("format"); return Task.FromResult("{\"remove\":[]}");
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            if (++Embeddings == FailEmbeddingCall) throw new HttpRequestException("test interruption");
            return Task.FromResult(new VectorBatch(Dimension, inputs.Select(_ => Enumerable.Range(0, Dimension).Select(i => i == 0 ? 1f : 0f).ToArray()).ToArray()));
        }
    }
}
