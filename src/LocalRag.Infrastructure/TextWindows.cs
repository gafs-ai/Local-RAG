using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Lossless subdivision. Never cuts a surrogate pair, drops a character, or rewrites text.</summary>
public static class TextWindows
{
    public static IEnumerable<string> Split(string text, Func<string, bool> fits)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var ends = new List<int> { 0 };
        foreach (var rune in text.EnumerateRunes()) ends.Add(ends[^1] + rune.Utf16SequenceLength);
        var first = 0;
        while (first < ends.Count - 1)
        {
            var low = first + 1;
            var high = ends.Count - 1;
            var last = first;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (fits(text[ends[first]..ends[middle]])) { last = middle; low = middle + 1; }
                else high = middle - 1;
            }
            if (last == first) throw new PipelineValidationException("context_capacity", "指示や応答の予約を除くと本文を処理する余地がありません。");
            // Prefer a nearby line/sentence boundary without wasting most of a small context window.
            if (last < ends.Count - 1)
                for (var i = last; i > first + (last - first) * 3 / 4; i--)
                    if (text[ends[i] - 1] is '\n' or '。' or '！' or '？' or ' ' or '\t') { last = i; break; }
            yield return text[ends[first]..ends[last]];
            first = last;
        }
    }

    public static IEnumerable<string> Split(string text, int maxCharacters, int maxUtf8Bytes) =>
        Split(text, part => part.Length <= maxCharacters && Encoding.UTF8.GetByteCount(part) <= maxUtf8Bytes);

    public static string Tail(string text, int maxUtf8Bytes)
    {
        var bytes = 0;
        var start = text.Length;
        while (start > 0)
        {
            var next = start - 1;
            if (char.IsLowSurrogate(text[next]) && next > 0 && char.IsHighSurrogate(text[next - 1])) next--;
            var count = Encoding.UTF8.GetByteCount(text.AsSpan(next, start - next));
            if (bytes + count > maxUtf8Bytes) break;
            bytes += count;
            start = next;
        }
        return text[start..];
    }
}
