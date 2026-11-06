using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class ParentChunkTests
{
    [Fact]
    public void MarkdownSectionsPreserveOriginalAndNestedHierarchyWithoutBlankLines()
    {
        const string source = "導入\r\n# Manual\r\n概要\r\n## A\r\n本文A\r\n### Detail\r\n詳細\r\n## B\r\n本文B\r\n";
        var structure = TextStructure.Build(source, "guide.md", 2);
        Assert.Equal(source, string.Concat(structure.Parents.Select(p => p.Text)));
        Assert.Equal(4, structure.Parents.Count);
        Assert.Equal("Manual / A", structure.Parents[2].Title);
        Assert.Contains("### Detail", structure.Parents[2].Text);
        Assert.DoesNotContain("## B", structure.Parents[2].Text);
        Assert.Equal(Enumerable.Range(1, structure.Paragraphs.Count), structure.Paragraphs.Select(p => p.Id));
        Assert.All(structure.Paragraphs, p => Assert.Single(structure.Parents, s => p.Id >= s.Start && p.Id <= s.End));
        Assert.Equal(2, TextStructure.Build(source, "guide.md", 1).Parents.Count);
        Assert.Equal(5, TextStructure.Build(source, "guide.md", 3).Parents.Count);
    }

    [Fact]
    public void SetextAndRepeatedTitlesHaveStableDistinctParents()
    {
        const string source = "Manual\n======\nintro\n\nA\n---\nfirst\n\nA\n---\nsecond";
        var parsed = TextStructure.Build(source, "guide.MARKDOWN", 2);
        Assert.Equal(3, parsed.Parents.Count);
        Assert.Equal("Manual / A", parsed.Parents[1].Title);
        Assert.Equal(parsed.Parents[1].Title, parsed.Parents[2].Title);
        Assert.NotEqual(parsed.Parents[1].Id, parsed.Parents[2].Id);
        Assert.Equal(source, string.Concat(parsed.Parents.Select(p => p.Text)));
    }

    [Theory]
    [InlineData("````", "```", "````")]
    [InlineData("~~~", "```", "~~~~")]
    public void FenceContentsCannotManufactureSections(string open, string inner, string close)
    {
        var source = $"# Real\n{open}\n## Fake\n{inner}\nFake\n----\n{close}\n## Next\nbody";
        var parents = TextStructure.Build(source, "guide.md", 2).Parents;
        Assert.Equal(2, parents.Count);
        Assert.Contains("## Fake", parents[0].Text);
        Assert.Equal("Real / Next", parents[1].Title);
    }

    [Theory]
    [InlineData("guide.txt", "# literal\n\n## not a chapter")]
    [InlineData("Code.cs", "# region\n\npublic class Example {}")]
    [InlineData("guide.md", "No headings\n\nsecond paragraph")]
    public void UnstructuredTextIsOneOriginalParent(string path, string text)
    {
        Assert.Equal(text, Assert.Single(TextStructure.Build(text, path, 2).Parents).Text);
    }

    [Fact]
    public async Task ParagraphChildrenSurviveFormattingAndSmallEmbeddingBudgetsWithoutLlmCalls()
    {
        var text = "# Title\n\n## A\n\n" + string.Concat(Enumerable.Repeat("長い本文😀。", 80)) + "\n\n## B\n\n末尾";
        var structure = TextStructure.Build(text, "guide.md", 2);
        // Formatting may remove a heading; parent boundaries must still come from the original.
        var paragraphs = structure.Paragraphs.Where(p => !p.Text.StartsWith('#')).ToList();
        var settings = new TextRagSettings();
        var ai = new NoChatAi();
        var children = await TextStructure.ChildrenAsync(new(ai, settings), settings, new(), structure.Parents, paragraphs, default);
        Assert.All(children, c =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(c.Text) <= 80);
            var parent = Assert.Single(structure.Parents, p => p.Id == c.ParentId);
            Assert.InRange(c.Start, parent.Start, parent.End);
            Assert.Contains(c.Text, parent.Text);
        });
        foreach (var paragraph in paragraphs)
            Assert.Equal(paragraph.Text, string.Concat(children.Where(c => c.Start == paragraph.Id).Select(c => c.Text)));
        Assert.Contains("## A", structure.Parents[1].Text);
    }

    [Fact]
    public async Task SemanticChildrenRemainInsideTheirParent()
    {
        var structure = TextStructure.Build("## A\n\none\n\ntwo\n\n## B\n\nthree", "guide.md", 2);
        var settings = new TextRagSettings { ParagraphChildren = false };
        var ai = new FakeTextAi();
        var children = await TextStructure.ChildrenAsync(new(ai, settings), settings, new(), structure.Parents, structure.Paragraphs, default);
        Assert.True(ai.TransformCalls >= 2);
        Assert.All(children, c => Assert.Single(structure.Parents, p => p.Id == c.ParentId && p.Start <= c.Start && p.End >= c.End));
    }

    [Fact]
    public void ParentConfigurationChangesRequireReindexingAndRoundTrip()
    {
        var settings = new TextRagSettings();
        Assert.True(settings.ParentChunksEnabled);
        Assert.True(settings.ParagraphChildren);
        Assert.NotEmpty((settings with { ParentHeadingLevel = 0 }).Validate());
        Assert.NotEmpty((settings with { ParentHeadingLevel = 7 }).Validate());
        Assert.NotEqual(settings.ConfigurationId(new()), (settings with { ParentChunksEnabled = false }).ConfigurationId(new()));
        Assert.NotEqual(settings.ConfigurationId(new()), (settings with { ParentHeadingLevel = 3 }).ConfigurationId(new()));
        Assert.NotEqual(settings.ConfigurationId(new()), (settings with { ParagraphChildren = false }).ConfigurationId(new()));
        var custom = settings with { ParentHeadingLevel = 3, ParagraphChildren = false };
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<TextRagSettings>(System.Text.Json.JsonSerializer.Serialize(custom))!;
        Assert.Equal(custom.ConfigurationId(new()), roundTrip.ConfigurationId(new()));
    }

    [ServerFact]
    public async Task StructuralRankingSelectsOriginalParentOrChildAndMcpFetchReturnsEntireDocument()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs"; Directory.CreateDirectory(input);
        var file = Path.Combine(input, "guide.md");
        var first = "## A\n\n" + string.Join("\n\n", Enumerable.Range(1, 40).Select(i => "needle paragraph " + i)) + "\n\n";
        var second = "## B\n\norchid second section\n\nother context";
        var original = first + second;
        await File.WriteAllTextAsync(file, original);
        var settings = Settings(input);
        settings = settings with { TextPipeline = settings.TextPipeline with { ChildMode = ChildChunkMode.Paragraphs } };
        var ai = new RankedAi();
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, fixture.Root);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(2, document.Parents!.Count);
        Assert.True(document.Chunks!.Count > 40);
        Assert.All(ai.EmbeddedInputs, text => Assert.DoesNotContain("needle paragraph 1\n\nneedle paragraph 2", text));
        var hits = await service.SearchAsync("needle", 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal(first, hits[0].Content);
        // Section B has one relevant paragraph and unrelated surrounding text: return only the child.
        Assert.Equal("orchid second section", hits[1].Content);
        Assert.Equal("child", hits[1].ResultUnitKind);
        Assert.True(hits[0].Ranking!.Combined >= hits[1].Ranking!.Combined);
        Assert.NotEqual(hits[0].ParentChunkId, hits[1].ParentChunkId);
        Assert.Equal(document.DocumentId, hits[0].DocumentId);

        var port = McpTests.FreePort(); var secret = new string('d', 64);
        await using var host = await LocalMcpHost.StartAsync(service, port, secret);
        await using var client = await McpTests.ConnectAsync(port, secret);
        var search = McpTests.Body(await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "needle", ["limit"] = 2 }));
        var result = search.GetProperty("results")[0];
        Assert.Equal(first, result.GetProperty("text").GetString());
        Assert.Equal("A", result.GetProperty("section_title").GetString());
        var fetchArgs = new Dictionary<string, object?> { ["id"] = result.GetProperty("id").GetString() };
        var fetched = McpTests.Body(await client.CallToolAsync("fetch", fetchArgs));
        Assert.Equal(original, fetched.GetProperty("text").GetString());
        Assert.False(fetched.TryGetProperty("parent_chunk_id", out _));

        // A changed parent policy invalidates old vectors until reindexing completes.
        using (var changed = new TextRagService(repo, ai, settings with { TextPipeline = settings.TextPipeline with { ParentHeadingLevel = 1 } }, fixture.Root))
        {
            Assert.Empty(await changed.SearchAsync("needle"));
            Assert.Null(await changed.FetchAsync(document.DocumentId));
        }
        await File.WriteAllTextAsync(file, "## Updated\n\nneedle new content");
        Assert.Empty(await service.SearchAsync("needle"));
        Assert.True((await client.CallToolAsync("fetch", fetchArgs)).IsError);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.DoesNotContain("paragraph 1", (await service.SearchAsync("needle"))[0].Content!);
        File.Delete(file);
        Assert.Empty(await service.SearchAsync("needle"));
        Assert.True((await client.CallToolAsync("fetch", fetchArgs)).IsError);
    }

    [ServerFact]
    public async Task SecretsOutsideMatchedChildBlockParentsAndFullFetchAndOptOutIsExplicit()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs"; Directory.CreateDirectory(input);
        var source = "## Public\n\nneedle safe paragraph\n\n## Restricted\n\n社外秘 private detail";
        await File.WriteAllTextAsync(Path.Combine(input, "secret.md"), source);
        var settings = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new RankedAi(), settings, fixture.Root);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        using (var calls = new LocalRetrievalCalls(service))
        {
            var hits = McpTests.Body(await calls.CallAsync("search", "needle", 5, null, default)).GetProperty("results");
            var hit = Assert.Single(hits.EnumerateArray());
            Assert.Equal("withheld", hit.GetProperty("decision").GetString());
            Assert.Equal(["decision", "id", "source"], hit.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(document.Path, hit.GetProperty("source").GetString());
            var fetch = McpTests.Body(await calls.CallAsync("fetch", null, 0, document.DocumentId.ToString("D"), default));
            Assert.Equal(["decision", "id", "source"], fetch.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(document.Path, fetch.GetProperty("source").GetString());
        }
        var llm = new InspectAi();
        using (var checkedService = new TextRagService(repo, llm, settings with { Confidentiality = new() { PatternsEnabled = false, LlmEnabled = true } }, fixture.Root))
        {
            Assert.Equal(DisclosureDecision.Withheld, Assert.Single(await checkedService.SearchAsync("needle")).Decision);
            Assert.Equal(DisclosureDecision.Withheld, (await checkedService.FetchAsync(document.DocumentId))!.Decision);
            Assert.Contains(llm.Inspected, s => s.Contains("private detail", StringComparison.Ordinal));
            llm.Fail = true;
            Assert.Equal(DisclosureDecision.Pending, (await checkedService.FetchAsync(document.DocumentId))!.Decision);
        }
        using var uncheckedService = new TextRagService(repo, new RankedAi(), settings with { Confidentiality = new() { Enabled = false } }, fixture.Root);
        Assert.All(await uncheckedService.SearchAsync("needle"), h => Assert.Equal(DisclosureDecision.Unchecked, h.Decision));
        Assert.Equal(source, (await uncheckedService.FetchAsync(document.DocumentId))!.Content);
    }

    [ServerFact]
    public async Task DerivedHeadingPathIsInspectedBeforeDisclosingMetadata()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs"; Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "guide.md"), "# Alpha\n\n## Beta\n\nneedle");
        var settings = Settings(input) with { Confidentiality = new() { Patterns = ["Alpha / Beta"] } };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new RankedAi(), settings, fixture.Root);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var hit = Assert.Single(await service.SearchAsync("needle", 1));
        Assert.Equal(DisclosureDecision.Withheld, hit.Decision);
        Assert.Null(hit.Content);
        Assert.Null(hit.SectionTitle);
        Assert.Null(hit.ParentChunkId);
    }

    [ServerFact]
    public async Task InterruptedEmbeddingResumesParentMappingAndLegacyModeStillReturnsChildren()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs"; Directory.CreateDirectory(input);
        const string source = "## A\n\nneedle one\n\n## B\n\norchid two";
        await File.WriteAllTextAsync(Path.Combine(input, "guide.md"), source);
        var settings = Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var interrupted = new TextRagService(repo, new FakeTextAi { FailEmbeddingCall = 2 }, settings, fixture.Root))
        {
            Assert.Equal(1, (await interrupted.ScanAsync()).Failed);
            Assert.Empty(await interrupted.SearchAsync("needle"));
        }
        var checkpoint = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(2, checkpoint.Parents!.Count);
        Assert.Equal(1, checkpoint.EmbeddedChunks);
        var resumedAi = new FakeTextAi();
        using (var resumed = new TextRagService(repo, resumedAi, settings, fixture.Root))
        {
            Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
            Assert.Equal(checkpoint.Chunks!.Count - 1, resumedAi.EmbeddingCalls);
            Assert.Equal(0, resumedAi.TransformCalls);
            Assert.Equal(2, (await resumed.SearchAsync("needle")).Count);
            var ready = Assert.Single(await repo.DocumentsAsync(default));
            Assert.Equal(checkpoint.Parents, ready.Parents);
            Assert.All(ready.Chunks!, c => Assert.NotNull(c.ParentId));
            // Never silently return a child if the parent mapping is missing or corrupt.
            await repo.SaveAsync(ready with { Parents = null }, default);
            Assert.Empty(await resumed.SearchAsync("needle"));
            await Assert.ThrowsAsync<InvalidDataException>(() => repo.PublishAsync(ready with { Parents = null }, default));
        }
        var legacy = settings with { TextPipeline = settings.TextPipeline with { ParentChunksEnabled = false, ChunkEnabled = true } };
        using var oldMode = new TextRagService(repo, new FakeTextAi(), legacy, fixture.Root);
        Assert.Equal(1, (await oldMode.ScanAsync()).Indexed);
        Assert.All(await oldMode.SearchAsync("needle"), h => Assert.Null(h.ParentChunkId));
        Assert.Equal(source, (await oldMode.FetchAsync(checkpoint.DocumentId))!.Content);
    }

    private static AppSettings Settings(string input) => new()
    {
        UpdateExclusionMinutes = 0, // Synthetic fixtures are intentionally processed immediately.
        Ai = new() { ChatModel = "test", EmbeddingModel = "test" }, Folders = [new(input)],
        TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false }
    };
    private sealed class NoChatAi : ITextRagAi
    {
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException("No LLM call expected.");
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => Task.FromResult(80);
    }
    private class RankedAi : ITextRagAi
    {
        public List<string> EmbeddedInputs { get; } = [];
        public virtual Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException("No LLM call expected.");
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            EmbeddedInputs.AddRange(inputs);
            return Task.FromResult(new VectorBatch(3, inputs.Select(s => s.Contains("needle", StringComparison.Ordinal)
                ? new float[] { 1, 0.01f, 0 } : s.Contains("orchid", StringComparison.Ordinal) ? [0.9f, 0.2f, 0] : [0, 1, 0.1f]).ToArray()));
        }
    }
    private sealed class InspectAi : RankedAi
    {
        public bool Fail { get; set; }
        public List<string> Inspected { get; } = [];
        public override Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            if (Fail) throw new HttpRequestException("local inspection unavailable");
            Inspected.Add(input);
            return Task.FromResult(input.Contains("private detail", StringComparison.Ordinal) ? "{\"decision\":\"withhold\"}" : "{\"decision\":\"allow\"}");
        }
    }
}
