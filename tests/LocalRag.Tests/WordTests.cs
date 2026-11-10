using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class WordTests
{
    internal const string Namespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal static byte[] Package(string body, string styles = "", string header = "", string footer = "", string? rawDocument = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Add(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            var section = "<w:sectPr>" + (header.Length == 0 ? "" : "<w:headerReference w:type=\"default\" r:id=\"header\"/>") +
                (footer.Length == 0 ? "" : "<w:footerReference w:type=\"default\" r:id=\"footer\"/>") + "</w:sectPr>";
            Add("word/document.xml", rawDocument ?? $"<w:document xmlns:w=\"{Namespace}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>{body}{section}</w:body></w:document>");
            Add("word/styles.xml", $"<w:styles xmlns:w=\"{Namespace}\">{styles}</w:styles>");
            Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"main\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"styles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                (header.Length == 0 ? "" : "<Relationship Id=\"header\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>") +
                (footer.Length == 0 ? "" : "<Relationship Id=\"footer\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>") + "</Relationships>");
            if (header.Length > 0) Add("word/header1.xml", $"<w:hdr xmlns:w=\"{Namespace}\">{header}</w:hdr>");
            if (footer.Length > 0) Add("word/footer1.xml", $"<w:ftr xmlns:w=\"{Namespace}\">{footer}</w:ftr>");
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                (header.Length == 0 ? "" : "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>") +
                (footer.Length == 0 ? "" : "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>") + "</Types>");
        }
        return stream.ToArray();
    }
    internal static string Paragraph(string text, string properties = "") => $"<w:p>{properties}<w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>";

    [Fact]
    public void HeadingStylesAndInheritedOutlineLevelsArePreserved()
    {
        var bytes = Package(Paragraph("概要", "<w:pPr><w:pStyle w:val=\"custom\"/></w:pPr>") + Paragraph("説明文。"),
            "<w:style w:type=\"paragraph\" w:styleId=\"base\"><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"custom\"><w:basedOn w:val=\"base\"/></w:style>");
        var converted = WordDocuments.Convert(bytes, new(), 2000);
        Assert.Equal("## 概要\n\n説明文。", converted.Markdown);
        Assert.Equal(2, converted.Blocks[0].HeadingLevel);
    }
    [Fact]
    public void HeaderFooterAreRemovedButLabelsAndDisclosureSourceSurvive()
    {
        var converted = WordDocuments.Convert(Package(Paragraph("本文です。"), header: Paragraph("会社のロゴ") + Paragraph("極秘"), footer: Paragraph("Page 1") + Paragraph("SECRET")), new(), 2000);
        Assert.StartsWith("極秘\nSECRET", converted.Markdown);
        Assert.DoesNotContain("Page 1", converted.Markdown); Assert.DoesNotContain("会社のロゴ", converted.Markdown);
        Assert.Contains("会社のロゴ", converted.DisclosureText); Assert.Contains("Page 1", converted.DisclosureText);
        Assert.True(converted.Blocks[0].Protected);
    }
    [Fact]
    public void CustomLabelsInHeadersAreAvailableForDisclosureEvenWithoutKnownMarker()
    {
        var converted = WordDocuments.Convert(Package(Paragraph("本文"), header: Paragraph("PROJECT-RESTRICTED")), new(), 2000);
        Assert.DoesNotContain("PROJECT-RESTRICTED", converted.Markdown);
        Assert.StartsWith("PROJECT-RESTRICTED", converted.DisclosureText);
    }
    [Theory]
    [InlineData(true, "## 太字の見出し")]
    [InlineData(false, "太字の見出し")]
    public void BoldHeadingInferenceIsOptional(bool infer, string expected)
    {
        var converted = WordDocuments.Convert(Package("<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>太字の見出し</w:t></w:r></w:p>"), new() { InferHeadings = infer }, 2000);
        Assert.Equal(expected, converted.Markdown);
    }
    [Theory]
    [InlineData("日本語の", "文章です", "日本語の文章です")]
    [InlineData("English", "words", "English words")]
    [InlineData("한국어", "문장", "한국어 문장")]
    [InlineData("中文", "内容", "中文内容")]
    public void SoftLineBreaksJoinWithoutDamagingMultilingualWords(string first, string second, string expected)
    {
        var bytes = Package($"<w:p><w:r><w:t>{first}</w:t><w:br/><w:t>{second}</w:t></w:r></w:p>");
        Assert.Equal(expected, WordDocuments.Convert(bytes, new(), 2000).Markdown);
        Assert.Equal(first + "\n" + second, WordDocuments.Convert(bytes, new() { JoinSoftLineBreaks = false }, 2000).Markdown);
    }
    [Fact]
    public void TablesRetainAllCellTextAndEscapeMarkdownDelimiters()
    {
        var bytes = Package("<w:tbl><w:tr><w:tc>" + Paragraph("項目") + "</w:tc><w:tc>" + Paragraph("値|value") + "</w:tc></w:tr><w:tr><w:tc>" + Paragraph("a") + "</w:tc><w:tc>" + Paragraph("b") + "</w:tc></w:tr></w:tbl>");
        var converted = WordDocuments.Convert(bytes, new(), 2000);
        Assert.Contains("| 項目 | 値\\|value |", converted.Markdown);
        Assert.Contains("| a | b |", converted.Markdown); Assert.True(converted.Blocks[0].Protected);
    }
    [Fact]
    public void TrackedDeletionsAndFieldInstructionsNeverBecomeExecutableContent()
    {
        var bytes = Package("<w:p><w:del><w:r><w:delText>削除済み</w:delText></w:r></w:del><w:ins><w:r><w:t>採用本文</w:t></w:r></w:ins><w:r><w:instrText>DDEAUTO something</w:instrText></w:r></w:p>");
        var result = WordDocuments.Convert(bytes, new(), 2000);
        Assert.Equal("採用本文", result.Markdown); Assert.Contains("word_revisions", result.Notices);
    }
    [Fact]
    public void DtdAndUnsupportedEmbeddedContentAreRejected()
    {
        Assert.Equal("word_invalid", Assert.Throws<PipelineValidationException>(() => WordDocuments.Convert(Package("", rawDocument:
            "<!DOCTYPE document [<!ENTITY e SYSTEM 'file:///C:/secret'>]><document>&e;</document>"), new(), 2000)).Code);
        Assert.Equal("word_content_unsupported", Assert.Throws<PipelineValidationException>(() => WordDocuments.Convert(Package("<w:altChunk/>"), new(), 2000)).Code);
        Assert.Equal("word_invalid", Assert.Throws<PipelineValidationException>(() => WordDocuments.Convert([1, 2, 3], new(), 2000)).Code);
    }
    [Fact]
    public void ConvertedTextAndDisclosureHaveBoundedSizes()
    {
        Assert.Equal("document_size", Assert.Throws<PipelineValidationException>(() => WordDocuments.Convert(Package(Paragraph(new string('x', 100))), new(), 50)).Code);
        Assert.Equal("document_size", Assert.Throws<PipelineValidationException>(() => WordDocuments.Convert(Package(Paragraph("x"), header: Paragraph(new string('h', 200))), new(), 50)).Code);
    }
    [Fact]
    public void KnownLabelsRemainAtStartOfDisclosureDespiteLongNormalHeader()
    {
        var header = string.Concat(Enumerable.Range(0, 8).Select(i => Paragraph("ordinary header " + i))) + Paragraph("極秘");
        var converted = WordDocuments.Convert(Package(Paragraph("本文"), header: header), new(), 2000);
        Assert.StartsWith("極秘\n", converted.DisclosureText);
    }
    [Fact]
    public void LeadingBodyLabelRemainsInInspectionWindowAfterHeaderExtraction()
    {
        var header = string.Concat(Enumerable.Range(0, 8).Select(i => Paragraph("ordinary header " + i)));
        var converted = WordDocuments.Convert(Package(Paragraph("極秘") + Paragraph("本文"), header: header), new(), 2000);
        Assert.StartsWith("極秘\n", converted.DisclosureText);
        Assert.StartsWith("極秘\n", converted.Markdown);
    }
    [Fact]
    public void NumberedHeadingsKeepTheirOutlineLevel()
    {
        var converted = WordDocuments.Convert(Package(Paragraph("番号付き見出し", "<w:pPr><w:outlineLvl w:val=\"0\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>")), new(), 2000);
        Assert.Equal("# 1. 番号付き見出し", converted.Markdown);
        Assert.Equal(1, converted.Blocks[0].HeadingLevel);
    }
    [Fact]
    public void StrictOoxmlAndImageAltTextAreReadWithoutFetchingAnything()
    {
        var xml = $"<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\" xmlns:a=\"urn:test\"><w:body><w:p><w:r><w:t>本文</w:t><w:drawing><a:docPr descr=\"図の説明\"/><a:blip a:link=\"https://invalid.example/remote.png\"/></w:drawing></w:r></w:p></w:body></w:document>";
        var converted = WordDocuments.Convert(Package("", rawDocument: xml), new(), 2000);
        Assert.Equal("本文［画像: 図の説明］", converted.Markdown); Assert.Contains("word_images", converted.Notices);
    }
    [Fact]
    public async Task NoLlmModeStillCheckpointsButNeverCallsAi()
    {
        var ai = new WordAi(); var settings = new TextRagSettings { Word = new() { RepairWithLlm = false } };
        var saves = 0;
        var result = await WordStructureRepair.RunAsync(new(ai, settings), settings, [new(1, "章", 1), new(2, "文章")], null,
            _ => { saves++; return Task.CompletedTask; }, default);
        Assert.Equal(0, ai.Calls); Assert.Equal(1, saves); Assert.Equal(new[] { "# 章", "文章" }, result.Blocks);
    }
    [Fact]
    public async Task CancellationResumesAfterCompletedWindowWithoutRepeatingIt()
    {
        var ai = new WordAi(); var settings = new TextRagSettings();
        var blocks = Enumerable.Range(1, 14).Select(i => new WordBlock(i, "paragraph " + i)).ToArray();
        using var cancel = new CancellationTokenSource(); WordRepairProgress? saved = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WordStructureRepair.RunAsync(new(ai, settings), settings, blocks, null,
            next => { saved = next; cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.Equal(6, saved!.NextBlock); Assert.Equal(1, ai.Calls);
        var finished = await WordStructureRepair.RunAsync(new(ai, settings), settings, blocks, saved, _ => Task.CompletedTask, default);
        Assert.Equal(14, finished.NextBlock); Assert.Equal(3, ai.Calls); Assert.Equal(blocks.Select(WordDocuments.Render), finished.Blocks);
    }
    [Fact]
    public void WordRouteHasMarkdownStructureAndSeparateSettings()
    {
        var management = ManagementSettings.CreateDefault();
        Assert.Empty(management.Validate());
        var route = management.RouteFor("Example.DOCX");
        Assert.Equal(SourceCodeMode.Word, route.SourceMode);
        var usage = TextRouteUsage.For(route, new());
        Assert.True(usage.Text && usage.Markdown && usage.StructureParents && usage.ParentDescription);
        var settings = management.Resolve(new(), "Example.docx");
        Assert.Equal(2, TextStructure.Build("# A\n\ntext\n\n# B\n\ntext", "a.docx", settings with { MarkdownParentMinCharacters = 0 }).Parents.Count);
        var old = new AppSettings { Management = management with { Routes = [management.RouteFor("a.txt")] } };
        var updated = old with { Management = old.Management! with { Routes = [route, .. old.Management.Routes!] } };
        Assert.Equal(ProcessingPlan.Create(old, "a.txt", null).Id, ProcessingPlan.Create(updated, "a.txt", null).Id);
    }
    [Theory]
    [InlineData("{\"headings\":[{\"id\":9,\"level\":1}],\"join\":[]}")]
    [InlineData("{\"headings\":[{\"id\":2,\"level\":8}],\"join\":[]}")]
    [InlineData("{\"headings\":[],\"join\":[{\"start\":2,\"end\":1}]}")]
    [InlineData("{\"headings\":[{\"id\":1,\"level\":2}],\"join\":[]}")]
    [InlineData("{\"headings\":[],\"join\":[{\"start\":1,\"end\":2}]}")]
    public async Task InvalidLlmEditsAreRetriedThenOriginalIsPreserved(string response)
    {
        var ai = new WordAi { Response = response };
        var options = new TextRagSettings();
        var result = await WordStructureRepair.RunAsync(new(ai, options), options, [new(1, "極秘", Protected: true), new(2, "説明")], null, _ => Task.CompletedTask, default);
        Assert.Equal(2, ai.Calls); Assert.Equal(new[] { "極秘", "説明" }, result.Blocks);
        Assert.Contains("word_repair_preserved_invalid", result.Notices);
    }
    [Fact]
    public async Task ConstrainedLlmChangesOnlyHeadingAndSeparators()
    {
        var ai = new WordAi { Response = "{\"headings\":[{\"id\":4,\"level\":1}],\"join\":[{\"start\":5,\"end\":6}]}" };
        var options = new TextRagSettings();
        var result = await WordStructureRepair.RunAsync(new(ai, options), options,
            [new(4, "章タイトル"), new(5, "これは途中の"), new(6, "文章です。")], null, _ => Task.CompletedTask, default);
        Assert.Equal(new[] { "# 章タイトル", "これは途中の文章です。" }, result.Blocks);
    }
    [Fact]
    public async Task SmallContextAndCheckpointNeverDropLongParagraphs()
    {
        var ai = new WordAi { Budget = 350 };
        var options = new TextRagSettings();
        var blocks = new WordBlock[] { new(1, "済み"), new(2, new string('漢', 200)), new(3, "最後") };
        var result = await WordStructureRepair.RunAsync(new(ai, options), options, blocks, new(1, ["済み"], []), _ => Task.CompletedTask, default);
        Assert.Equal(3, result.NextBlock); Assert.Equal(new[] { "済み", new string('漢', 200), "最後" }, result.Blocks);
        Assert.Contains("word_repair_preserved_long", result.Notices); Assert.All(ai.InputSizes, n => Assert.True(n <= 350));
    }
}

internal sealed class WordAi : ITextRagAi
{
    public int Budget { get; init; } = 3000;
    public int Calls { get; private set; }
    public int Embeddings { get; private set; }
    public List<int> InputSizes { get; } = [];
    public string Response { get; init; } = "{\"headings\":[],\"join\":[]}";
    public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => Task.FromResult(Budget);
    public Task<string> CompleteAsync(string system, string input, CancellationToken token)
    { Calls++; InputSizes.Add(Encoding.UTF8.GetByteCount(input)); return Task.FromResult(Response); }
    public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> input, CancellationToken token)
    { Embeddings += input.Count; return Task.FromResult(new VectorBatch(3, input.Select(_ => new float[] { 1, 0, 0 }).ToArray())); }
}
