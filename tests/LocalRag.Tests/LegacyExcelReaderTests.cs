using System.Security.Cryptography;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class LegacyExcelReaderTests
{
    private static AppSettings Settings() => new() { Management = ManagementSettings.CreateDefault() };

    [Fact]
    public void ConversionRecipeIsDistinctAndEmbeddingChangesDoNotInvalidateIt()
    {
        var app = Settings();
        var plan = ProcessingPlan.Create(app, "book.xls", "general");
        Assert.Equal("excel-legacy-reader/v1", plan.Read.Method);
        Assert.NotEqual(plan.Read.Id, ProcessingPlan.Create(app, "book.xlsx", "general").Read.Id);
        var changed = app with { Ai = app.Ai with { EmbeddingModel = "another-embedding" } };
        var updated = ProcessingPlan.Create(changed, "book.xls", "general");
        Assert.Equal(plan.Read.Id, updated.Read.Id);
        Assert.NotEqual(plan.Embedding.Id, updated.Embedding.Id);
    }

    [Fact]
    public void RegionAndVisibilityChangesReuseConversionButCellLimitChangesDoNot()
    {
        var route = ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel);
        var app = Settings() with { Management = ManagementSettings.CreateDefault() with { Routes = [route] } };
        var original = ProcessingPlan.Create(app, "book.xls", "general");
        var changed = route with { Spreadsheet = route.Spreadsheet with {
            IncludeHidden = true, HeaderRows = 2, Mode = SpreadsheetMode.Records
        } };
        var updatedApp = app with { Management = app.Management! with { Routes = [changed] } };
        var updated = ProcessingPlan.Create(updatedApp, "book.xls", "general");
        Assert.Equal(original.Read.Id, updated.Read.Id);
        Assert.NotEqual(original.Children.Id, updated.Children.Id);
        var limited = app with { Management = app.Management! with { Routes = [route with {
            Spreadsheet = route.Spreadsheet with { MaxCells = 100 }
        }] } };
        Assert.NotEqual(original.Read.Id, ProcessingPlan.Create(limited, "book.xls", "general").Read.Id);
    }

    [Fact]
    public async Task CachedConvertedWorkbookUsesOriginalHashAndAvoidsExcel()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-xls-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "legacy.xls");
            // Deliberately not an Excel file: a cache hit must not try opening it in Excel.
            byte[] bytes = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(path, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var app = Settings(); var plan = ProcessingPlan.Create(app, path, "general");
            var book = new SpreadsheetDocument([new("元シート", [new(1, 1, "保存済みの内容")], [], [])], ["spreadsheet_legacy_converted"]);
            var previous = new TextDocumentState { SourceHash = hash, Spreadsheet = book, Processing = new() {
                Stages = new() { ["read"] = new(plan.Read, hash, "output") }
            } };
            var snapshot = await DocumentReader.ReadAsync(path, app.PipelineFor(path), root, default, previous, plan.Read);
            Assert.Same(book, snapshot.Spreadsheet);
            Assert.Equal(hash, snapshot.Hash);
            Assert.Equal(hash, await DocumentReader.HashAsync(path, app.PipelineFor(path), default));
            Assert.Contains("保存済みの内容", snapshot.Text);
            Assert.Contains("spreadsheet_legacy_converted", snapshot.Notices);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.False(Directory.Exists(Path.Combine(root, "excel-work")));

            // A missing/mismatched receipt or changed source must not reuse the cache.
            await Assert.ThrowsAsync<PipelineValidationException>(() => DocumentReader.ReadAsync(path, app.PipelineFor(path), root, default, previous));
            await Assert.ThrowsAsync<PipelineValidationException>(() => DocumentReader.ReadAsync(path, app.PipelineFor(path), root, default,
                previous with { SourceHash = "different-source" }, plan.Read));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SkipDoesNotRequireExcelOrValidWorkbook()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-xls-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "skip.xls"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var settings = new TextRagSettings { SourceMode = SourceCodeMode.Excel, Spreadsheet = new() { Mode = SpreadsheetMode.Skip } };
            var result = await DocumentReader.ReadAsync(path, settings, root, default);
            Assert.Empty(result.Spreadsheet!.Sheets);
            Assert.Contains("spreadsheet_no_regions", result.Notices);
            Assert.False(Directory.Exists(Path.Combine(root, "excel-work")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExistingSavedExtensionsArePreserved()
    {
        var management = ManagementSettings.CreateDefault() with { Routes = [
            ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Extensions = [".xlsx"] }
        ] };
        Assert.Empty(management.Validate());
        Assert.Equal(new[] { ".xlsx" }, Assert.Single(management.Routes).Extensions);
        Assert.NotNull(DocumentInputs.ExtractionNotice("book.xls"));
    }
}
