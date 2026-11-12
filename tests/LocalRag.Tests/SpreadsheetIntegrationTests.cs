using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class SpreadsheetIntegrationTests
{
    private static AppSettings Settings(string input, SpreadsheetMode mode = SpreadsheetMode.Records) => TextRagTests.Settings(input) with
    {
        Management = ManagementSettings.CreateDefault() with { Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Csv) with {
            Spreadsheet = new() { Mode = mode, HeaderRows = 1 } }], Retrieval = new() { ConfidentialityEnabled = false } }
    };
    private sealed class Ai : ITextRagAi
    {
        public List<string> Inputs { get; } = [];
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException("Spreadsheet defaults must not call LLM.");
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => Task.FromResult(240);
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        { Inputs.AddRange(inputs); return Task.FromResult(new VectorBatch(3, inputs.Select(_ => new float[] { 1, 0, 0 }).ToArray())); }
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsSearchRangeFetchAndConfidentiality(bool structural)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "records.csv");
        await File.WriteAllTextAsync(path, "ID,説明,状態\r\n001,\"起動時に停止\r\n要調査\",未解決\r\n002,画面表示,解決\r\n");
        var settings = Settings(input); settings = settings with { Management = settings.Management! with {
            Retrieval = settings.Management!.Retrieval with { HierarchySearchEnabled = structural } } };
        var ai = new Ai();
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var doc = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Equal(2, doc.SpreadsheetRegions!.Count);
        var sheet = Assert.Single(doc.Spreadsheet!.Sheets).Name;
        var hits = await service.SearchAsync("起動時に停止"); Assert.NotEmpty(hits);
        Assert.All(hits, h => { Assert.Equal(sheet, h.Sheet); Assert.NotNull(h.CellRange); });
        var fetched = await service.FetchRangeAsync(doc.DocumentId, sheet, "A2:B2");
        Assert.Contains("001", fetched!.Content); Assert.Contains("起動時に停止", fetched.Content); Assert.DoesNotContain("画面表示", fetched.Content);
        using var calls = new LocalRetrievalCalls(service);
        Assert.Equal("A2:B2", McpTests.Body(await calls.FetchRangeAsync(doc.DocumentId.ToString("D"), sheet, "A2:B2", default)).GetProperty("range").GetString());
        Assert.Equal("invalid_arguments", McpTests.Body(await calls.FetchRangeAsync(doc.DocumentId.ToString("D"), sheet, "A0", default)).GetProperty("error").GetString());
        Assert.Equal(1, (await service.ScanAsync()).Unchanged);
        var restricted = settings with { Management = settings.Management with { Retrieval = new() { Patterns = ["要調査"], LlmEnabled = false } } };
        using var closed = new TextRagService(repository, ai, restricted, Path.Combine(fixture.Root, "data"));
        var denied = await closed.FetchRangeAsync(doc.DocumentId, sheet, "A3:C3");
        Assert.Equal(DisclosureDecision.Withheld, denied!.Decision); Assert.Null(denied.Content); Assert.Null(denied.Sheet);
        Assert.All(await closed.SearchAsync("画面"), h => Assert.Null(h.Content));
        await File.AppendAllTextAsync(path, "003,追加,未解決\n");
        Assert.Null(await service.FetchRangeAsync(doc.DocumentId, sheet, "A2:B2"));
    }

    [ServerFact]
    public async Task NumericDescriptionsPreserveValuesAndReprocessOnlyWhatChanged()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "table.csv"), "地域,売上,費用\n東京,1234567,987654\n大阪,3456789,654321\n");
        var settings = Settings(input, SpreadsheetMode.NumericTable); var ai = new Ai();
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var service = new TextRagService(repository, ai, settings, Path.Combine(fixture.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.DoesNotContain(ai.Inputs, text => text.Contains("1234567"));
        var original = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Contains("1234567", original.Original);
        Assert.Equal("1234567", original.Spreadsheet!.Sheets[0].Cells.Single(c => c.Row == 2 && c.Column == 2).RawValue);
        var changed = settings with { Ai = settings.Ai with { EmbeddingModel = "changed" } };
        using (var service = new TextRagService(repository, new Ai(), changed, Path.Combine(fixture.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var current = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Equal(original.SpreadsheetRegions, current.SpreadsheetRegions);
        var skip = changed with { Management = changed.Management! with { Routes = [changed.Management!.Routes![0] with { Spreadsheet = new() { Mode = SpreadsheetMode.Skip } }] } };
        using var skipped = new TextRagService(repository, new Ai(), skip, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await skipped.ScanAsync()).Excluded); Assert.Null(await skipped.FetchAsync(original.DocumentId));
    }
}
