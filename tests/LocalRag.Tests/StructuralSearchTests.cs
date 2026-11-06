using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class StructuralSearchTests
{
    [Fact]
    public void ContextChangesOrderAndMissingEvidenceIsNotZero()
    {
        var scores = StructuralRanking.Calculate([
            new("isolated", "weak", .99), new("weak", null, .1),
            new("coherent", "strong", .8), new("strong", null, .8),
            new("no_parent_vector", "unembedded", .7), new("unembedded", null, null),
            new("unsupported", "strong", null)
        ], new(), false);
        Assert.True(scores["coherent"].Combined > scores["isolated"].Combined);
        Assert.Equal(.7, scores["no_parent_vector"].Combined, 8);
        Assert.Null(scores["no_parent_vector"].Parent);
        Assert.Equal(.7, scores["unembedded"].Combined, 8);
        Assert.DoesNotContain("unsupported", scores.Keys);
    }

    [Fact]
    public void ChildComponentUsesTopThreeUnitsAndNormalizesAvailableWeights()
    {
        var scores = StructuralRanking.Calculate([
            new("p", null, .4), new("a", "p", .9), new("b", "p", .8),
            new("c", "p", .7), new("d", "p", -.8)
        ], new(), true);
        Assert.Equal(.8, scores["p"].Children!.Value, 8);
        Assert.Equal(.5, scores["p"].Combined, 8);
        Assert.True(scores["p"].ExpansionLimited);
    }

    [Fact]
    public void CosineRejectsInvalidVectorsAndPreservesNegativeSimilarity()
    {
        Assert.Equal(-1, StructuralRanking.Cosine([1, 0], [-2, 0]));
        Assert.Equal(1, StructuralRanking.Cosine([2, 2], [3, 3])!.Value, 8);
        Assert.Null(StructuralRanking.Cosine([1], [1, 2]));
        Assert.Null(StructuralRanking.Cosine([1], [0]));
        Assert.Null(StructuralRanking.Cosine([float.NaN], [1]));
        Assert.Null(StructuralRanking.Cosine([1], [float.PositiveInfinity]));
    }

    [Fact]
    public async Task RetrievalSettingsRoundTripWithoutInvalidatingIndex()
    {
        var original = TextRagTests.Settings("C:\\sample") with { Management = ManagementSettings.CreateDefault() };
        Assert.True(original.Management.Retrieval.HierarchySearchEnabled);
        var modified = original with { Management = original.Management with { Retrieval = original.Management.Retrieval with
            { HierarchySearchEnabled = false, HierarchySelfWeight = .8, HierarchyParentWeight = .1, HierarchyChildWeight = .1 } } };
        Assert.Equal(original.PipelineConfigurationId(), modified.PipelineConfigurationId());
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        await store.SaveAsync(modified);
        var loaded = (await store.LoadAsync()).Management!.Retrieval;
        Assert.False(loaded.HierarchySearchEnabled);
        Assert.Equal(.8, loaded.HierarchySelfWeight);
        foreach (var value in new[] { 0, -1, double.NaN, double.PositiveInfinity, 1.1 })
            Assert.Contains((original.Management with { Retrieval = loaded with { HierarchySelfWeight = value } }).Validate(),
                message => message.Contains("構造検索"));
    }

    [ServerFact]
    public async Task ContextReranksAndChoosesParentOrChildWithNoExtraAiCalls()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var settings = Settings(fixture.Root);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var weak = await AddDocument(repo, settings, "weak.txt", [.99], .1);
        var strong = await AddDocument(repo, settings, "strong.txt", [.8], .8);
        var ai = new QueryAi();
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        var results = await service.SearchAsync("topic", 10);
        Assert.Equal(strong.DocumentId, results[0].DocumentId);
        Assert.Equal("parent", results[0].ResultUnitKind);
        Assert.Equal(strong.Original, results[0].Content);
        Assert.Contains(results, r => r.DocumentId == weak.DocumentId && r.ResultUnitKind == "child" && r.Content == "paragraph 0");
        Assert.Equal(results.Select(r => r.Ranking!.Combined).OrderDescending(), results.Select(r => r.Ranking!.Combined));
        Assert.Equal(results.Count, results.Select(r => r.ResultUnitId).Distinct().Count());
        Assert.Equal(1, ai.Calls);
        using var legacy = new TextRagService(repo, ai, settings with { Management = settings.Management! with
            { Retrieval = settings.Management.Retrieval with { HierarchySearchEnabled = false } } }, Path.Combine(fixture.Root, "data"));
        var old = await legacy.SearchAsync("topic");
        Assert.Equal(weak.DocumentId, old[0].DocumentId);
        Assert.All(old, r => Assert.Null(r.Ranking));
        Assert.Equal(weak.Original, old[0].Content);
        Assert.Equal(strong.Original, (await service.FetchAsync(strong.DocumentId))!.Content);
        await File.AppendAllTextAsync(strong.Path, "changed");
        Assert.DoesNotContain(await service.SearchAsync("topic", 10), r => r.DocumentId == strong.DocumentId);
    }

    [ServerFact]
    public async Task ExpandsBeyondAnnSeedsAndRejectsInactiveOrWrongConfigurationVectors()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var settings = Settings(fixture.Root);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var document = await AddDocument(repo, settings, "large.txt", Enumerable.Repeat(.95, 70).ToArray(), .1);
        var parentKey = document.RevisionId + "_70";
        var seeds = await repo.SearchVectorSeedsAsync(document.SpaceId!, document.ConfigurationId, [1, 0, 0], 64, default);
        Assert.Equal(64, seeds.Count);
        Assert.DoesNotContain(seeds, s => s.Chunk.Ordinal == 70);
        var related = Assert.Single(await repo.RelatedVectorsAsync(document.SpaceId!, document.ConfigurationId, [parentKey, parentKey], default));
        Assert.Equal(.1, StructuralRanking.Cosine([1, 0, 0], related.Embedding)!.Value, 6);
        Assert.Empty(await repo.RelatedVectorsAsync(document.SpaceId!, "other_config", [parentKey], default));
        var uncommitted = document with { RevisionId = "uncommitted" };
        await repo.StageChunkAsync(uncommitted, 0, [1, 0, 0], default);
        Assert.Empty(await repo.RelatedVectorsAsync(document.SpaceId!, document.ConfigurationId, ["uncommitted_0"], default));
        using var service = new TextRagService(repo, new QueryAi(), settings, Path.Combine(fixture.Root, "data"));
        var result = (await service.SearchAsync("topic"))[0];
        Assert.Equal("child", result.ResultUnitKind);
        Assert.Equal(.1, result.Ranking!.Parent!.Value, 6);
        Assert.False(result.Ranking.ExpansionLimited);
    }

    [ServerFact]
    public async Task MissingParentVectorUsesChildrenAndConfidentialityStillCoversWholeOriginal()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var settings = Settings(fixture.Root);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var document = await AddDocument(repo, settings, "secret.txt", [.9, .8], null, "CONFIDENTIAL");
        using var open = new TextRagService(repo, new QueryAi(), settings, Path.Combine(fixture.Root, "data"));
        var result = Assert.Single(await open.SearchAsync("topic"));
        Assert.Equal("parent", result.ResultUnitKind);
        Assert.Null(result.Ranking!.Self);
        Assert.Equal(.85, result.Ranking.Children!.Value, 6);
        using var closed = new TextRagService(repo, new QueryAi(), settings with { Management = settings.Management! with
            { Retrieval = settings.Management.Retrieval with { ConfidentialityEnabled = true, PatternsEnabled = true, LlmEnabled = false, Patterns = ["CONFIDENTIAL"] } } }, Path.Combine(fixture.Root, "data"));
        using var mcp = new LocalRetrievalCalls(closed);
        var output = McpTests.Body(await mcp.CallAsync("search", "topic", 10, null, default)).GetProperty("results");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal(new[] { "decision", "id", "source" }, output[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(document.Path, output[0].GetProperty("source").GetString());
        Assert.Null((await closed.FetchAsync(document.DocumentId))!.Content);
        using var openMcp = new LocalRetrievalCalls(open);
        var allowed = McpTests.Body(await openMcp.CallAsync("search", "topic", 10, null, default)).GetProperty("results")[0];
        Assert.Equal("hierarchical_cosine_v1", allowed.GetProperty("ranking").GetProperty("method").GetString());
        Assert.Equal("parent", allowed.GetProperty("result_unit_kind").GetString());
    }

    [ServerFact]
    public async Task ExpansionHasNodeLimitAndReportsPartialEvidence()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var settings = Settings(fixture.Root);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        // Every vector exists: the flag must reflect the node cap, not missing records.
        await AddDocument(repo, settings, "bounded.txt", Enumerable.Repeat(.5, 1030).ToArray(), null);
        using var service = new TextRagService(repo, new QueryAi(), settings, Path.Combine(fixture.Root, "data"));
        var results = await service.SearchAsync("topic");
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Ranking!.ExpansionLimited));
    }

    [ServerTheory]
    [InlineData(.9, false, "class")]
    [InlineData(.1, false, "function")]
    [InlineData(.9, true, "function")]
    public async Task CodeUsesDistinctUnitsAndRespectsRelevanceAndReturnLimit(double classScore, bool oversized, string expectedKind)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var settings = Settings(fixture.Root);
        settings = settings with { Management = settings.Management! with { Retrieval = settings.Management.Retrieval with { CodeResultMaxCharacters = 500 } } };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var path = Path.Combine(settings.Folders[0].Path, "sample.cs");
        var code = "class C { int F() => 1; }";
        await File.WriteAllTextAsync(path, code);
        var units = new List<SourceCodeUnit> {
            new("file", null, "file", "sample.cs", 1, 1, code) { Descriptions = ["file description"] },
            new("class", "file", "class", "C", 1, 1, oversized ? code + new string(' ', 1000) : code) { Descriptions = ["class description"] },
            new("function", "class", "function", "F", 1, 1, "int F() => 1;") { Descriptions = ["function description"] } };
        var chunks = new List<TextChunk> { new(1, 1, "file description", "file", "code_description"),
            new(1, 1, "class description", "class", "code_description"),
            new(1, 1, "function description", "function", "code_description"),
            new(1, 1, "function other fragment", "function", "code_description") };
        var state = new TextDocumentState { Key = TextRagSettings.Hash(path.ToUpperInvariant()), DocumentId = Guid.NewGuid(),
            Path = path, SourceHash = (await TextFiles.ReadAsync(path, 1048576, default)).Hash,
            ConfigurationId = settings.PipelineConfigurationId(), RevisionId = Guid.NewGuid().ToString("N"), Original = code,
            CategoryId = "source_code", Chunks = chunks, CodeUnits = units, EmbeddedChunks = chunks.Count, Dimension = 3,
            Parents = units.Select(u => new TextParentChunk(u.Id, 1, 1, u.Code, u.Name, u.Kind, 1, 1, u.ContainerId)).ToList(),
            SpaceId = settings.RetrievalPrompts().SpaceId(settings.Ai, 3) };
        await repo.EnsureSpaceAsync(state.SpaceId, 3, default);
        double[] similarities = [.9, classScore, .99, .1];
        for (var i = 0; i < chunks.Count; i++)
            await repo.StageChunkAsync(state, i, [(float)similarities[i], (float)Math.Sqrt(1 - similarities[i] * similarities[i]), 0], default);
        await repo.PublishAsync(state, default);
        using var service = new TextRagService(repo, new QueryAi(), settings, Path.Combine(fixture.Root, "data"));
        var results = await service.SearchAsync("topic", 10);
        Assert.Equal(expectedKind, results[0].SourceKind);
        var type = Assert.Single(results, r => r.SourceKind == "class");
        Assert.Equal(.99, type.Ranking!.Children!.Value, 6); // Two vectors represent one function, not two children.
        Assert.Equal(oversized ? "outline" : "full", type.ContentMode);
        Assert.Contains(results, r => r.SourceKind == "file");
        Assert.All(results, r => Assert.InRange(r.Content!.Length, 1, 500));
    }

    private static AppSettings Settings(string root)
    {
        var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        return TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() with
            { Retrieval = new() { ConfidentialityEnabled = false } } };
    }

    private static async Task<TextDocumentState> AddDocument(TextRagRepository repo, AppSettings settings, string name,
        double[] children, double? parent, string suffix = "")
    {
        var path = Path.Combine(settings.Folders[0].Path, name);
        var original = string.Join("\n\n", children.Select((_, i) => "paragraph " + i)) + "\n" + suffix;
        await File.WriteAllTextAsync(path, original);
        var read = await TextFiles.ReadAsync(path, 1048576, default);
        var chunks = children.Select((_, i) => new TextChunk(i + 1, i + 1, "paragraph " + i, "p")).ToList();
        if (parent.HasValue) chunks.Add(new(1, children.Length, original, "p", "parent_text"));
        var state = new TextDocumentState { Key = TextRagSettings.Hash(path.ToUpperInvariant()), DocumentId = Guid.NewGuid(),
            Path = path, SourceHash = read.Hash, ConfigurationId = settings.PipelineConfigurationId(), RevisionId = Guid.NewGuid().ToString("N"),
            Original = original, CategoryId = "general", Status = "ready", Chunks = chunks,
            Parents = [new("p", 1, children.Length, original, "section")], EmbeddedChunks = chunks.Count,
            SpaceId = settings.RetrievalPrompts().SpaceId(settings.Ai, 3), Dimension = 3 };
        await repo.EnsureSpaceAsync(state.SpaceId, 3, default);
        for (var i = 0; i < chunks.Count; i++)
        {
            var score = i < children.Length ? children[i] : parent!.Value;
            await repo.StageChunkAsync(state, i, [(float)score, (float)Math.Sqrt(1 - score * score), 0], default);
        }
        await repo.PublishAsync(state, default);
        return state;
    }

    private sealed class QueryAi : ITextRagAi
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException("Reranking must not call an LLM");
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        { Calls++; Assert.Single(inputs); return Task.FromResult(new VectorBatch(3, [new float[] { 1, 0, 0 }])); }
    }
}
