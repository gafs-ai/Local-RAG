using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class LegacyExcelConversionTests
{
    [Fact]
    public async Task UnsafeInputIsRejectedBeforeWritingAConversionWorkspaceOrStartingExcel()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", "xls-preflight", Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => LegacyExcelConversion.ConvertAsync([1, 2, 3], root, default));
        Assert.Equal("excel_invalid", error.Code);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeReadingInputOrStartingOffice()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", "xls-cancel", Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyExcelConversion.ConvertAsync([], root, cancellation.Token));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void EmbeddedConverterHasRequiredReadOnlyAndOwnershipGuards()
    {
        var script = LegacyExcelConversion.Script();
        Assert.Contains("$previousIds = @(Get-Process EXCEL", script);
        Assert.Contains("[IntPtr]$excel.Hwnd", script);
        Assert.Contains("$previousIds -contains $excelProcessId", script);
        Assert.Contains("StartUtcTicks", script);
        Assert.Contains("if ($owned)", script);
        Assert.Contains("$excel.AutomationSecurity = 3", script);
        Assert.Contains("$excel.EnableEvents = $false", script);
        Assert.Contains("$excel.CalculateBeforeSave = $false", script);
        Assert.Contains("$excel.Calculation = -4135", script);
        Assert.Contains("$workbooks.Open((Join-Path $PSScriptRoot 'input.xls'), 0, $true", script);
        Assert.Contains("$password = [Guid]::NewGuid()", script);
        Assert.Contains("$workbook.SaveAs($output, 51", script);
        Assert.DoesNotContain("GetActiveObject", script);
        Assert.DoesNotContain(".RefreshAll(", script);
        Assert.DoesNotContain(".Calculate(", script);
    }

    [ExcelLegacyTheory]
    [InlineData(".xls", 56)]
    [InlineData(".xlsb", 50)]
    [InlineData(".xlsm", 52)]
    public async Task InstalledExcelFormatsPreserveValuesFormulaAndSource(string extension, int format)
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", "xls-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Reuse the exact process ownership, no-events and bounded cleanup protocol to create test data.
            // Replace only the input-open block and output format; never touch an existing workbook.
            const string create = """
                $workbook = $workbooks.Add(-4167)
                $sheet = $workbook.Worksheets.Item(1)
                $sheet.Name = 'Fixture'
                $range = $sheet.Range('A1', 'B2')
                $values = New-Object 'object[,]' 2,2
                $values[0,0] = 'Item'; $values[0,1] = 'Value'
                $values[1,0] = 'Synthetic fixture'; $values[1,1] = 42
                $range.Value2 = $values
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($range)
                $formula = $sheet.Range('B3')
                $formula.Formula = '=B2*2'
                $formula.Calculate()
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($formula)
                $merged = $sheet.Range('A4', 'B4')
                $merged.Merge()
                $merged.Value2 = 'Merged section'
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($merged)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($sheet)
                """;
            var script = Regex.Replace(LegacyExcelConversion.Script(), @"# BEGIN INPUT OPEN.*?# END INPUT OPEN",
                _ => create, RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                .Replace("'output.xlsx'", "'output" + extension + "'", StringComparison.Ordinal)
                .Replace("$workbook.SaveAs($output, 51", "$workbook.SaveAs($output, " + format, StringComparison.Ordinal);
            var input = await LegacyExcelConversion.RunWorkerAsync([], root, script, "output" + extension, default);
            if (Environment.GetEnvironmentVariable("LOCALRAG_TEST_FIXTURE_DIR") is { Length: > 0 } fixtureDirectory)
            {
                Directory.CreateDirectory(fixtureDirectory);
                await File.WriteAllBytesAsync(Path.Combine(fixtureDirectory, "fixture" + extension), input);
            }
            var file = Path.Combine(root, "fixture" + extension);
            await File.WriteAllBytesAsync(file, input);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var settings = new TextRagSettings { SourceMode = SourceCodeMode.Excel, MaxDocumentCharacters = 100000 };
            var snapshot = await DocumentReader.ReadAsync(file, settings, root, default);
            Assert.Equal(hash, snapshot.Hash);
            Assert.Equal(input, await File.ReadAllBytesAsync(file));
            if (extension != ".xlsm") Assert.Contains(extension == ".xls" ? "spreadsheet_legacy_converted" : "spreadsheet_binary_converted", snapshot.Notices);
            else Assert.DoesNotContain(snapshot.Notices, n => n.EndsWith("_converted", StringComparison.Ordinal));
            var sheet = Assert.Single(snapshot.Spreadsheet!.Sheets);
            Assert.Equal("Fixture", sheet.Name);
            Assert.Equal("42", sheet.Cells.Single(c => c.Row == 2 && c.Column == 2).RawValue);
            var formula = sheet.Cells.Single(c => c.Row == 3 && c.Column == 2);
            Assert.Equal("B2*2", formula.Formula);
            Assert.Equal("84", formula.RawValue);
            Assert.Contains("A4:B4", sheet.MergedRanges);
            Assert.Contains("Synthetic fixture", snapshot.Text);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "excel-work")));
        }
        finally
        {
            var working = Path.Combine(root, "excel-work");
            if (!Directory.Exists(working) || !Directory.EnumerateDirectories(working).Any()) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class ExcelLegacyFactAttribute : FactAttribute
{
    public ExcelLegacyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOCALRAG_TEST_EXCEL") != "1")
            Skip = "Opt in with LOCALRAG_TEST_EXCEL=1 to use installed Excel with synthetic workbooks.";
    }
}

public sealed class ExcelLegacyTheoryAttribute : TheoryAttribute
{
    public ExcelLegacyTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOCALRAG_TEST_EXCEL") != "1") Skip = "Opt in with LOCALRAG_TEST_EXCEL=1 to use installed Excel with synthetic workbooks.";
    }
}
