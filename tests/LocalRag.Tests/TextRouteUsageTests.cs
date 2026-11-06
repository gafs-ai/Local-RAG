using LocalRag.Core;

namespace LocalRag.Tests;

public class TextRouteUsageTests
{
    [Theory]
    [InlineData(ParentChunkMode.None, false, false, false)]
    [InlineData(ParentChunkMode.Structure, true, false, true)]
    [InlineData(ParentChunkMode.Llm, false, true, true)]
    public void ExplicitParentModesShowOnlyTheirInputs(ParentChunkMode mode, bool structure, bool llm, bool embedding)
    {
        var usage = TextRouteUsage.For(new() { SourceMode = SourceCodeMode.Text, Extensions = [".md"], ParentMode = mode,
            ParentEmbedding = ParentEmbeddingMode.TextOrSummary }, new());
        Assert.True(usage.Markdown);
        Assert.Equal(structure, usage.StructureParents);
        Assert.Equal(llm, usage.LlmParents);
        Assert.Equal(embedding, usage.ParentEmbedding);
        Assert.Equal(embedding, usage.ParentDescription);
        Assert.False(usage.CommonParents);
    }

    [Fact]
    public void CodeRoutesNeverUseTextProcessingEvenWithRetainedTextValues()
    {
        foreach (var language in SourceLanguages.All)
        {
            var usage = TextRouteUsage.For(new() { SourceMode = language.Mode, Extensions = [".md", ".txt"],
                ChildMode = ChildChunkMode.Llm, ParentMode = ParentChunkMode.Structure, ParentEmbedding = ParentEmbeddingMode.TextOrSummary }, new());
            Assert.Equal(new(false, false, false, false, false, false, false, false, false, false), usage);
        }
    }

    [Theory]
    [InlineData(".md", true, false, true, false)]
    [InlineData(".markdown", false, false, true, false)]
    [InlineData(".txt", true, false, false, true)]
    [InlineData(".txt", false, true, false, true)]
    public void InheritedChildrenMatchRuntimeMarkdownException(string extension, bool paragraphs, bool llm, bool mechanical, bool common)
    {
        var usage = TextRouteUsage.For(new() { SourceMode = SourceCodeMode.Text, Extensions = [extension] }, new() { ParagraphChildren = paragraphs });
        Assert.Equal(llm, usage.LlmChildren);
        Assert.Equal(mechanical, usage.MechanicalChildren);
        Assert.Equal(common, usage.CommonChildren);
    }

    [Fact]
    public void MixedExtensionsAndSharedParentSwitchAreReflectedImmediately()
    {
        var route = new TextRoute { SourceMode = SourceCodeMode.Text, Extensions = [".MD", ".txt"], ParentEmbedding = ParentEmbeddingMode.TextWhenFits };
        var usage = TextRouteUsage.For(route, new() { ParagraphChildren = false });
        Assert.True(usage.LlmChildren && usage.MechanicalChildren && usage.StructureParents && usage.CommonParents);
        Assert.False(usage.ParentDescription);
        Assert.False(TextRouteUsage.For(route, new() { ParentChunksEnabled = false }).ParentEmbedding);
        Assert.True(TextRouteUsage.For(route, new() { MechanicalMarkdown = false, ParagraphChildren = false }).LlmChildren);
    }
}
