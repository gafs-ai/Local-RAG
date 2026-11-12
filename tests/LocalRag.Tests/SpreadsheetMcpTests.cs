using System.IO.Compression;
using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;
using ModelContextProtocol.Client;

namespace LocalRag.Tests;

public class SpreadsheetMcpTests
{
    [Theory]
    [InlineData(DisclosureDecision.Allowed)]
    [InlineData(DisclosureDecision.Withheld)]
    [InlineData(DisclosureDecision.Pending)]
    public async Task HttpAndStdioForwardRangeAndSuppressClosedMetadata(DisclosureDecision decision)
    {
        var data = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var port = McpTests.FreePort();
        await new SettingsStore(data).SaveAsync(new() { Mcp = new() { Port = port } });
        var secret = await new McpTokenStore(data).GetOrCreateAsync();
        var stub = new RangeStub(decision);
        await using var host = await LocalMcpHost.StartAsync(stub, port, secret);
        await using var http = await McpTests.ConnectAsync(port, secret);
        await using var bridge = await McpClient.CreateAsync(new StdioClientTransport(new() {
            Command = "dotnet", Arguments = [Path.Combine(AppContext.BaseDirectory, "LocalRag.Mcp.dll"), "--data-dir", data] }));
        foreach (var client in new[] { http, bridge })
        {
            var result = McpTests.Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> {
                ["id"] = stub.Id.ToString("D"), ["sheet"] = "売上", ["range"] = "$B$2:C4" }));
            Assert.Equal(decision.ToString().ToLowerInvariant(), result.GetProperty("decision").GetString());
            if (decision == DisclosureDecision.Allowed)
            {
                Assert.Equal("売上", result.GetProperty("sheet").GetString());
                Assert.Equal("$B$2:C4", result.GetProperty("range").GetString());
                Assert.Equal("stored value", result.GetProperty("text").GetString());
            }
            else Assert.Equal(["decision", "id", "source"], result.EnumerateObject().Select(p => p.Name).Order().ToArray());
            var invalid = McpTests.Body(await client.CallToolAsync("fetch", new Dictionary<string, object?> {
                ["id"] = stub.Id.ToString("D"), ["sheet"] = "売上" }));
            Assert.Equal("invalid_arguments", invalid.GetProperty("error").GetString());
        }
        Assert.Equal(2, stub.RangeCalls);
        Assert.Equal(0, stub.FullCalls);
    }

    [ServerFact]
    public async Task HiddenWorksheetIsNeverDisclosedAndStillParticipatesInWorkbookConfidentiality()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "book.xlsx"); await File.WriteAllBytesAsync(path, Book("Public guide"));
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() with {
            Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Spreadsheet = new() { Mode = SpreadsheetMode.Records, HeaderRows = 1 } }],
            Retrieval = new() { ConfidentialityEnabled = false } } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, new EmbeddingOnlyAi(), settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repository.DocumentsAsync(default));
        Assert.DoesNotContain("SECRET", document.Original);
        Assert.Contains("SECRET", document.WordDisclosureText);
        Assert.DoesNotContain(document.SpreadsheetRegions!, r => r.Sheet == "PrivateData");
        Assert.DoesNotContain("SECRET", (await service.FetchAsync(document.DocumentId))!.Content);
        using var calls = new LocalRetrievalCalls(service);
        var forbidden = McpTests.Body(await calls.FetchRangeAsync(document.DocumentId.ToString("D"), "PrivateData", "A1", default));
        Assert.Equal("invalid_arguments", forbidden.GetProperty("error").GetString());

        var restricted = settings with { Management = settings.Management with { Retrieval = new() { Patterns = ["SECRET"], LlmEnabled = false } } };
        using var closed = new TextRagService(repository, new EmbeddingOnlyAi(), restricted, Path.Combine(fixture.Root, "data"));
        using var closedCalls = new LocalRetrievalCalls(closed);
        var result = McpTests.Body(await closedCalls.FetchRangeAsync(document.DocumentId.ToString("D"), "Visible", "A2:B2", default));
        Assert.Equal("withheld", result.GetProperty("decision").GetString());
        Assert.Equal(["decision", "id", "source"], result.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.All(await closed.SearchAsync("guide"), hit => { Assert.Null(hit.Content); Assert.Null(hit.Sheet); Assert.Null(hit.CellRange); });

        await File.WriteAllBytesAsync(path, Book("Changed guide"));
        Assert.Null(await service.FetchRangeAsync(document.DocumentId, "Visible", "A2:B2"));
        Assert.Null(await service.FetchAsync(document.DocumentId));
    }

    private sealed class RangeStub(DisclosureDecision decision) : ITextRetrieval
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int RangeCalls { get; private set; }
        public int FullCalls { get; private set; }
        public Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<TextSearchResult>>([]);
        public Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default)
        { FullCalls++; return Task.FromResult<TextSearchResult?>(null); }
        public Task<TextSearchResult?> FetchRangeAsync(Guid documentId, string sheet, string range, CancellationToken token = default)
        {
            Assert.Equal(Id, documentId); Assert.Equal("売上", sheet); Assert.Equal("$B$2:C4", range); RangeCalls++;
            // Even a buggy retrieval adapter must not cause metadata/content from a closed result to leak.
            return Task.FromResult<TextSearchResult?>(new(Id, decision, "stored value", @"C:\資料\book.xlsx", null, null, Sheet: sheet, CellRange: range));
        }
    }

    [ServerTheory]
    [InlineData("G1", DisclosureDecision.Withheld)]
    [InlineData("G6", DisclosureDecision.Allowed)]
    public async Task BoundaryPolicyUsesWholeOccupiedRowsIncludingRightmostHeaderCells(string secretAddress, DisclosureDecision expected)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "header.xlsx"); await File.WriteAllBytesAsync(path, BoundaryBook(secretAddress));
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() with {
            Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Spreadsheet = new() { Mode = SpreadsheetMode.Raw } }],
            Retrieval = new() { ConfidentialityEnabled = true, PatternsEnabled = false, LlmEnabled = false,
                BoundaryPatternsEnabled = true, BoundaryPatterns = ["SECRET"], LeadingLines = 1, TrailingLines = 0 } } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, new EmbeddingOnlyAi(), settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repository.DocumentsAsync(default));
        var result = await service.FetchRangeAsync(document.DocumentId, "Visible", "A2:B2");
        Assert.Equal(expected, result!.Decision);
        if (expected == DisclosureDecision.Withheld) { Assert.Null(result.Content); Assert.Null(result.Sheet); }
        else Assert.Contains("Public guide", result.Content);
    }

    private static byte[] BoundaryBook(string secretAddress)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var header = string.Concat(Enumerable.Range(0, 6).Select(i => $"<c r=\"{(char)('A' + i)}1\" t=\"inlineStr\"><is><t>Column {i}</t></is></c>"));
        var secret = $"<c r=\"{secretAddress}\" t=\"inlineStr\"><is><t>SECRET</t></is></c>";
        return Archive(new() {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Visible\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"r1\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\">{header}{(secretAddress == "G1" ? secret : "")}</row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Public guide</t></is></c></row>{(secretAddress == "G6" ? "<row r=\"6\">" + secret + "</row>" : "")}</sheetData></worksheet>"
        });
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSkipDoesNotParseOrClassifyAndCanLaterBeReenabled(bool validWorkbook)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "skip.xlsx");
        await File.WriteAllBytesAsync(path, validWorkbook ? Book("Public guide") : Encoding.UTF8.GetBytes("not an XLSX archive"));
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() with {
            Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { ClassifyEnabled = true, Spreadsheet = new() { Mode = SpreadsheetMode.Skip } }],
            Retrieval = new() { ConfidentialityEnabled = false } } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var skipped = new TextRagService(repository, new EmbeddingOnlyAi(), settings, Path.Combine(fixture.Root, "data")))
        {
            var cycle = await skipped.ScanAsync();
            Assert.Equal(0, cycle.Failed); Assert.Equal(1, cycle.Excluded);
        }
        if (!validWorkbook) return;
        var enabled = settings with { Management = settings.Management with {
            Routes = [ManagementSettings.CreateSpreadsheetRoute(SourceCodeMode.Excel) with { Spreadsheet = new() { Mode = SpreadsheetMode.Raw } }] } };
        using var service = new TextRagService(repository, new EmbeddingOnlyAi(), enabled, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repository.DocumentsAsync(default));
        Assert.NotNull(document.Spreadsheet);
        Assert.Contains("Public guide", (await service.FetchAsync(document.DocumentId))!.Content);
    }
    private sealed class EmbeddingOnlyAi : ITextRagAi
    {
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => throw new InvalidOperationException("Unexpected LLM call.");
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) =>
            Task.FromResult(new VectorBatch(3, inputs.Select(_ => new float[] { 1, 0, 0 }).ToArray()));
    }
    private static byte[] Book(string description)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var parts = new Dictionary<string, string> {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Visible\" sheetId=\"1\" r:id=\"r1\"/><sheet name=\"PrivateData\" state=\"hidden\" sheetId=\"2\" r:id=\"r2\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"r1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"r2\" Target=\"worksheets/sheet2.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Item</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Description</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Water</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>{description}</t></is></c></row></sheetData></worksheet>",
            ["xl/worksheets/sheet2.xml"] = $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>SECRET account</t></is></c></row></sheetData></worksheet>"
        };
        return Archive(parts);
    }
    private static byte[] Archive(Dictionary<string, string> parts)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, value) in parts) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); }
        return stream.ToArray();
    }
}
