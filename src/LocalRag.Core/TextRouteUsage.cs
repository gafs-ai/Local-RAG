namespace LocalRag.Core;

// Mirrors the execution modes, including Markdown's special inherited child mode.
public sealed record TextRouteUsage(bool Text, bool Markdown, bool LlmChildren, bool MechanicalChildren,
    bool StructureParents, bool LlmParents, bool ParentEmbedding, bool ParentDescription,
    bool CommonParents, bool CommonChildren)
{
    public static TextRouteUsage For(TextRoute route, TextRagSettings limits)
    {
        var text = route.SourceMode is SourceCodeMode.Text or SourceCodeMode.Word;
        var extensions = route.Extensions ?? [route.Extension];
        var markdown = route.SourceMode == SourceCodeMode.Word || extensions.Any(IsMarkdown);
        var other = route.SourceMode != SourceCodeMode.Word && extensions.Any(e => !IsMarkdown(e));
        var inherited = route.ChildMode == ChildChunkMode.Inherit;
        var llm = route.ChildMode == ChildChunkMode.Llm || inherited && !limits.ParagraphChildren && (other || !limits.MechanicalMarkdown);
        var mechanical = route.ChildMode == ChildChunkMode.Mechanical || inherited && markdown && limits.MechanicalMarkdown;
        var parent = route.ParentMode == ParentChunkMode.Inherit
            ? limits.ParentChunksEnabled ? ParentChunkMode.Structure : ParentChunkMode.None : route.ParentMode;
        var embed = text && parent != ParentChunkMode.None;
        return new(text, text && markdown, text && llm, text && mechanical,
            text && parent == ParentChunkMode.Structure, text && parent == ParentChunkMode.Llm,
            embed, embed && route.ParentEmbedding == ParentEmbeddingMode.TextOrSummary,
            text && route.ParentMode == ParentChunkMode.Inherit,
            text && inherited && (other || !limits.MechanicalMarkdown));
    }

    private static bool IsMarkdown(string extension) => extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
}
