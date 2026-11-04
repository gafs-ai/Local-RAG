using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class HierarchicalPipelineTests
{
    [Fact]
    public async Task MarkdownJoinsWithinSectionsMechanicallyWithoutAskingLlm()
    {
        const string text = "# Title\n\nfirst\n\nsecond\n\n## Next\n\nthird";
        var ai = new GroupAi();
        var settings = new TextRagSettings();
        var source = TextStructure.Build(text, "guide.md", 2);
        var result = await TextChunking.BuildAsync(ai, new(ai, settings), settings, new(), "guide.md", source, source.Paragraphs, default);
        Assert.Empty(ai.Prompts);
        Assert.Equal(2, result.Children.Count);
        Assert.Equal(2, result.Parents!.Count);
        Assert.Equal(text, string.Concat(result.Parents.Select(p => p.Text)));
        Assert.DoesNotContain("third", result.Children[0].Text);
        Assert.Equal(result.Parents[0].Id, result.Children[0].ParentId);
    }

    [Fact]
    public async Task TwoLlmStagesUseDistinctPromptsAndParentKeepsOriginalRemovedParagraph()
    {
        const string text = "first\n\nremoved\n\nlast";
        var ai = new GroupAi();
        var options = new TextRagSettings { ChildMode = ChildChunkMode.Llm, ParentMode = ParentChunkMode.Llm,
            ChunkPrompt = "child", ParentPrompt = "parent" };
        var source = TextStructure.Build(text, "input.txt", 2);
        var result = await TextChunking.BuildAsync(ai, new(ai, options), options, new(), "input.txt", source,
            source.Paragraphs.Where(p => p.Id != 2).ToList(), default);
        Assert.Equal(2, result.Children.Count);
        Assert.Equal(text, Assert.Single(result.Parents!).Text);
        Assert.Equal(result.Children[0].ParentId, result.Children[1].ParentId);
        Assert.StartsWith("child", ai.Prompts[0]);
        Assert.Equal("parent", ai.Prompts[1]);
    }

    [Fact]
    public async Task OversizedChildrenDoNotCauseParentPromptTruncation()
    {
        var text = new string('語', 400) + "\n\n" + new string('別', 400);
        var ai = new GroupAi { Budget = 220 };
        var options = new TextRagSettings { ChildMode = ChildChunkMode.Paragraphs, ParentMode = ParentChunkMode.Llm };
        var source = TextStructure.Build(text, "input.txt", 2);
        var result = await TextChunking.BuildAsync(ai, new(ai, options), options, new(), "input.txt", source, source.Paragraphs, default);
        Assert.Empty(ai.Prompts);
        Assert.Equal(2, result.Parents!.Count);
        Assert.Equal(source.Paragraphs.Select(p => p.Text), result.Parents.Select(p => p.Text));
    }

    [Fact]
    public async Task InvalidParentRangesAreRetriedAndNotPublished()
    {
        var ai = new GroupAi { Invalid = true };
        var options = new TextRagSettings { ChildMode = ChildChunkMode.Paragraphs, ParentMode = ParentChunkMode.Llm };
        var source = TextStructure.Build("first\n\nsecond", "input.txt", 2);
        await Assert.ThrowsAsync<PipelineValidationException>(() => TextChunking.BuildAsync(ai, new(ai, options), options,
            new(), "input.txt", source, source.Paragraphs, default));
        Assert.Equal(2, ai.Prompts.Count);
    }

    [Fact]
    public void MechanicalSplittingPreservesLongParagraphAndHonorsEmbeddingBudget()
    {
        var text = string.Concat(Enumerable.Repeat("あ😀", 100));
        var chunks = TextChunking.MechanicalChildren([new(1, text)], 100, 91, true, default);
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(c.Text), 1, 91));
    }

    [Fact]
    public void ManagedModesAndCategoryParentPromptAreValidatedAndAffectConfiguration()
    {
        var management = ManagementSettings.FromLegacy(new(), new());
        var changed = management with { UserPrompts = [new("parent", "独自まとめ", PromptPurpose.ParentChunk, "parent")],
            DefaultTextRoute = management.DefaultTextRoute with { ChildMode = ChildChunkMode.Mechanical, ParentMode = ParentChunkMode.Llm,
                CategoryOverrides = [new("general", ParentPromptId: "parent")] } };
        Assert.Empty(changed.Validate());
        var effective = changed.Resolve(new(), "input.txt");
        Assert.Equal(ChildChunkMode.Mechanical, effective.ChildMode);
        Assert.Equal("parent", effective.Categories.Single(c => c.Id == "general").ParentPrompt);
        Assert.NotEqual(management.ConfigurationId(new(), new()), changed.ConfigurationId(new(), new()));
        Assert.NotEmpty((changed with { DefaultTextRoute = changed.DefaultTextRoute with { ParentPromptId = "system.chunk" } }).Validate());
    }

    private sealed class GroupAi : ITextRagAi
    {
        public List<string> Prompts { get; } = [];
        public int Budget { get; init; } = int.MaxValue;
        public bool Invalid { get; init; }
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => Task.FromResult(Budget);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            Prompts.Add(system);
            if (Invalid) return Task.FromResult("{\"chunks\":[{\"start\":99,\"end\":99}]}");
            using var json = JsonDocument.Parse(input);
            var ids = json.RootElement.GetProperty("paragraphs").EnumerateArray().Select(p => p.GetProperty("Id").GetInt32()).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { chunks = system.StartsWith("child", StringComparison.Ordinal)
                ? ids.Select(id => new { start = id, end = id }).ToArray() : [new { start = ids[0], end = ids[^1] }] }));
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }
}
