using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private async Task<TextDocumentState> PrepareSpreadsheetAsync(TextDocumentState state, TextRagSettings options,
        ProcessingPlan plan, CancellationToken token)
    {
        var book = state.Spreadsheet ?? throw new InvalidDataException("Spreadsheet source missing.");
        if (!Reusable(state, "spreadsheet-regions", plan.Children, book, state.SpreadsheetRegions))
        {
            state = state with { SpreadsheetRegions = SpreadsheetRegions.Build(book, options.Spreadsheet, token) };
            state = Stamp(state, "spreadsheet-regions", plan.Children, book, state.SpreadsheetRegions);
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        var budget = await IngestionAi.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        var parents = new List<TextParentChunk>();
        var chunks = new List<TextChunk>();
        var index = 0;
        foreach (var region in state.SpreadsheetRegions!)
        {
            token.ThrowIfCancellationRequested();
            index++;
            parents.Add(new(region.Id, index, index, region.Text, $"{region.Sheet}!{region.Range} ({region.Mode})"));
            // All fragments retain their original record/table identity. Numeric bodies are not embedded.
            foreach (var part in TextWindows.Split(region.SearchText, options.MaxChunkCharacters, budget))
                chunks.Add(new(index, index, part, region.Id, region.Mode == SpreadsheetMode.NumericTable ? "parent_summary" : "parent_text"));
        }
        state = state with { Parents = parents, Chunks = chunks, CodeUnits = null, ParentEmbeddings = [], ParentEmbeddingsCompleted = true };
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }

    private static string SpreadsheetSearchContent(TextDocumentState document, string? parentId, string content)
    {
        if (content.Length <= 8000 || document.SpreadsheetRegions?.FirstOrDefault(r => r.Id == parentId) is not { } region) return content;
        return region.SearchText[..Math.Min(region.SearchText.Length, 6000)] +
            $"\n\n大きな領域のため概要を返しました。シート: {region.Sheet} / 範囲: {region.Range}。fetchに文書ID・sheet・rangeを指定して範囲を取得してください。";
    }

    public Task<TextSearchResult?> FetchRangeAsync(Guid documentId, string sheet, string range, CancellationToken token = default) =>
        RunRetrievalAsync<TextSearchResult?>("fetch_range", async () =>
        {
            var document = (await repository.DocumentsAsync(token).ConfigureAwait(false)).SingleOrDefault(d => d.DocumentId == documentId);
            if (document?.Spreadsheet is null || !await CurrentAsync(document, token).ConfigureAwait(false)) return null;
            // Check the entire original workbook first, including cells omitted from indexing.
            var preflight = await DiscloseAsync(document, "", token).ConfigureAwait(false);
            if (preflight.Decision is not (DisclosureDecision.Allowed or DisclosureDecision.Unchecked))
                return new(document.DocumentId, preflight.Decision, null, document.Path, null, null);
            var content = SpreadsheetDocuments.RenderRange(document.Spreadsheet, sheet, range, 10000,
                settings.PipelineFor(document.Path).Spreadsheet.IncludeHidden);
            if (content.Length > 100000) throw new ArgumentException("取得範囲が大きすぎます。小さい範囲を指定してください。");
            var disclosure = await DiscloseAsync(document, content, token).ConfigureAwait(false);
            if (!await CurrentAsync(document, token).ConfigureAwait(false)) return null;
            var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
            return new(document.DocumentId, disclosure.Decision, allowed ? content : null, document.Path, null, null,
                Sheet: allowed ? sheet : null, CellRange: allowed ? range : null);
        }, token);
}
