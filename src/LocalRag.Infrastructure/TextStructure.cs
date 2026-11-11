using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record StructuredText(List<TextParentChunk> Parents, List<TextParagraph> Paragraphs);

/// <summary>Deterministic Markdown section boundaries; parent text always comes from the original source.</summary>
public static class TextStructure
{
    public static StructuredText Build(string text, string path, TextRagSettings settings) =>
        Build(text, DocumentInputs.IsConverted(settings.SourceMode) ? "converted.md" : path, settings.ParentHeadingLevel,
            settings.EffectiveParentMode == ParentChunkMode.Structure ? settings.MarkdownParentMinCharacters : 0,
            settings.EffectiveParentMode == ParentChunkMode.Structure ? settings.MarkdownParentMaxCharacters : 262144);

    public static StructuredText Build(string text, string path, int headingLevel, int minimum = 0, int maximum = 262144)
    {
        if (headingLevel is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(headingLevel));
        if (!TextRagSettings.ValidMarkdownParentBounds(minimum, maximum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        var boundaries = new List<(int Offset, string? Title)> { (0, null) };
        var markdown = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        if (markdown)
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
        var sections = new List<(string Text, string? Title)>();
        for (var i = 0; i < boundaries.Count; i++)
        {
            var end = i + 1 < boundaries.Count ? boundaries[i + 1].Offset : text.Length;
            var source = text[boundaries[i].Offset..end];
            sections.Add((source, boundaries[i].Title));
        }
        if (markdown) sections = BoundSections(sections, minimum, maximum);
        var parents = new List<TextParentChunk>();
        var paragraphs = new List<TextParagraph>();
        foreach (var (source, title) in sections)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var start = paragraphs.Count + 1;
            foreach (var paragraph in TextFiles.Paragraphs(source, int.MaxValue))
                paragraphs.Add(new(paragraphs.Count + 1, paragraph.Text));
            parents.Add(new("p" + (parents.Count + 1), start, paragraphs.Count, source, title));
        }
        if (parents.Count == 0) throw new InvalidDataException("親チャンクを作成できる本文がありません。");
        return new(parents, paragraphs);
    }

    private static List<(string Text, string? Title)> BoundSections(List<(string Text, string? Title)> sections, int minimum, int maximum)
    {
        var pieces = sections.SelectMany(section => SplitSection(section.Text, maximum).Select(part => (Text: part, section.Title)));
        var result = new List<(string Text, string? Title)>();
        string pending = "";
        string? firstTitle = null, lastTitle = null;
        void Flush()
        {
            if (pending.Length == 0) return;
            result.Add((pending, firstTitle == lastTitle || lastTitle is null ? firstTitle :
                firstTitle is null ? lastTitle : firstTitle + " ～ " + lastTitle));
            pending = ""; firstTitle = lastTitle = null;
        }
        foreach (var piece in pieces)
        {
            // A rejected merge leaves the current group as-is, even if below the minimum.
            if (pending.Length > 0 && (pending.Length >= minimum || pending.Length + piece.Text.Length > maximum)) Flush();
            if (pending.Length == 0) firstTitle = piece.Title;
            pending += piece.Text;
            lastTitle = piece.Title;
        }
        Flush(); // A short final group is allowed; never exceed the maximum to meet the minimum.
        return result;
    }

    private static IEnumerable<string> SplitSection(string source, int maximum)
    {
        // Preserve original separators. Prefer paragraph boundaries, then lines, then scalar-safe cuts.
        var paragraphs = Regex.Matches(source, @"\r?\n[ \t]*\r?\n").Select(m => m.Index + m.Length).ToArray();
        var lines = Regex.Matches(source, @"\n").Select(m => m.Index + 1).ToArray();
        var start = 0;
        while (start < source.Length)
        {
            var end = Math.Min(source.Length, start + maximum);
            if (end < source.Length)
            {
                var paragraphEnd = BoundaryAtOrBefore(paragraphs, end);
                var lineEnd = BoundaryAtOrBefore(lines, end);
                if (paragraphEnd > start) end = paragraphEnd;
                else if (lineEnd > start) end = lineEnd;
                else if (char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end]) || source[end - 1] == '\r' && source[end] == '\n') end--;
            }
            yield return source[start..end];
            start = end;
        }
    }

    private static int BoundaryAtOrBefore(int[] boundaries, int offset)
    {
        var index = Array.BinarySearch(boundaries, offset);
        if (index < 0) index = ~index - 1;
        return index < 0 ? 0 : boundaries[index];
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
