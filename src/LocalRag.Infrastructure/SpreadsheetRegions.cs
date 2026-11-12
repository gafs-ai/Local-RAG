using System.Globalization;
using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Conservative, deterministic region routing. Cell values are evidence, never instructions.</summary>
public static class SpreadsheetRegions
{
    private sealed record Bounds(int StartRow, int EndRow, int StartColumn, int EndColumn)
    {
        public bool Contains(SpreadsheetCell c) => c.Row >= StartRow && c.Row <= EndRow && c.Column >= StartColumn && c.Column <= EndColumn;
        public bool Intersects(Bounds b) => StartRow <= b.EndRow && EndRow >= b.StartRow && StartColumn <= b.EndColumn && EndColumn >= b.StartColumn;
        public string Address => SpreadsheetDocuments.CellAddress(StartRow, StartColumn) + ":" + SpreadsheetDocuments.CellAddress(EndRow, EndColumn);
    }

    public static List<SpreadsheetRegion> Build(SpreadsheetDocument document, SpreadsheetSettings settings, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (settings.Validate().Count != 0) throw new PipelineValidationException("spreadsheet_settings", "Excel・CSV設定が不正です。");
        var output = new List<SpreadsheetRegion>();
        foreach (var originalSheet in document.Sheets)
        {
            token.ThrowIfCancellationRequested();
            if (originalSheet.Hidden && !settings.IncludeHidden) continue;
            var sheet = originalSheet with { Cells = originalSheet.Cells.Where(c => settings.IncludeHidden || !c.Hidden).ToList() };
            var all = sheet.Cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
            if (all.Count == 0) continue;
            var rows = all.ToLookup(c => c.Row);
            var rowNumbers = rows.Select(g => g.Key).Order().ToArray();
            var claimed = new HashSet<(int Row, int Column)>();
            List<SpreadsheetCell> Select(Bounds b)
            {
                var found = new List<SpreadsheetCell>();
                var start = Array.BinarySearch(rowNumbers, b.StartRow);
                if (start < 0) start = ~start;
                for (var row = start; row < rowNumbers.Length && rowNumbers[row] <= b.EndRow; row++)
                {
                    token.ThrowIfCancellationRequested();
                    found.AddRange(rows[rowNumbers[row]].Where(c => b.Contains(c) && !claimed.Contains((c.Row, c.Column))));
                }
                foreach (var cell in found) claimed.Add((cell.Row, cell.Column));
                return found;
            }
            var rules = settings.Overrides.Where(o => o.Sheet == sheet.Name || o.Sheet == "*")
                .OrderBy(o => o.Sheet == sheet.Name ? 0 : 1).ToArray();
            var whole = rules.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Range));
            var mode = whole?.Mode ?? settings.Mode;
            var headers = whole?.HeaderRows ?? settings.HeaderRows;
            var explicitRules = rules.Where(o => !string.IsNullOrWhiteSpace(o.Range)).Select(o => (Rule: o, Bounds: Parse(o.Range))).ToArray();
            if (explicitRules.Length > 256) throw new PipelineValidationException("spreadsheet_settings", "シートごとの手動指定範囲は256件までです。");
            for (var i = 0; i < explicitRules.Length; i++)
                for (var j = i + 1; j < explicitRules.Length; j++)
                    if (explicitRules[i].Bounds.Intersects(explicitRules[j].Bounds))
                        throw new PipelineValidationException("spreadsheet_settings", "Excel・CSVの手動指定範囲が重複しています。");
            var contextRows = mode != SpreadsheetMode.Skip && explicitRules.All(r => r.Rule.Mode != SpreadsheetMode.Skip) ? rows :
                all.Where(c => (explicitRules.FirstOrDefault(r => r.Bounds.Contains(c)).Rule?.Mode ?? mode) != SpreadsheetMode.Skip).ToLookup(c => c.Row);
            var mergedHeaders = sheet.MergedRanges.Select(Parse).ToLookup(b => (b.StartRow, b.StartColumn));
            foreach (var (rule, bounds) in explicitRules)
            {
                token.ThrowIfCancellationRequested();
                var cells = Select(bounds);
                Emit(sheet, contextRows, mergedHeaders, cells, bounds, rule.Mode, rule.HeaderRows, "手動範囲", output, token);
            }
            if (mode == SpreadsheetMode.Skip) continue;
            // Explicit Excel tables delimit regions before blank-row/column inference.
            foreach (var table in sheet.Tables)
            {
                token.ThrowIfCancellationRequested();
                var bounds = Parse(table.Range);
                var cells = Select(bounds);
                Emit(sheet, contextRows, mergedHeaders, cells, bounds, mode, headers >= 0 ? headers : table.HeaderRows, table.Name, output, token);
            }
            var remaining = all.Where(c => !claimed.Contains((c.Row, c.Column))).ToList();
            foreach (var cells in Split(remaining, mode == SpreadsheetMode.Automatic && headers < 0))
                Emit(sheet, contextRows, mergedHeaders, cells, Extent(cells), mode, headers, null, output, token);
        }
        return output;
    }

    private static Bounds Parse(string range)
    {
        var b = SpreadsheetDocuments.ParseRange(range);
        return new(b.StartRow, b.EndRow, b.StartColumn, b.EndColumn);
    }
    private static Bounds Extent(List<SpreadsheetCell> cells) => new(cells.Min(c => c.Row), cells.Max(c => c.Row), cells.Min(c => c.Column), cells.Max(c => c.Column));

    private static IEnumerable<List<SpreadsheetCell>> Split(List<SpreadsheetCell> cells, bool splitProse)
    {
        if (cells.Count == 0) yield break;
        // Only occupied coordinates are considered; a cell at XFD1048576 never allocates a huge grid.
        var rows = Runs(cells, c => c.Row);
        if (rows.Count > 1)
        {
            foreach (var row in rows) foreach (var region in Split(row, splitProse)) yield return region;
            yield break;
        }
        var columns = Runs(cells, c => c.Column);
        if (columns.Count > 1)
        {
            foreach (var column in columns) foreach (var region in Split(column, splitProse)) yield return region;
            yield break;
        }
        var grouped = cells.GroupBy(c => c.Row).OrderBy(g => g.Key).ToArray();
        // Separate prose/title/notes from a rectangular table. A blank first-column cell inside
        // a populated table does not by itself create a boundary.
        if (splitProse && grouped.Length > 1 && grouped.Any(g => g.Count() == 1) && grouped.Any(g => g.Count() >= 2))
        {
            var part = new List<SpreadsheetCell>();
            bool? single = null;
            foreach (var row in grouped)
            {
                var next = row.Count() == 1;
                if (single is not null && next != single) { yield return part; part = []; }
                part.AddRange(row); single = next;
            }
            if (part.Count > 0) yield return part;
            yield break;
        }
        yield return cells;
    }

    private static List<List<SpreadsheetCell>> Runs(List<SpreadsheetCell> cells, Func<SpreadsheetCell, int> coordinate)
    {
        var groups = cells.GroupBy(coordinate).OrderBy(g => g.Key);
        var output = new List<List<SpreadsheetCell>>();
        int? previous = null;
        foreach (var group in groups)
        {
            if (previous is null || group.Key > previous + 1) output.Add([]);
            output[^1].AddRange(group); previous = group.Key;
        }
        return output;
    }

    private static void Emit(SpreadsheetSheet sheet, ILookup<int, SpreadsheetCell> sheetRows, ILookup<(int Row, int Column), Bounds> mergedHeaders, List<SpreadsheetCell> cells, Bounds bounds,
        SpreadsheetMode requested, int headerRows, string? tableName, List<SpreadsheetRegion> output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (cells.Count == 0 || requested == SpreadsheetMode.Skip) return;
        cells = cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
        if (headerRows < 0) headerRows = DetectHeaders(cells, bounds);
        var header = cells.Where(c => c.Row < bounds.StartRow + headerRows).ToArray();
        var body = cells.Where(c => c.Row >= bounds.StartRow + headerRows).ToArray();
        // Never discard a header-only table, including an excessive manual header count.
        var mode = body.Length == 0 ? SpreadsheetMode.Raw : requested == SpreadsheetMode.Automatic ? Detect(cells, body, bounds, headerRows) : requested;
        var context = Context(sheetRows, bounds, tableName);
        var populatedColumns = cells.Select(c => c.Column).Distinct().ToArray();
        var expandedHeader = header.ToList();
        var headerLocations = header.ToDictionary(c => (c.Row, c.Column));
        foreach (var anchor in header)
        foreach (var span in mergedHeaders[(anchor.Row, anchor.Column)])
        {
            foreach (var column in populatedColumns.Where(c => c >= span.StartColumn && c <= span.EndColumn))
                if (!headerLocations.ContainsKey((anchor.Row, column))) expandedHeader.Add(anchor with { Column = column });
        }
        var headerByColumn = expandedHeader.ToLookup(h => h.Column);
        var labels = populatedColumns.ToDictionary(c => c, c =>
        {
            var names = headerByColumn[c].OrderBy(h => h.Row).Select(h => h.Text).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            return names.Length > 0 ? string.Join(" / ", names) : "列" + SpreadsheetDocuments.CellAddress(1, c).TrimEnd('1');
        });
        string Prefix(Bounds b) => $"# シート: {sheet.Name}\n範囲: {b.Address}\n" + context;
        if (mode == SpreadsheetMode.Records)
        {
            foreach (var row in body.GroupBy(c => c.Row).OrderBy(g => g.Key))
            {
                token.ThrowIfCancellationRequested();
                var b = new Bounds(row.Key, row.Key, bounds.StartColumn, bounds.EndColumn);
                var text = Prefix(b) + (header.Length > 0 ? "列定義: " + string.Join("; ", header.Select(Cell)) + "\n" : "ヘッダーなし\n") +
                    string.Join("\n", row.Select(c => $"{labels[c.Column]} ({SpreadsheetDocuments.CellAddress(c.Row, c.Column)}): {Value(c)}"));
                Add(sheet, b, mode, text, text, output);
            }
            return;
        }
        string content;
        if (mode == SpreadsheetMode.Document)
        {
            content = string.Join("\n\n", cells.GroupBy(c => c.Row).Select(row =>
                (row.Count() == 1 && row.First().Bold && row.First().Text.Length <= 100 ? "## " : "") + string.Join("  ", row.Select(Cell))));
        }
        else if (mode == SpreadsheetMode.Form)
        {
            content = string.Join("\n", cells.GroupBy(c => c.Row).Select(row => string.Join(" : ", row.Select(Cell))));
        }
        else content = string.Join("\n", cells.GroupBy(c => c.Row).Select(row => string.Join(" | ", row.Select(Cell))));
        var full = Prefix(bounds) + $"領域種別: {mode}\n" + content;
        var search = full;
        if (mode == SpreadsheetMode.NumericTable)
        {
            var axes = body.Where(c => c.Column == bounds.StartColumn && !Numeric(c)).Select(c => c.Text).Distinct().Take(24);
            search = Prefix(bounds) + "数値表。計算・集計は未実施。\n" +
                "列見出し: " + (header.Length == 0 ? "指定なし" : string.Join("; ", labels.Select(p => $"{SpreadsheetDocuments.CellAddress(bounds.StartRow, p.Key)}: {p.Value}"))) + "\n" +
                "行見出し（最大24件）: " + string.Join("; ", axes) + "\n" +
                $"データ行数: {body.Select(c => c.Row).Distinct().Count().ToString(CultureInfo.InvariantCulture)}\n" +
                (cells.Any(c => c.Formula is not null) ? "数式の値は保存済みキャッシュ。再計算していません。\n" : "");
        }
        Add(sheet, bounds, mode, full, search, output);
    }

    private static void Add(SpreadsheetSheet sheet, Bounds bounds, SpreadsheetMode mode, string text, string search, List<SpreadsheetRegion> output) =>
        output.Add(new(TextRagSettings.Hash(sheet.Name + "\n" + bounds.Address + "\n" + mode), sheet.Name, bounds.Address, mode,
            text, search, bounds.StartRow, bounds.EndRow, bounds.StartColumn, bounds.EndColumn));

    private static int DetectHeaders(List<SpreadsheetCell> cells, Bounds b)
    {
        var first = cells.Where(c => c.Row == b.StartRow).ToArray();
        var later = cells.Where(c => c.Row > b.StartRow).ToArray();
        if (first.Length < 2 || later.Length == 0 || first.Any(c => Numeric(c) || c.Text.Length > 80)) return 0;
        if (first.All(c => c.Bold) || first.Any(c => KnownHeader(c.Text)) || later.Any(c => Numeric(c) || c.Text.Length > 80)) return 1;
        return 0;
    }
    private static bool KnownHeader(string value) => value.Trim().ToLowerInvariant() is
        "id" or "name" or "description" or "status" or "date" or "value" or "question" or "answer" or
        "項目" or "名称" or "説明" or "内容" or "状態" or "日付" or "氏名" or "品名" or "質問" or "回答" or
        "姓名" or "日期" or "이름" or "날짜";

    private static SpreadsheetMode Detect(List<SpreadsheetCell> cells, SpreadsheetCell[] body, Bounds b, int headerRows)
    {
        var width = cells.Select(c => c.Column).Distinct().Count();
        if (width == 1) return cells.All(c => !Numeric(c)) ? SpreadsheetMode.Document : SpreadsheetMode.Raw;
        var dataRows = body.GroupBy(c => c.Row).ToArray();
        var values = body.Where(c => c.Column > b.StartColumn).ToArray();
        var stable = dataRows.Count(r => r.Count() >= width * .7) >= dataRows.Length * .8;
        if (headerRows > 0 && dataRows.Length >= 2 && stable && values.Length > 0 && values.Count(Numeric) >= values.Length * .8)
            return SpreadsheetMode.NumericTable;
        if (headerRows > 0 && stable) return SpreadsheetMode.Records;
        if (width == 2 && body.Where(c => c.Column == b.StartColumn).All(c => !Numeric(c) && c.Text.Length <= 40) &&
            body.Any(c => c.Column == b.StartColumn && (c.Text.EndsWith(':') || c.Text.EndsWith('：')))) return SpreadsheetMode.Form;
        if (cells.Count(c => c.Text.Length > 100) >= cells.Count * .6 && cells.All(c => !Numeric(c))) return SpreadsheetMode.Document;
        return SpreadsheetMode.Raw;
    }
    private static bool Numeric(SpreadsheetCell c) => c.Kind == "number" || c.Kind == "date" ||
        c.Kind == "text" && double.TryParse(c.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
    private static string Cell(SpreadsheetCell c) => $"{SpreadsheetDocuments.CellAddress(c.Row, c.Column)}={Value(c)}";
    private static string Value(SpreadsheetCell c) => c.Text + (c.Formula is null ? "" : $" [数式: {c.Formula}; 保存値: {c.RawValue ?? "なし"}; 未再計算]");
    private static string Context(ILookup<int, SpreadsheetCell> rows, Bounds b, string? name)
    {
        var above = rows[b.StartRow - 2].Concat(rows[b.StartRow - 1]).Where(c => c.Column >= b.StartColumn && c.Column <= b.EndColumn)
            .GroupBy(c => c.Row).Where(g => g.Count() == 1).SelectMany(g => g).ToArray();
        var below = rows[b.EndRow + 1].Where(c => c.Column >= b.StartColumn && c.Column <= b.EndColumn).ToArray();
        return (string.IsNullOrWhiteSpace(name) ? "" : $"表: {name}\n") +
            (above.Length == 0 ? "" : "直前の記載: " + string.Join("; ", above.Select(Cell)) + "\n") +
            (below.Length != 1 ? "" : "直後の記載: " + Cell(below[0]) + "\n");
    }
}
