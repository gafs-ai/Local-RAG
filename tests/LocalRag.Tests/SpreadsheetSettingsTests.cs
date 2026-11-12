using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Tests;

public class SpreadsheetSettingsTests
{
    [Fact]
    public void DefaultsIncludeDistinctSpreadsheetRoutesBeforeMarkdown()
    {
        var settings = ManagementSettings.CreateDefault();
        Assert.Empty(settings.Validate());
        var routes = settings.OrderedRoutes().ToList();
        Assert.Equal(SourceCodeMode.Excel, settings.RouteFor("Book.XLSX").SourceMode);
        Assert.Equal(SourceCodeMode.Excel, settings.RouteFor("Book.XLS").SourceMode);
        Assert.Equal(SourceCodeMode.Excel, settings.RouteFor("Book.XLSM").SourceMode);
        Assert.Equal(SourceCodeMode.Excel, settings.RouteFor("Book.XLSB").SourceMode);
        Assert.Equal(SourceCodeMode.Csv, settings.RouteFor("data.csv").SourceMode);
        Assert.False(settings.Resolve(new(), "data.csv").ClassifyEnabled);
        Assert.True(routes.FindIndex(r => r.SourceMode == SourceCodeMode.Excel) < routes.FindIndex(r => r.Name == "Markdown用"));
        Assert.True(routes.FindIndex(r => r.SourceMode == SourceCodeMode.Csv) < routes.FindIndex(r => r.Name == "Markdown用"));
        Assert.DoesNotContain(".csv", routes.Last().Extensions!);
    }

    [Fact]
    public void SpreadsheetOptionsRoundTripAndResolveWithoutChangingTextSettings()
    {
        var spreadsheet = new SpreadsheetSettings { Mode = SpreadsheetMode.Records, HeaderRows = 2, CsvDelimiter = ";", IncludeHidden = true,
            Overrides = [new() { Sheet = "明細", Range = "A5:F25", Mode = SpreadsheetMode.NumericTable, HeaderRows = 1 }] };
        var settings = ManagementSettings.CreateDefault() with { Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Csv) with { Spreadsheet = spreadsheet }] };
        var restored = JsonSerializer.Deserialize<ManagementSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Empty(restored.Validate());
        Assert.Equal(JsonSerializer.Serialize(spreadsheet), JsonSerializer.Serialize(restored.Resolve(new(), "data.csv").Spreadsheet));
        var initial = new TextRagSettings { SourceMode = SourceCodeMode.Csv };
        Assert.NotEqual(initial.ConfigurationId(new()), (initial with { Spreadsheet = spreadsheet }).ConfigurationId(new()));
        var text = initial with { SourceMode = SourceCodeMode.Text };
        Assert.Equal(text.ConfigurationId(new()), (text with { Spreadsheet = spreadsheet }).ConfigurationId(new()));
    }

    [Theory]
    [InlineData(SourceCodeMode.Excel)]
    [InlineData(SourceCodeMode.Csv)]
    public void SpreadsheetRoutesHideTextAndCodePromptOptions(SourceCodeMode mode)
    {
        var route = ManagementSettings.CreateSpreadsheetRoute(mode) with { ChildMode = ChildChunkMode.Llm,
            ParentMode = ParentChunkMode.Llm, ParentEmbedding = ParentEmbeddingMode.TextOrSummary };
        Assert.Equal(new(false, false, false, false, false, false, false, false, false, false), TextRouteUsage.For(route, new()));
    }

    [Fact]
    public void ValidationRejectsInvalidParametersAndWrongExtensions()
    {
        Assert.NotEmpty(new SpreadsheetSettings { HeaderRows = -2 }.Validate());
        Assert.NotEmpty(new SpreadsheetSettings { CsvDelimiter = "invalid" }.Validate());
        Assert.NotEmpty(new SpreadsheetSettings { MaxCells = 0 }.Validate());
        var settings = ManagementSettings.CreateDefault() with { Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Extensions = [".xls"] }] };
        Assert.Empty(settings.Validate());
        Assert.NotEmpty((settings with { Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Extensions = [".exe"] }] }).Validate());
        Assert.NotEmpty((settings with { Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Csv) with { Extensions = [".xlsx"] }] }).Validate());
    }
}
