using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record StructuredText(List<TextParentChunk> Parents, List<TextParagraph> Paragraphs);

/// <summary>Deterministic Markdown section boundaries; parent text always comes from the original source.</summary>
public static class TextStructure
{
    public static StructuredText Build(string text, string path, int headingLevel)
    {
        if (headingLevel is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(headingLevel));
        var boundaries = new List<(int Offset, string? Title)> { (0, null) };
        if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            var headings = new List<(int Level, string Title)>();
            char fence = '\0';
            var fenceLength = 0;
            (int Offset, string Text)? previous = null;
            foreach (Match line in Regex.Matches(text, @"[^\n]*(?:\n|$)"))
            {
                var value = line.Value.TrimEnd('\r', '\n');
                var marker = Regex.Match(value, @"^ {0,3}(`{3,}|~{3,})(.*)$");
                if (fence != '\0')
                {
                    if (marker.Success && marker.Groups[1].Value[0] == fence &&
                        marker.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(marker.Groups[2].Value)) fence = '\0';
                    previous = null;
                    continue;
                }
                if (marker.Success)
                {
                    fence = marker.Groups[1].Value[0]; fenceLength = marker.Groups[1].Length;
                    previous = null; continue;
                }
                var atx = Regex.Match(value, @"^ {0,3}(#{1,6})(?:[ \t]+(.*)|$)");
                var setext = Regex.Match(value, @"^ {0,3}(=+|-+)[ \t]*$");
                int level, offset;
                string title;
                if (atx.Success)
                {
                    level = atx.Groups[1].Length; offset = line.Index;
                    title = Regex.Replace(atx.Groups[2].Value, @"[ \t]+#+[ \t]*$", "").Trim();
                }
                else if (setext.Success && previous is { } preceding)
                {
                    level = setext.Groups[1].Value[0] == '=' ? 1 : 2;
                    offset = preceding.Offset; title = preceding.Text.Trim();
                }
                else
                {
                    previous = !string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value, @"^( {4}|\t| {0,3}(>|[-+*] |\d+[.)] ))")
                        ? (line.Index, value) : null;
                    continue;
                }
                previous = null;
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, title));
                if (level > headingLevel) continue;
                var headingPath = string.Join(" / ", headings.Select(h => h.Title).Where(t => t.Length > 0));
                if (boundaries[^1].Offset == offset) boundaries[^1] = (offset, headingPath);
                else boundaries.Add((offset, headingPath));
            }
        }
        var parents = new List<TextParentChunk>();
        var paragraphs = new List<TextParagraph>();
        for (var i = 0; i < boundaries.Count; i++)
        {
            var end = i + 1 < boundaries.Count ? boundaries[i + 1].Offset : text.Length;
            var source = text[boundaries[i].Offset..end];
            if (string.IsNullOrWhiteSpace(source)) continue;
            var start = paragraphs.Count + 1;
            foreach (var paragraph in TextFiles.Paragraphs(source, int.MaxValue))
                paragraphs.Add(new(paragraphs.Count + 1, paragraph.Text));
            parents.Add(new("p" + (parents.Count + 1), start, paragraphs.Count, source, boundaries[i].Title));
        }
        if (parents.Count == 0) throw new InvalidDataException("親チャンクを作成できる本文がありません。");
        return new(parents, paragraphs);
    }

    public static async Task<List<TextChunk>> ChildrenAsync(TextAiPipeline pipeline, TextRagSettings settings,
        TextCategory category, IReadOnlyList<TextParentChunk> parents, List<TextParagraph> paragraphs, CancellationToken token)
    {
        var chunks = new List<TextChunk>();
        foreach (var parent in parents)
        {
            var members = paragraphs.Where(p => p.Id >= parent.Start && p.Id <= parent.End).ToList();
            if (members.Count == 0) continue;
            if (settings.ParagraphChildren)
            {
                foreach (var paragraph in members)
                    chunks.AddRange((await pipeline.ChunkAsync([paragraph], category, token).ConfigureAwait(false))
                        .Select(c => c with { ParentId = parent.Id }));
            }
            else chunks.AddRange((await pipeline.ChunkAsync(members, category, token).ConfigureAwait(false))
                .Select(c => c with { ParentId = parent.Id }));
        }
        return chunks;
    }
}
