using System.IO.Compression;
using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class ExcelAdditionalFormatsTests
{
    [Fact]
    public async Task XlsmReadsCellsAndIgnoresVbaWithoutStartingExcel()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-xlsm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "macro.xlsm");
            var bytes = Archive(new() {
                ["xl/workbook.xml"] = """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Data" sheetId="1" r:id="s1"/></sheets></workbook>""",
                ["xl/_rels/workbook.xml.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Target="worksheets/sheet1.xml"/><Relationship Id="vba" Target="vbaProject.bin"/></Relationships>""",
                ["xl/worksheets/sheet1.xml"] = """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>顧客対応状況</t></is></c><c r="B1"><f>21*2</f><v>42</v></c></row></sheetData></worksheet>""",
                ["xl/vbaProject.bin"] = "Not executable: VBA payload must never be parsed as cells"
            });
            await File.WriteAllBytesAsync(path, bytes);
            var result = await DocumentReader.ReadAsync(path, new() { SourceMode = SourceCodeMode.Excel }, root, default);
            Assert.Contains("顧客対応状況", result.Text);
            Assert.DoesNotContain("VBA payload", result.Text);
            Assert.Contains("spreadsheet_macros_ignored", result.Notices);
            var formula = Assert.Single(result.Spreadsheet!.Sheets).Cells.Single(c => c.Column == 2);
            Assert.Equal("21*2", formula.Formula); Assert.Equal("42", formula.RawValue);
            Assert.False(Directory.Exists(Path.Combine(root, "excel-work")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void XlsbHasSeparateReadRecipeAndReusesItAfterEmbeddingChange()
    {
        var app = new AppSettings { Management = ManagementSettings.CreateDefault() };
        var read = ProcessingPlan.Create(app, "book.xlsb", "general").Read;
        Assert.Equal("excel-binary-reader/v1", read.Method);
        Assert.NotEqual(read.Id, ProcessingPlan.Create(app, "book.xls", "general").Read.Id);
        Assert.Equal(read.Id, ProcessingPlan.Create(app with { Ai = app.Ai with { EmbeddingModel = "other" } }, "book.xlsb", "general").Read.Id);
        Assert.NotNull(DocumentInputs.ExtractionNotice("book.xlsm"));
        Assert.NotNull(DocumentInputs.ExtractionNotice("book.xlsb"));
    }

    [Fact]
    public void BinaryPackageAllowsVbaWhichIsDisabledByConverter()
    {
        var entries = BinaryParts();
        entries["xl/vbaProject.bin"] = "opaque VBA bytes";
        XlsbPreflight.Validate(Archive(entries));
    }

    [Fact]
    public void WorkbookTypeMayUsePackageDefaultAndOverridesTakePrecedence()
    {
        var entries = BinaryParts();
        entries["[Content_Types].xml"] = entries["[Content_Types].xml"].Replace("Override PartName=\"/xl/workbook.bin\"", "Default Extension=\"bin\"");
        XlsbPreflight.Validate(Archive(entries));
        entries["[Content_Types].xml"] = entries["[Content_Types].xml"].Replace("</Types>", "<Override PartName=\"/xl/workbook.bin\" ContentType=\"wrong\"/></Types>");
        Assert.Equal("excel_invalid", Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
    }

    [Theory]
    [InlineData("xl/macrosheets/sheet1.bin", "excel_macro_sheet")]
    [InlineData("xl/connections.bin", "excel_external_data")]
    [InlineData("xl/queryTables/query1.bin", "excel_external_data")]
    [InlineData("xl/externalLinks/externalLink1.bin", "excel_external_data")]
    [InlineData("xl/embeddings/oleObject1.bin", "excel_external_data")]
    public void BinaryPackageRejectsAutoExecutableOrRefreshableParts(string part, string code)
    {
        var entries = BinaryParts(); entries[part] = "opaque";
        Assert.Equal(code, Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
    }

    [Fact]
    public void MacroSheetTypeAndRelationAreCheckedEvenWithInnocentFilename()
    {
        var entries = BinaryParts();
        entries["[Content_Types].xml"] = entries["[Content_Types].xml"].Replace("</Types>", "<Override PartName=\"/xl/innocent.bin\" ContentType=\"application/vnd.ms-excel.macrosheet\"/></Types>");
        Assert.Equal("excel_macro_sheet", Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
        entries = BinaryParts();
        entries["xl/_rels/workbook.bin.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="m" Type="http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet" Target="innocent.bin"/></Relationships>""";
        Assert.Equal("excel_macro_sheet", Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
    }

    [Theory]
    [InlineData("<Relationship Id=\"x\" Type=\"externalData\" TargetMode=\"External\" Target=\"https://example.invalid/data\"/>", "excel_external_data")]
    [InlineData("<Relationship Id=\"x\" Type=\"worksheet\" Target=\"../../outside.bin\"/>", "excel_invalid")]
    public void InvalidRelationsNeverReachExcel(string relationship, string code)
    {
        var entries = BinaryParts();
        entries["xl/_rels/workbook.bin.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{relationship}</Relationships>";
        Assert.Equal(code, Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
    }

    [Fact]
    public void RejectsDtdInvalidWorkbookAndCancellation()
    {
        var entries = BinaryParts(); entries["[Content_Types].xml"] = "<!DOCTYPE Types [<!ENTITY payload SYSTEM 'file:///C:/private'>]><Types>&payload;</Types>";
        Assert.Equal("excel_invalid", Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
        entries = BinaryParts(); entries.Remove("xl/workbook.bin");
        Assert.Equal("excel_invalid", Assert.Throws<PipelineValidationException>(() => XlsbPreflight.Validate(Archive(entries))).Code);
        Assert.Throws<OperationCanceledException>(() => XlsbPreflight.Validate([], new CancellationToken(true)));
    }

    [Fact]
    public async Task UnsafeXlsbIsRejectedBeforeWorkspaceCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-xlsb-" + Guid.NewGuid().ToString("N"));
        var entries = BinaryParts(); entries["xl/connections.bin"] = "opaque";
        Assert.Equal("excel_external_data", (await Assert.ThrowsAsync<PipelineValidationException>(() =>
            LegacyExcelConversion.ConvertAsync(Archive(entries), root, default, ".xlsb"))).Code);
        Assert.False(Directory.Exists(root));
    }

    private static Dictionary<string, string> BinaryParts() => new() {
        ["[Content_Types].xml"] = """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.bin" ContentType="application/vnd.ms-excel.sheet.binary.macroEnabled.main"/></Types>""",
        ["_rels/.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="w" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.bin"/></Relationships>""",
        ["xl/workbook.bin"] = "Opaque binary workbook, interpreted only by Excel",
        ["xl/_rels/workbook.bin.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>"""
    };

    private static byte[] Archive(Dictionary<string, string> parts)
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
            foreach (var (name, value) in parts) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); }
        return result.ToArray();
    }
}
