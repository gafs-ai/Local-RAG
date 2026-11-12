using System.IO.Compression;
using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class SpreadsheetReaderTests
{
    private static TextRagSettings Options(SourceCodeMode mode = SourceCodeMode.Excel) => new()
        { SourceMode = mode, MaxDocumentCharacters = 100000, Spreadsheet = new() };
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void XlsxKeepsTypesRawNumbersFormulaCachesCoordinatesAndTableDefinitions()
    {
        var document = SpreadsheetDocuments.Convert(Book("""
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>金額</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>商品A</t></is></c><c r="B2" s="1"><v>0.125</v></c><c r="C2" t="b"><v>1</v></c><c r="D2" t="e"><v>#N/A</v></c></row>
            <row r="3"><c r="A3" s="2"><v>1</v></c><c r="B3"><f>SUM(B2:B2)</f><v>0.125</v></c><c r="C3"><f>NOW()</f></c></row>
            """, extraSheet: "<mergeCells><mergeCell ref=\"A4:C4\"/></mergeCells><tableParts><tablePart r:id=\"t1\"/></tableParts>"), "book.xlsx", Options());
        var sheet = Assert.Single(document.Sheets);
        Assert.Equal("製品", sheet.Cells.Single(c => c.Row == 1 && c.Column == 1).Text);
        var numeric = sheet.Cells.Single(c => c.Row == 2 && c.Column == 2);
        Assert.Equal("number", numeric.Kind); Assert.Equal("0.125", numeric.RawValue); Assert.Equal("0%", numeric.NumberFormat);
        Assert.True(numeric.Bold);
        Assert.Equal("TRUE", sheet.Cells.Single(c => c.Column == 3 && c.Row == 2).Text);
        Assert.Equal("date", sheet.Cells.Single(c => c.Column == 1 && c.Row == 3).Kind);
        Assert.StartsWith("1900-01-01", sheet.Cells.Single(c => c.Column == 1 && c.Row == 3).Text);
        Assert.Equal("SUM(B2:B2)", sheet.Cells.Single(c => c.Column == 2 && c.Row == 3).Formula);
        Assert.Null(sheet.Cells.Single(c => c.Column == 3 && c.Row == 3).RawValue);
        Assert.Contains("spreadsheet_formula_cache_missing", document.Notices);
        Assert.Contains("spreadsheet_cell_errors", document.Notices);
        Assert.Equal("A4:C4", Assert.Single(sheet.MergedRanges));
        Assert.Equal("A1:B3", Assert.Single(sheet.Tables).Range);
        Assert.Equal("Products", Assert.Single(sheet.Tables).Name);
    }

    [Fact]
    public void HiddenCellsRemainForDisclosureButAreNotRenderedForNormalRetrieval()
    {
        var document = SpreadsheetDocuments.Convert(Book("""
            <row r="1"><c r="A1" t="inlineStr"><is><t>visible</t></is></c><c r="B1" t="inlineStr"><is><t>SECRET column</t></is></c></row>
            <row r="2" hidden="1"><c r="A2" t="inlineStr"><is><t>SECRET row</t></is></c></row>
            """, prefixSheet: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"), "book.xlsx", Options());
        Assert.Contains("SECRET row", SpreadsheetDocuments.Render(document));
        Assert.Contains("SECRET column", SpreadsheetDocuments.Render(document));
        Assert.DoesNotContain("SECRET", SpreadsheetDocuments.Render(document, false));
        Assert.Throws<ArgumentException>(() => SpreadsheetDocuments.RenderRange(document, "Sheet1", "A1:B2"));
        Assert.Contains("SECRET", SpreadsheetDocuments.RenderRange(document, "Sheet1", "A1:B2", includeHidden: true));
        Assert.DoesNotContain("SECRET", SpreadsheetDocuments.RenderRange(document, "Sheet1", "A1"));
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    [InlineData("B2:A1")]
    [InlineData("A1:B2:C3")]
    [InlineData("Sheet1!A1")]
    public void InvalidCoordinatesAreRejected(string range) => Assert.Throws<ArgumentException>(() => SpreadsheetDocuments.ParseRange(range));

    [Fact]
    public void RangePreservesExactSourceCellsAndBounds()
    {
        var document = SpreadsheetDocuments.ReadCsv("name,value\nitem,42\nother,90", new());
        var output = SpreadsheetDocuments.RenderRange(document, "CSV", "$B$2:B2");
        Assert.Contains("B2 [number] 42", output); Assert.DoesNotContain("90", output); Assert.DoesNotContain("item", output);
        Assert.Throws<ArgumentException>(() => SpreadsheetDocuments.RenderRange(document, "CSV", "A1:XFD1048576"));
        Assert.Throws<ArgumentException>(() => SpreadsheetDocuments.RenderRange(document, "unknown", "A1"));
        Assert.Equal("XFD1048576", SpreadsheetDocuments.CellAddress(1048576, 16384));
    }

    [Fact]
    public void CsvParsesQuotedNewlinesEscapedQuotesTrailingEmptyFieldsAndLeadingZeroIds()
    {
        var text = "id,description,amount,empty\r\n001,\"line1\r\nline2,\"\"quoted\"\"\",12.50,\r\n002,next,13,\r\n";
        var document = SpreadsheetDocuments.ReadCsv(text, new());
        var cells = Assert.Single(document.Sheets).Cells;
        Assert.Equal("001", cells.Single(c => c.Row == 2 && c.Column == 1).Text);
        Assert.Equal("text", cells.Single(c => c.Row == 2 && c.Column == 1).Kind);
        Assert.Equal("line1\r\nline2,\"quoted\"", cells.Single(c => c.Row == 2 && c.Column == 2).Text);
        Assert.Equal("number", cells.Single(c => c.Row == 2 && c.Column == 3).Kind);
        Assert.Equal(3, cells.Max(c => c.Row));
        Assert.DoesNotContain("spreadsheet_csv_ragged", document.Notices);
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    public void CsvDelimiterDetectionAndOverride(string delimiter)
    {
        var text = string.Join(delimiter, "項目", "説明") + "\n" + string.Join(delimiter, "例", "本文");
        Assert.Equal(4, SpreadsheetDocuments.ReadCsv(text, new()).Sheets[0].Cells.Count);
        Assert.Equal(4, SpreadsheetDocuments.ReadCsv(text, new() { CsvDelimiter = delimiter }).Sheets[0].Cells.Count);
    }

    [Theory]
    [InlineData("a,\"unterminated")]
    [InlineData("a,ab\"cd")]
    [InlineData("a,\"closed\"junk")]
    public void BrokenCsvIsRejectedInsteadOfSilentlyLosingCells(string text) =>
        Assert.Equal("csv_invalid", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.ReadCsv(text, new() { CsvDelimiter = "," })).Code);

    [Fact]
    public void CsvEncodingUsesSharedDecoderAndBoms()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "名前,説明\r\n製品,安全性についての説明\r\n";
        var shiftJis = SpreadsheetDocuments.Convert(Encoding.GetEncoding(932).GetBytes(text), "book.csv", Options(SourceCodeMode.Csv) with { EncodingMode = TextEncodingMode.ShiftJis });
        Assert.Equal("製品", shiftJis.Sheets[0].Cells.Single(c => c.Row == 2 && c.Column == 1).Text);
        var utf8 = SpreadsheetDocuments.Convert([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)], "book.csv", Options(SourceCodeMode.Csv));
        Assert.Equal(shiftJis.Sheets[0].Cells, utf8.Sheets[0].Cells);
    }

    [Fact]
    public void CellLimitsXmlDtdAndInvalidSharedStringsFailClosed()
    {
        Assert.Equal("spreadsheet_cells", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.ReadCsv("a,b\nc,d", new() { MaxCells = 3 })).Code);
        var options = Options() with { Spreadsheet = new() { MaxCells = 1 } };
        Assert.Equal("spreadsheet_cells", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(Book("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"B1\"><v>2</v></c></row>"), "x.xlsx", options)).Code);
        Assert.Equal("spreadsheet_invalid", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(Book("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>99</v></c></row>"), "x.xlsx", Options())).Code);
        var corrupt = Archive(new() { ["xl/workbook.xml"] = "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///secret'>]><workbook>&secret;</workbook>" });
        Assert.Equal("spreadsheet_invalid", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(corrupt, "x.xlsx", Options())).Code);
    }

    [Fact]
    public void ExternalRelationshipsAreNeverFetchedAndEscapingTargetsAreRejected()
    {
        var parts = Parts("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>");
        parts["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"r1\" Target=\"https://example.invalid/sheet.xml\" TargetMode=\"External\"/></Relationships>";
        Assert.Equal("spreadsheet_invalid", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(Archive(parts), "x.xlsx", Options())).Code);
        parts["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"r1\" Target=\"../../outside.xml\"/></Relationships>";
        Assert.Equal("spreadsheet_invalid", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(Archive(parts), "x.xlsx", Options())).Code);
    }

    [Fact]
    public void Date1904AndPhoneticAnnotationsAreHandledWithoutRewritingRawValues()
    {
        var parts = Parts("<row r=\"1\"><c r=\"A1\" s=\"2\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>0</v></c></row>");
        parts["xl/workbook.xml"] = parts["xl/workbook.xml"].Replace("<sheets>", "<workbookPr date1904=\"1\"/><sheets>");
        parts["xl/sharedStrings.xml"] = $"<sst xmlns=\"{Ns}\"><si><t>漢字</t><rPh sb=\"0\" eb=\"2\"><t>カンジ</t></rPh></si></sst>";
        var document = SpreadsheetDocuments.Convert(Archive(parts), "x.xlsx", Options());
        Assert.StartsWith("1904-01-01", document.Sheets[0].Cells[0].Text);
        Assert.Equal("0", document.Sheets[0].Cells[0].RawValue);
        Assert.Equal("漢字", document.Sheets[0].Cells[1].Text);
    }

    [Fact]
    public void ExcessiveZipPartCountIsRejectedAndCancellationIsObserved()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            for (var i = 0; i < 10001; i++) zip.CreateEntry("part" + i);
        Assert.Equal("spreadsheet_size", Assert.Throws<PipelineValidationException>(() => SpreadsheetDocuments.Convert(stream.ToArray(), "x.xlsx", Options())).Code);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpreadsheetDocuments.Convert([], "x.xlsx", Options(), cts.Token));
    }

    [Fact]
    public void NumericColorFormatsAndElapsedTimeDoNotBecomeCalendarDates()
    {
        var parts = Parts("<row r=\"1\"><c r=\"A1\" s=\"1\"><v>42.5</v></c><c r=\"B1\" s=\"2\"><v>2.5</v></c></row>");
        parts["xl/styles.xml"] = $"<styleSheet xmlns=\"{Ns}\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"[Red]0.00\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/><xf numFmtId=\"46\"/></cellXfs></styleSheet>";
        var cells = SpreadsheetDocuments.Convert(Archive(parts), "x.xlsx", Options()).Sheets[0].Cells;
        Assert.All(cells, cell => Assert.Equal("number", cell.Kind));
        Assert.Equal("42.5", cells[0].Text); Assert.Equal("2.5", cells[1].Text);
        Assert.Equal("[h]:mm:ss", cells[1].NumberFormat);
    }

    private static byte[] Book(string rows, string extraSheet = "", string prefixSheet = "") => Archive(Parts(rows, extraSheet, prefixSheet));
    private static Dictionary<string, string> Parts(string rows, string extraSheet = "", string prefixSheet = "") => new()
    {
        ["xl/workbook.xml"] = $"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"r1\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
        ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{prefixSheet}<sheetData>{rows}</sheetData>{extraSheet}</worksheet>",
        ["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships><Relationship Id=\"t1\" Target=\"../tables/table1.xml\"/></Relationships>",
        ["xl/tables/table1.xml"] = $"<table xmlns=\"{Ns}\" displayName=\"Products\" ref=\"A1:B3\"/>",
        ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{Ns}\"><si><r><t>製</t></r><r><t>品</t></r></si></sst>",
        ["xl/styles.xml"] = $"<styleSheet xmlns=\"{Ns}\"><fonts><font/><font><b/></font></fonts><cellXfs><xf numFmtId=\"0\" fontId=\"0\"/><xf numFmtId=\"9\" fontId=\"1\"/><xf numFmtId=\"14\" fontId=\"0\"/></cellXfs></styleSheet>"
    };
    private static byte[] Archive(Dictionary<string, string> parts)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content);
            }
        return stream.ToArray();
    }
}
