using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record ChunkHierarchy(List<TextChunk> Children, List<TextParentChunk>? Parents);

/// <summary>Stage 1 joins retrieval children; stage 2 groups them for disclosure.</summary>
public static class TextChunking
{
    public static async Task<ChunkHierarchy> BuildAsync(ITextRagAi ai, TextAiPipeline pipeline, TextRagSettings settings,
        TextCategory category, string path, StructuredText source, List<TextParagraph> formatted, CancellationToken token)
    {
        var childMode = settings.EffectiveChildMode;
        if (settings.ChildMode == ChildChunkMode.Inherit && settings.MechanicalMarkdown &&
            Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown") childMode = ChildChunkMode.Mechanical;
        var children = new List<TextChunk>();
        var parents = new List<TextParentChunk>();
        var embeddingBudget = await ai.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        foreach (var section in source.Parents)
        {
            var paragraphs = formatted.Where(p => p.Id >= section.Start && p.Id <= section.End).ToList();
            if (paragraphs.Count == 0) continue;
            List<TextChunk> members;
            if (childMode == ChildChunkMode.Llm && (category.ChunkEnabled ?? settings.ChunkEnabled))
                members = await pipeline.ChunkAsync(paragraphs, category, token).ConfigureAwait(false);
            else
                members = MechanicalChildren(paragraphs, settings.MaxChunkCharacters, embeddingBudget,
                    childMode == ChildChunkMode.Mechanical && (category.ChunkEnabled ?? settings.ChunkEnabled), token);

            if (settings.EffectiveParentMode == ParentChunkMode.None) children.AddRange(members);
            else if (settings.EffectiveParentMode == ParentChunkMode.Structure)
            {
                parents.Add(section);
                children.AddRange(members.Select(c => c with { ParentId = section.Id }));
            }
            else
            {
                var groups = await GroupParentsAsync(pipeline, settings, category, members, token).ConfigureAwait(false);
                // Locate original paragraph spans, including removed paragraphs between retained children.
                var offsets = new Dictionary<int, (int Start, int End)>();
                var cursor = 0;
                foreach (var paragraph in source.Paragraphs.Where(p => p.Id >= section.Start && p.Id <= section.End))
                {
                    var start = section.Text.IndexOf(paragraph.Text, cursor, StringComparison.Ordinal);
                    if (start < 0) throw new InvalidDataException("原文と段落の対応が不正です。");
                    cursor = start + paragraph.Text.Length;
                    offsets.Add(paragraph.Id, (start, cursor));
                }
                foreach (var group in groups)
                {
                    var first = members[group.Start - 1];
                    var last = members[group.End - 1];
                    var id = "g" + (parents.Count + 1);
                    var raw = section.Text[offsets[first.Start].Start..offsets[last.End].End];
                    parents.Add(new(id, first.Start, last.End, raw, section.Title));
                    children.AddRange(members.Skip(group.Start - 1).Take(group.End - group.Start + 1).Select(c => c with { ParentId = id }));
                }
            }
        }
        return new(children, settings.EffectiveParentMode == ParentChunkMode.None ? null : parents);
    }

    public static List<TextChunk> MechanicalChildren(List<TextParagraph> paragraphs, int limit, int budget, bool join, CancellationToken token)
    {
        var result = new List<TextChunk>();
        foreach (var paragraph in paragraphs)
            foreach (var part in TextWindows.Split(paragraph.Text, limit, budget))
            {
                token.ThrowIfCancellationRequested();
                if (join && result.Count > 0 && result[^1].End != paragraph.Id && result[^1].Text.Length + 2 + part.Length <= limit &&
                    Encoding.UTF8.GetByteCount(result[^1].Text) + 2 + Encoding.UTF8.GetByteCount(part) <= budget)
                    result[^1] = result[^1] with { End = paragraph.Id, Text = result[^1].Text + "\n\n" + part };
                else result.Add(new(paragraph.Id, paragraph.Id, part));
            }
        return result;
    }

    private static async Task<List<TextChunk>> GroupParentsAsync(TextAiPipeline pipeline, TextRagSettings settings,
        TextCategory category, List<TextChunk> children, CancellationToken token)
    {
        var prompt = category.ParentPrompt ?? settings.ParentPrompt;
        var budget = await pipeline.InputBudgetAsync(prompt, token).ConfigureAwait(false);
        var result = new List<TextChunk>();
        var batch = new List<TextParagraph>();
        object Input(IEnumerable<TextParagraph> values)
        {
            var items = values.ToArray();
            return new { paragraphs = items, responseExample = new { chunks = items.Select(p => new { start = p.Id, end = p.Id }) } };
        }
        async Task Flush()
        {
            if (batch.Count == 0) return;
            if (batch.Count == 1) result.Add(new(batch[0].Id, batch[0].Id, ""));
            else result.AddRange(await pipeline.JsonAsync(prompt, Input(batch),
                root => TextAiPipeline.ValidateChunks(root, batch, int.MaxValue), token).ConfigureAwait(false));
            batch.Clear();
        }
        for (var i = 0; i < children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var child = new TextParagraph(i + 1, children[i].Text);
            if (batch.Count == 4 || TextAiPipeline.InputBytes(Input(batch.Append(child))) > budget) await Flush().ConfigureAwait(false);
            if (TextAiPipeline.InputBytes(Input(new[] { child })) > budget)
                result.Add(new(i + 1, i + 1, "")); // Do not ask the LLM about unseen or truncated content.
            else batch.Add(child);
        }
        await Flush().ConfigureAwait(false);
        return result;
    }
}
