using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class SpreadsheetRegionTests
{
    private static SpreadsheetDocument Book(params SpreadsheetCell[] cells) => new([new("Sheet1", cells.ToList(), [], [])], []);
    private static SpreadsheetCell C(int row, int column, string text, string kind = "text") => new(row, column, text, kind, text);

    [Fact]
    public void RecordsRetainColumnLabelsCoordinatesAndContext()
    {
        var book = Book(C(1,1,"製品課題"), C(2,1,"ID"), C(2,2,"内容"), C(2,3,"状態"),
            C(3,1,"123"), C(3,2,"起動時に停止"), C(3,3,"調査中"),
            C(4,1,"124"), C(4,2,"表示崩れ"), C(4,3,"修正済み"));
        var regions = SpreadsheetRegions.Build(book, new());
        var records = regions.Where(r => r.Mode == SpreadsheetMode.Records).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Contains("製品課題", records[0].SearchText);
        Assert.Contains("内容 (B3): 起動時に停止", records[0].SearchText);
        Assert.Contains("A2=ID", records[0].Text);
        Assert.Equal("A3:C3", records[0].Range);
        Assert.NotEqual(records[0].Id, records[1].Id);
        Assert.Equal(regions, SpreadsheetRegions.Build(book, new()));
    }

    [Fact]
    public void NumericTablesEmbedMetadataAndKeepEveryOriginalValue()
    {
        var book = Book(C(1,1,"地域別売上（百万円）"), C(2,1,"地域"), C(2,2,"4月"), C(2,3,"5月"),
            C(3,1,"関東"), C(3,2,"314159", "number"), new(3,3,"271828","number","271828","SUM(B3)"),
            C(4,1,"関西"), C(4,2,"123456", "number"), C(4,3,"654321", "number"), C(5,1,"注記: 速報値"));
        var table = Assert.Single(SpreadsheetRegions.Build(book, new()), r => r.Mode == SpreadsheetMode.NumericTable);
        Assert.Contains("地域別売上（百万円）", table.SearchText);
        Assert.Contains("4月", table.SearchText);
        Assert.Contains("関東", table.SearchText);
        Assert.Contains("速報値", table.SearchText);
        Assert.DoesNotContain("314159", table.SearchText);
        Assert.Contains("B3=314159", table.Text);
        Assert.Contains("SUM(B3)", table.Text);
        Assert.Contains("未再計算", table.Text);
    }

    [Fact]
    public void ExplicitTablesAndDisjointAreasDoNotMerge()
    {
        var sheet = new SpreadsheetSheet("Data", [C(1,1,"ID"),C(1,2,"内容"),C(2,1,"A"),C(2,2,"左"),
            C(1,5,"ID"),C(1,6,"内容"),C(2,5,"B"),C(2,6,"右"),C(7,1,"離れた文章")], [],
            [new("LeftTable","A1:B2"),new("RightTable","E1:F2")]);
        var regions = SpreadsheetRegions.Build(new([sheet],[]),new());
        Assert.Equal(3,regions.Count);
        Assert.Contains(regions,r=>r.Text.Contains("LeftTable") && r.Text.Contains("左") && !r.Text.Contains("右"));
        Assert.Contains(regions,r=>r.Text.Contains("RightTable") && r.Text.Contains("右") && !r.Text.Contains("左"));
        Assert.Contains(regions,r=>r.Mode==SpreadsheetMode.Document && r.Text.Contains("離れた文章"));
    }

    [Fact]
    public void BlankColumnsSplitSideBySideTablesWithoutExplicitTableMetadata()
    {
        var regions=SpreadsheetRegions.Build(Book(C(1,1,"ID"),C(1,2,"内容"),C(2,1,"A"),C(2,2,"左"),
            C(1,5,"ID"),C(1,6,"内容"),C(2,5,"B"),C(2,6,"右")),new());
        Assert.Equal(2,regions.Count);
        Assert.Equal(["A2:B2","E2:F2"],regions.Select(r=>r.Range));
    }

    [Fact]
    public void NoHeaderNumericFirstRowAndAmbiguousTextNeverDiscardData()
    {
        var regions=SpreadsheetRegions.Build(Book(C(1,1,"100"),C(1,2,"200"),C(2,1,"300"),C(2,2,"400")),new());
        var region=Assert.Single(regions);
        Assert.Equal(SpreadsheetMode.Raw,region.Mode);
        Assert.Contains("A1=100",region.Text);
        var ambiguous=Assert.Single(SpreadsheetRegions.Build(Book(C(1,1,"Alpha"),C(1,2,"Beta"),C(2,1,"Gamma"),C(2,2,"Delta")),new()));
        Assert.Equal(SpreadsheetMode.Raw,ambiguous.Mode);
        Assert.Contains("A1=Alpha",ambiguous.Text);
    }

    [Fact]
    public void ManualMultilineHeadersAndNoHeaderRowsAreRespected()
    {
        var book=Book(C(1,1,"大分類"),C(1,2,"製品"),C(2,1,"ID"),C(2,2,"名称"),C(3,1,"A"),C(3,2,"B"));
        var grouped=Assert.Single(SpreadsheetRegions.Build(book,new(){Mode=SpreadsheetMode.Records,HeaderRows=2}));
        Assert.Contains("大分類 / ID (A3): A",grouped.Text);
        Assert.Contains("製品 / 名称 (B3): B",grouped.Text);
        var all=SpreadsheetRegions.Build(book,new(){Mode=SpreadsheetMode.Records,HeaderRows=0});
        Assert.Equal(3,all.Count);
        Assert.Contains("A1",all[0].Text);
    }

    [Fact]
    public void RangeOverrideWinsOverWholeSheetAndDoesNotDiscardUnassignedCells()
    {
        var book=Book(C(1,1,"A"),C(1,2,"B"),C(2,1,"C"),C(2,2,"D"),C(3,1,"E"));
        var options=new SpreadsheetSettings{Overrides=[new(){Sheet="*",Mode=SpreadsheetMode.Raw},
            new(){Sheet="Sheet1",Range="A1:B2",Mode=SpreadsheetMode.Records,HeaderRows=0}]};
        var regions=SpreadsheetRegions.Build(book,options);
        Assert.Equal(2,regions.Count(r=>r.Mode==SpreadsheetMode.Records));
        Assert.Contains(regions,r=>r.Mode==SpreadsheetMode.Raw && r.Text.Contains("A3=E"));
        var skipped=SpreadsheetRegions.Build(book,options with {Overrides=[new(){Sheet="Sheet1",Range="A1:B2",Mode=SpreadsheetMode.Skip}]});
        Assert.Contains("A3=E",Assert.Single(skipped).Text);
    }

    [Fact]
    public void OverlappingManualRangesAreRejected()
    {
        var error=Assert.Throws<PipelineValidationException>(()=>SpreadsheetRegions.Build(Book(C(1,1,"A")),new(){Overrides=
            [new(){Sheet="Sheet1",Range="A1:B2"},new(){Sheet="Sheet1",Range="B2:C3"}]}));
        Assert.Equal("spreadsheet_settings",error.Code);
    }

    [Fact]
    public void FormsPreserveLabelValueAssociationAndFormulaCache()
    {
        var region=Assert.Single(SpreadsheetRegions.Build(Book(C(1,1,"部署："),C(1,2,"開発部"),C(2,1,"担当："),C(2,2,"山田")),new()));
        Assert.Equal(SpreadsheetMode.Form,region.Mode);
        Assert.Contains("A1=部署： : B1=開発部",region.Text);
    }

    [Fact]
    public void HiddenCellsAndTheirContextAreExcludedFromIndexButCanBeEnabled()
    {
        var book=Book(C(1,1,"hidden title") with {Hidden=true},C(2,1,"public text"));
        var shown=SpreadsheetRegions.Build(book,new());
        Assert.DoesNotContain(shown,r=>r.Text.Contains("hidden title"));
        Assert.Contains(SpreadsheetRegions.Build(book,new(){IncludeHidden=true}),r=>r.Text.Contains("hidden title"));
    }

    [Fact]
    public void SparseDistantCoordinatesDoNotAllocateFullGrid()
    {
        var regions=SpreadsheetRegions.Build(Book(C(1,1,"first"),C(1048576,16384,"last")),new());
        Assert.Equal(2,regions.Count);
        Assert.Contains(regions,r=>r.Range=="XFD1048576:XFD1048576");
    }

    [Fact]
    public void MergedMultirowHeadersAreExpandedOnlyAsRecordLabels()
    {
        var sheet=new SpreadsheetSheet("Data",[C(1,1,"製品"),C(2,1,"型番"),C(2,2,"説明"),C(3,1,"P01"),C(3,2,"起動試験")],
            ["A1:B1"],[]);
        var region=Assert.Single(SpreadsheetRegions.Build(new([sheet],[]),new(){Mode=SpreadsheetMode.Records,HeaderRows=2}));
        Assert.Contains("製品 / 型番 (A3): P01",region.Text);
        Assert.Contains("製品 / 説明 (B3): 起動試験",region.Text);
        Assert.DoesNotContain("B1=製品",region.Text); // Original cell evidence must not be fabricated.
    }

    [Fact]
    public void NonAsciiRecordsWithDatesKeepOriginalValues()
    {
        var book=Book(C(1,1,"日期"),C(1,2,"姓名"),C(1,3,"内容"),C(2,1,"2026-11-11","date"),C(2,2,"홍길동"),C(2,3,"測試資料"));
        var region=Assert.Single(SpreadsheetRegions.Build(book,new()));
        Assert.Equal(SpreadsheetMode.Records,region.Mode);
        Assert.Contains("日期 (A2): 2026-11-11",region.SearchText);
        Assert.Contains("홍길동",region.SearchText);
        Assert.Contains("測試資料",region.SearchText);
    }

    [Fact]
    public void SkippedRangesAreNotReintroducedThroughAdjacentContext()
    {
        var book=Book(C(1,1,"omit me"),C(2,1,"keep me"));
        var result=SpreadsheetRegions.Build(book,new(){Overrides=[new(){Sheet="Sheet1",Range="A1",Mode=SpreadsheetMode.Skip}]});
        Assert.DoesNotContain("omit me",Assert.Single(result).SearchText);
        var reverse=SpreadsheetRegions.Build(book,new(){Mode=SpreadsheetMode.Skip,Overrides=[new(){Sheet="Sheet1",Range="A2",Mode=SpreadsheetMode.Document}]});
        Assert.DoesNotContain("omit me",Assert.Single(reverse).SearchText);
    }

    [Fact]
    public void CancelledRegionAnalysisDoesNotStartWork()
    {
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>SpreadsheetRegions.Build(Book(C(1,1,"text")),new(),cancellation.Token));
    }
}
