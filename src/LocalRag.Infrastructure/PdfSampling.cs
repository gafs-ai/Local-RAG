using LocalRag.Core;

namespace LocalRag.Infrastructure;

internal static class PdfSampling
{
    // Quartiles first, then bisect the gaps. End pages are last, not representative samples.
    internal static IEnumerable<int> Order(int count)
    {
        if (count <= 6) { foreach (var p in Enumerable.Range(1, count)) yield return p; yield break; }
        var seen = new HashSet<int>();
        for (var divisions = 4; divisions <= 128; divisions *= 2)
            for (var numerator = 1; numerator < divisions; numerator++)
            {
                if (divisions > 4 && numerator % 2 == 0) continue;
                var page = Math.Clamp((int)Math.Ceiling(count * numerator / (double)divisions), 2, count - 1);
                if (seen.Add(page)) yield return page;
            }
        foreach (var p in Enumerable.Range(1, count)) if (seen.Add(p)) yield return p;
    }

    internal static PdfAssessment Assess(int pageCount, PdfSettings settings, Func<int, PdfPageAssessment> inspect,
        CancellationToken token)
    {
        if (settings.Policy != PdfInputPolicy.Automatic)
            return new(pageCount, [], settings.Policy == PdfInputPolicy.Skip ? "skip" : "convert",
                settings.Policy == PdfInputPolicy.Skip ? "pdf_policy_skip" : "pdf_forced_text", []);
        var samples = new List<PdfPageAssessment>();
        var pending = new Queue<int>(Order(pageCount));
        var adjacent = new Queue<int>();
        var seen = new HashSet<int>();
        var limit = pageCount <= 6 ? pageCount : Math.Min(pageCount, settings.MaxSamplePages);
        while (samples.Count < limit && (pending.Count > 0 || adjacent.Count > 0))
        {
            var page = samples.Count >= 3 && adjacent.Count > 0 ? adjacent.Dequeue() : pending.Dequeue();
            token.ThrowIfCancellationRequested();
            if (!seen.Add(page)) continue;
            var sample = inspect(page); samples.Add(sample);
            // Adjacent pages help when a sampled page is a contents/index/divider page.
            if (sample.Kind == "auxiliary")
            {
                if (page + 1 < pageCount) adjacent.Enqueue(page + 1);
                if (page - 1 > 1) adjacent.Enqueue(page - 1);
            }
            if (pageCount <= 6 || samples.Count is not (3 or 7 or 15 or 31 or 63)) continue;
            var decided = Decision(samples, final: false);
            if (decided is not null) return new(pageCount, samples, decided == "pdf_text" ? "convert" : "skip", decided, []);
        }
        var reason = Decision(samples, final: true) ?? "pdf_uncertain";
        return new(pageCount, samples, reason == "pdf_text" ? "convert" : "skip", reason, []);
    }

    private static string? Decision(List<PdfPageAssessment> samples, bool final)
    {
        var meaningful = samples.Where(s => s.Kind != "auxiliary").ToArray();
        var text = meaningful.Count(s => s.Kind == "text");
        var visual = meaningful.Count(s => s.Kind == "visual");
        if (meaningful.Length == 0) return final ? "pdf_no_text" : null;
        // Three unanimous samples permit an early decision. Mixed evidence needs more samples.
        if ((!final && meaningful.Length >= 3 && text == meaningful.Length) ||
            (final || samples.Count >= 7) && text > 0 && text >= meaningful.Length * 0.6) return "pdf_text";
        if (text == 0 && visual >= meaningful.Length * 0.8 && (final || meaningful.Length >= 3)) return "pdf_visual";
        if (final && samples.Sum(s => s.Characters) < 80) return "pdf_no_text";
        return final ? "pdf_uncertain" : null;
    }
}
