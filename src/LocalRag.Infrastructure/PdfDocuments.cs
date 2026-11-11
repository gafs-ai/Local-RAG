using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LocalRag.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace LocalRag.Infrastructure;

public static class PdfDocuments
{
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxPages = 2000;
    private sealed record Line(string Text, double Top, double Bottom, double Size);
    private sealed record Section(List<Line> Lines);
    private sealed record PageData(int Number, double Height, List<Section> Sections, PdfPageAssessment Assessment,
        double? HeaderBottom, double? FooterTop);

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length > MaxFileBytes) throw new PipelineValidationException("pdf_size", "PDFは64MiB以内にしてください。");
        var data = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(data, token).ConfigureAwait(false); return data;
    }

    public static DocumentSnapshot Convert(byte[] bytes, TextRagSettings settings, CancellationToken token = default)
    {
        var hash = System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (settings.Pdf.Policy == PdfInputPolicy.Skip)
            return new(hash, "", ["pdf_policy_skip"], new("", "", [], []), new(0, [], "skip", "pdf_policy_skip", []));
        try { return ConvertCore(bytes, hash, settings, token); }
        catch (Exception ex) when (ex is not (OperationCanceledException or PipelineValidationException or OutOfMemoryException))
        { throw new PipelineValidationException("pdf_invalid", "PDFの構造・暗号化・文字情報を確認してください。"); }
    }

    private static DocumentSnapshot ConvertCore(byte[] bytes, string hash, TextRagSettings settings, CancellationToken token)
    {
        if (bytes.Length > MaxFileBytes) throw new PipelineValidationException("pdf_size", "PDFの容量上限です。");
        using var document = PdfDocument.Open(bytes);
        if (document.NumberOfPages is < 1 or > MaxPages) throw new PipelineValidationException("pdf_size", "PDFのページ上限です。");
        var pages = new Dictionary<int, PageData>();
        var totalCharacters = 0;
        PageData Read(int number)
        {
            token.ThrowIfCancellationRequested();
            if (pages.TryGetValue(number, out var cached)) return cached;
            var page = document.GetPage(number);
            if (page.Letters.Count > 100000 || page.Paths.Count > 100000)
                throw new PipelineValidationException("pdf_complexity", "PDFのページ構造が複雑すぎます。");
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            var blocks = words.Length == 0 ? [] : DocstrumBoundingBoxes.Instance.GetBlocks(words).ToArray();
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder);
            var sections = ordered.Select(b => new Section(b.TextLines.Select(l => new Line(l.Text,
                l.BoundingBox.Top, l.BoundingBox.Bottom, l.Words.SelectMany(w => w.Letters).Select(c => Math.Abs(c.BoundingBox.Height)).DefaultIfEmpty(10).Average())).ToList())).ToList();
            var lines = sections.SelectMany(s => s.Lines).ToArray();
            totalCharacters += lines.Sum(l => l.Text.Length + 2);
            if (totalCharacters > settings.MaxDocumentCharacters) throw new PipelineValidationException("document_size", "PDFの抽出文字数上限です。");
            var images = Coverage(page.GetImages().Select(i => i.BoundingBox), page.Width, page.Height);
            var drawingBoxes = page.Paths.Where(p => !p.IsClipping).Select(p => p.GetBoundingRectangle()).Where(b => b.HasValue).Select(b => b!.Value)
                .Where(b => b.Width * b.Height < page.Width * page.Height * 0.9); // Ignore a full-page background rectangle.
            var drawings = Coverage(drawingBoxes, page.Width, page.Height);
            var characters = lines.Sum(l => l.Text.Count(char.IsLetterOrDigit));
            var prose = lines.Count(l => l.Text.Count(char.IsLetterOrDigit) >= 25);
            var bad = lines.Sum(l => l.Text.Count(c => c == '\ufffd' || char.IsControl(c) && !char.IsWhiteSpace(c)));
            var auxiliary = lines.Take(5).Any(l => Regex.IsMatch(l.Text.Trim(), @"^(?:table of contents|contents|index|目\s*次|索\s*引|목차|색인|目录|目錄)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            var kind = auxiliary || characters < 40 && images + drawings < 0.1 ? "auxiliary" :
                bad > Math.Max(3, characters / 20) ? "uncertain" :
                characters >= 160 && prose >= 3 && (characters >= 350 || images + drawings < 0.6) ? "text" :
                characters < 160 && images + drawings > 0.25 ? "visual" : "uncertain";
            // Some PDFs place running headers/footers far inside the paper margins. Only admit
            // an outermost row separated from the body by a clear gap; repetition is checked later.
            var sizes = lines.Select(l => l.Size).Order().ToArray();
            var typical = sizes.Length == 0 ? 10 : Math.Max(1, sizes[sizes.Length / 2]);
            var top = lines.Select(l => l.Top).DefaultIfEmpty(0).Max();
            var bottom = lines.Select(l => l.Bottom).DefaultIfEmpty(0).Min();
            var headerBottom = lines.Where(l => l.Top >= top - typical / 2).Select(l => l.Bottom).DefaultIfEmpty(0).Min();
            var footerTop = lines.Where(l => l.Bottom <= bottom + typical / 2).Select(l => l.Top).DefaultIfEmpty(0).Max();
            var belowHeader = lines.Where(l => l.Top < top - typical / 2).Select(l => (double?)l.Top).Max();
            var aboveFooter = lines.Where(l => l.Bottom > bottom + typical / 2).Select(l => (double?)l.Bottom).Min();
            var gap = Math.Max(18, typical * 2);
            return pages[number] = new(number, page.Height, sections, new(number, characters, prose, images, drawings, kind),
                headerBottom - belowHeader >= gap ? headerBottom : null, aboveFooter - footerTop >= gap ? footerTop : null);
        }
        var assessment = PdfSampling.Assess(document.NumberOfPages, settings.Pdf, p => Read(p).Assessment, token);
        if (assessment.Decision != "convert")
            return new(hash, "", [assessment.Reason], new("", "", [], []), assessment);
        // The full conversion still checks every page, but missing image text is a notice, not a rejection.
        for (var p = 1; p <= document.NumberOfPages; p++) Read(p);
        var all = pages.Values.OrderBy(p => p.Number).ToArray();
        bool Margin(PageData p, Line l) => l.Top >= p.Height * 0.92 || l.Bottom <= p.Height * 0.08 ||
            p.HeaderBottom is { } header && l.Bottom >= header - .01 || p.FooterTop is { } footer && l.Top <= footer + .01;
        string Key(Line line) => Regex.Replace(Regex.Replace(line.Text.Trim(), @"\d+", "#"), @"\s+", " ");
        var repeated = all.SelectMany(p => p.Sections.SelectMany(s => s.Lines).Where(l => Margin(p, l))
            .Select(l => Key(l)).Distinct().Select(k => (p.Number, Key: k))).GroupBy(x => x.Key)
            .Where(g => g.Count() >= Math.Max(3, Math.Ceiling(all.Length * 0.5))).Select(g => g.Key).ToHashSet();
        var disclosure = string.Join("\n", all.SelectMany(p => p.Sections.SelectMany(s => s.Lines)).Select(l => l.Text));
        var notices = new HashSet<string> { "pdf_converted", "pdf_visual_content_unparsed" };
        if (assessment.Reason == "pdf_forced_text") notices.Add(assessment.Reason);
        var unavailable = all.Where(p => p.Assessment.Characters == 0).Select(p => p.Number).ToList();
        if (unavailable.Count > 0) notices.Add("pdf_pages_without_text");
        if (all.Sum(p => p.Assessment.Characters) == 0)
            return new(hash, "", ["pdf_no_text"], new("", "", [], []), assessment with { Decision = "skip", Reason = "pdf_no_text", TextUnavailablePages = unavailable });
        var blocksOut = new List<WordBlock>();
        foreach (var p in all)
        {
            token.ThrowIfCancellationRequested();
            if (p.Assessment.Characters == 0) continue;
            blocksOut.Add(new(blocksOut.Count + 1, $"PDFページ {p.Number}", 1, Protected: true));
            var sizes = p.Sections.SelectMany(s => s.Lines).Select(l => l.Size).Order().ToArray();
            var normalSize = sizes.Length == 0 ? 10 : sizes[sizes.Length / 2];
            foreach (var section in p.Sections)
            {
                var kept = section.Lines.Where(l =>
                {
                    if (!settings.Pdf.RemoveHeadersFooters || !Margin(p, l) || WordDocuments.HasLabel(l.Text)) return true;
                    var remove = repeated.Contains(Key(l)) || Regex.IsMatch(l.Text.Trim(), @"^[\[(\-–—\s]*(?:(?:Page|ページ)\s*)?\d+(?:\s*/\s*\d+)?[\])\-–—\s]*$", RegexOptions.IgnoreCase);
                    if (remove) notices.Add("pdf_headers_removed");
                    return !remove;
                }).ToArray();
                if (kept.Length == 0) continue;
                var text = settings.Word.JoinSoftLineBreaks ? WordDocuments.JoinLines(kept.Select(l => l.Text)) : string.Join("\n", kept.Select(l => l.Text));
                var level = settings.Word.InferHeadings && kept.Length == 1 && text.Length <= 160 && kept[0].Size > normalSize * 1.3 ? 2 : 0;
                blocksOut.Add(new(blocksOut.Count + 1, text, level, Protected: WordDocuments.HasLabel(text)));
            }
        }
        var markdown = string.Join("\n\n", blocksOut.Select(WordDocuments.Render));
        if (markdown.Length > settings.MaxDocumentCharacters || disclosure.Length > settings.MaxDocumentCharacters)
            throw new PipelineValidationException("document_size", "PDFの変換文字数上限です。");
        var result = new WordDocument(markdown, disclosure, blocksOut, notices.ToList());
        return new(hash, markdown, result.Notices, result, assessment with { TextUnavailablePages = unavailable });
    }

    // Grid union avoids double-counting overlaps and quadratic rectangle-union work.
    internal static double Coverage(IEnumerable<PdfRectangle> rectangles, double width, double height)
    {
        if (width <= 0 || height <= 0) return 0;
        const int grid = 32;
        var occupied = new bool[grid * grid];
        foreach (var b in rectangles.Take(100000))
        {
            var left = Math.Clamp((int)Math.Floor(b.Left / width * grid), 0, grid);
            var right = Math.Clamp((int)Math.Ceiling(b.Right / width * grid), 0, grid);
            var bottom = Math.Clamp((int)Math.Floor(b.Bottom / height * grid), 0, grid);
            var top = Math.Clamp((int)Math.Ceiling(b.Top / height * grid), 0, grid);
            for (var y = bottom; y < top; y++) for (var x = left; x < right; x++) occupied[y * grid + x] = true;
        }
        return occupied.Count(x => x) / (double)occupied.Length;
    }
}
