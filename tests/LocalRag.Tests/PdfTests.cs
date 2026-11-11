using LocalRag.Core;
using LocalRag.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LocalRag.Tests;

public class PdfTests
{
    internal static TextRagSettings Options(PdfInputPolicy policy = PdfInputPolicy.Automatic) => new() {
        SourceMode = SourceCodeMode.Pdf, Pdf = new() { Policy = policy }, Word = new() { RepairWithLlm = false },
        MaxDocumentCharacters = 200000, MarkdownParentMinCharacters = 0, MarkdownParentMaxCharacters = 4000 };

    internal static byte[] Document(int count = 4, Func<int, string>? kind = null, string header = "Company Report", int footerY = 25)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var number = 1; number <= count; number++)
        {
            var page = builder.AddPage(PageSize.A4);
            var type = kind?.Invoke(number) ?? "text";
            if (type == "blank") continue;
            if (type == "image")
            {
                page.AddPng(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), new PdfRectangle(0, 0, 595, 842));
                continue;
            }
            if (type == "visual") { page.DrawRectangle(new PdfPoint(30, 80), 500, 650, fill: true); continue; }
            page.AddText(header, 10, new PdfPoint(40, 810), font);
            page.AddText("[Page " + number + "]", 10, new PdfPoint(40, footerY), font);
            page.AddText(type == "contents" ? "Contents" : "Garden maintenance", 20, new PdfPoint(40, 740), font);
            if (type == "short") { page.AddText("Water plants in the morning.", 12, new PdfPoint(40, 700), font); continue; }
            for (var line = 0; line < 9; line++) page.AddText($"Garden care section {number}: water the flowers each morning before work.",
                11, new PdfPoint(40, 700 - line * 20), font);
        }
        return builder.Build();
    }

    [Fact]
    public void QuartilesComeFirstAndSamplingIsBounded()
    {
        Assert.Equal(new[] { 25, 50, 75, 13, 38, 63, 88 }, PdfSampling.Order(100).Take(7));
        var result = PdfSampling.Assess(100, new(), p => new(p, 600, 9, 0, 0, "text"), default);
        Assert.Equal("convert", result.Decision); Assert.Equal(3, result.Samples.Count);
        var unclear = PdfSampling.Assess(100, new(), p => new(p, 100, 1, 0, 0, "uncertain"), default);
        Assert.Equal("skip", unclear.Decision); Assert.Equal("pdf_uncertain", unclear.Reason); Assert.Equal(15, unclear.Samples.Count);
        Assert.DoesNotContain(unclear.Samples, p => p.Page is 1 or 100);
    }

    [Fact]
    public void MixedEvidenceExpandsAndShortDocumentsInspectEveryPage()
    {
        var result = PdfSampling.Assess(100, new(), p => new(p, 600, 9, 0, 0, p == 25 ? "visual" : "text"), default);
        Assert.Equal("convert", result.Decision); Assert.Equal(7, result.Samples.Count);
        var small = PdfSampling.Assess(4, new(), p => new(p, 600, 9, 0, 0, p is 1 or 4 ? "auxiliary" : "text"), default);
        Assert.Equal("convert", small.Decision); Assert.Equal(4, small.Samples.Count);
    }

    [Fact]
    public void ContentsPagesDoNotDecideTextSuitability()
    {
        var result = PdfSampling.Assess(100, new(), p => new(p, 800, 12, 0, 0, p % 25 == 0 ? "auxiliary" : "text"), default);
        Assert.Equal("convert", result.Decision); Assert.True(result.Samples.Count > 3);
        Assert.Contains(result.Samples, p => p.Page == 26);
    }

    [Fact]
    public void ConversionPreservesPagesAndSeparatesRemovedHeadersFromDisclosure()
    {
        var result = PdfDocuments.Convert(Document(), Options());
        Assert.Equal("convert", result.Pdf!.Decision);
        Assert.Contains("# PDFページ 4", result.Text); Assert.Contains("Garden care section 2", result.Text);
        Assert.DoesNotContain("Company Report", result.Text); Assert.DoesNotContain("Page 1", result.Text);
        Assert.Contains("Company Report", result.Word!.DisclosureText); Assert.Contains("pdf_headers_removed", result.Notices);
        var parents = TextStructure.Build(result.Text, "report.pdf", Options()).Parents;
        Assert.Contains(parents, p => p.Title!.Contains("PDFページ 4"));
        Assert.All(result.Word.Blocks.Where(b => b.Text.StartsWith("PDFページ")), b => Assert.True(b.Protected));
    }

    [Fact]
    public void KnownConfidentialLabelIsNeverRemovedByHeaderFilter()
    {
        var result = PdfDocuments.Convert(Document(header: "TOP SECRET"), Options());
        Assert.Contains("TOP SECRET", result.Text); Assert.StartsWith("TOP SECRET", result.Word!.DisclosureText);
        Assert.All(result.Word.Blocks.Where(b => b.Text.Contains("TOP SECRET")), b => Assert.True(b.Protected));
    }

    [Fact]
    public void IsolatedRepeatedFootersInsidePageMarginsCanBeRemovedOrRetained()
    {
        var bytes = Document(footerY: 130);
        Assert.DoesNotContain("[Page 1]", PdfDocuments.Convert(bytes, Options()).Text);
        var retained = PdfDocuments.Convert(bytes, Options() with { Pdf = new() { RemoveHeadersFooters = false } });
        Assert.Contains("[Page 1]", retained.Text); Assert.Contains("Company Report", retained.Text);
    }

    [Fact]
    public void LateVisualPageDoesNotInvalidateExtractedText()
    {
        var result = PdfDocuments.Convert(Document(16, p => p == 15 ? "visual" : "text"), Options());
        Assert.Equal("convert", result.Pdf!.Decision); Assert.Equal(new[] { 15 }, result.Pdf.TextUnavailablePages);
        Assert.Contains("pdf_pages_without_text", result.Notices); Assert.Contains("# PDFページ 16", result.Text);
    }

    [Fact]
    public void VisualAndUncertainDocumentsSkipWithoutCallingAi()
    {
        var visual = PdfDocuments.Convert(Document(16, _ => "visual"), Options());
        Assert.Empty(visual.Text); Assert.Equal("pdf_visual", visual.Pdf!.Reason); Assert.Equal(3, visual.Pdf.Samples.Count);
        var brief = Document(1, _ => "short");
        Assert.Empty(PdfDocuments.Convert(brief, Options()).Text);
        Assert.Contains("Water plants", PdfDocuments.Convert(brief, Options(PdfInputPolicy.ExtractText)).Text);
        Assert.Empty(PdfDocuments.Convert(brief, Options(PdfInputPolicy.Skip)).Text);
    }

    [Fact]
    public void InvalidInputLimitsAndCancellationAreExplicit()
    {
        Assert.Equal("pdf_invalid", Assert.Throws<PipelineValidationException>(() => PdfDocuments.Convert([1, 2, 3], Options())).Code);
        Assert.Equal("document_size", Assert.Throws<PipelineValidationException>(() => PdfDocuments.Convert(Document(), Options() with { MaxDocumentCharacters = 40 })).Code);
        Assert.ThrowsAny<OperationCanceledException>(() => PdfDocuments.Convert(Document(), Options(), new CancellationToken(true)));
        Assert.NotEmpty(new PdfSettings { MaxSamplePages = 2 }.Validate());
    }

    [Fact]
    public void RasterScansAreSkippedAndEmptyExtractionRemainsSkippedWhenForced()
    {
        var bytes = Document(8, _ => "image");
        var result = PdfDocuments.Convert(bytes, Options());
        Assert.Equal("pdf_visual", result.Pdf!.Reason);
        Assert.All(result.Pdf.Samples, p => Assert.True(p.ImageCoverage > .9));
        var forced = PdfDocuments.Convert(bytes, Options(PdfInputPolicy.ExtractText));
        Assert.Equal("pdf_no_text", forced.Pdf!.Reason); Assert.Empty(forced.Text);
    }

    [Fact]
    public async Task LlmFormattingCannotRemovePdfPageMarkersOrConfidentialLabels()
    {
        var options = Options() with { FormatEnabled = true };
        var ai = new WordAi { Response = "{\"remove\":[1,2]}" };
        List<TextParagraph> paragraphs = [new(1, "# PDFページ 1"), new(2, "SECRET"), new(3, "The long original text describes the garden maintenance schedule.")];
        var result = await new TextAiPipeline(ai, options).FormatAsync(paragraphs, new() { Id = "general" }, default);
        Assert.Equal(paragraphs, result);
    }

    [Fact]
    public void PdfDefaultsAreStructuredAndDoNotChangeSavedRoutes()
    {
        var settings = ManagementSettings.CreateDefault(); Assert.Empty(settings.Validate());
        var resolved = settings.Resolve(new(), "test.pdf");
        Assert.Equal(SourceCodeMode.Pdf, resolved.SourceMode); Assert.Equal(ParentChunkMode.Structure, resolved.ParentMode);
        Assert.Equal(4000, resolved.MarkdownParentMaxCharacters); Assert.Equal(PdfInputPolicy.Automatic, resolved.Pdf.Policy);
        Assert.Contains("PDF", resolved.Word.RepairPrompt);
        Assert.True(TextRouteUsage.For(settings.RouteFor("test.pdf"), new()).Markdown);
    }
}
