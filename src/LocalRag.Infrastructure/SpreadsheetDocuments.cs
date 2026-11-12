using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Read-only spreadsheet extraction. Never evaluates formulas, macros or external links.</summary>
public static class SpreadsheetDocuments
{
    public const int MaxPackageBytes = 64 * 1024 * 1024;
    private const int MaxXmlBytes = 32 * 1024 * 1024;
    private static readonly Regex Address = new(@"^\$?([A-Za-z]{1,3})\$?([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static async Task<SpreadsheetDocument> ReadAsync(string path, TextRagSettings options, CancellationToken token = default)
        => Convert(await ReadBytesAsync(path, token).ConfigureAwait(false), path, options, token);

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken token = default)
    {
        TextFiles.RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxPackageBytes) throw Failure("spreadsheet_size", "Excel・CSVファイルの上限を超えています。");
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > MaxPackageBytes) throw Failure("spreadsheet_size", "Excel・CSVファイルの上限を超えています。");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    public static SpreadsheetDocument Convert(byte[] bytes, string path, TextRagSettings options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaxPackageBytes) throw Failure("spreadsheet_size", "Excel・CSVファイルの上限を超えています。");
        if (options.Spreadsheet.Validate().Count != 0) throw Failure("spreadsheet_settings", "Excel・CSV設定が不正です。");
        try
        {
            var result = options.SourceMode == SourceCodeMode.Excel || DocumentInputs.IsExcel(path)
                ? ReadXlsx(bytes, options.Spreadsheet, token)
                : ReadCsv(TextDecoding.Decode(bytes, options.EncodingMode).Text, options.Spreadsheet, token);
            if (Render(result).Length > options.MaxDocumentCharacters)
                throw Failure("document_size", "セル位置・数式を含むExcel・CSVの抽出文字数が上限を超えています。");
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException)
        { throw Failure("spreadsheet_invalid", "Excel・CSVの構造を読み取れません。暗号化・破損やファイル形式を確認してください。"); }
    }

    private static SpreadsheetDocument ReadXlsx(byte[] bytes, SpreadsheetSettings settings, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
            throw Failure("spreadsheet_size", "展開後のExcelファイルが上限を超えています。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            if (!entries.TryAdd(entry.FullName, entry) || entry.FullName.Contains('\\') || entry.FullName.Split('/').Contains(".."))
                throw new InvalidDataException("Invalid package entry.");
        XDocument? Xml(string name)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(name, out var entry)) return null;
            if (entry.Length > MaxXmlBytes) throw Failure("spreadsheet_size", "Excel XMLが上限を超えています。");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes, MaxCharactersFromEntities = 0 });
            return XDocument.Load(reader);
        }
        var notices = new HashSet<string>(StringComparer.Ordinal) { "spreadsheet_extracted" };
        if (entries.Keys.Any(e => e.Contains("/drawings/", StringComparison.Ordinal) || e.Contains("/charts/", StringComparison.Ordinal)))
            notices.Add("spreadsheet_visuals_omitted");
        if (entries.Keys.Any(e => e.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))) notices.Add("spreadsheet_macros_ignored");
        if (entries.Keys.Any(e => e.Contains("/externalLinks/", StringComparison.Ordinal) || e.EndsWith("connections.xml", StringComparison.Ordinal))) notices.Add("spreadsheet_external_links_not_refreshed");
        Dictionary<string, string> Relationships(string part)
        {
            var slash = part.LastIndexOf('/');
            var directory = slash < 0 ? "" : part[..(slash + 1)];
            var name = slash < 0 ? part : part[(slash + 1)..];
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rel in Xml(directory + "_rels/" + name + ".rels")?.Root?.Elements() ?? [])
            {
                if (rel.Name.LocalName != "Relationship") continue;
                if (string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                { notices.Add("spreadsheet_external_links_not_refreshed"); continue; }
                var target = (string?)rel.Attribute("Target") ?? throw new InvalidDataException("Missing relationship target.");
                if (target.Contains(':') || target.Contains('\\') || target.Contains('#') || target.Contains('?')) throw new InvalidDataException("Unsafe relationship.");
                var resolved = new List<string>();
                foreach (var component in (target.StartsWith('/') ? target[1..] : directory + target).Split('/'))
                {
                    if (component is "" or ".") continue;
                    if (component == "..") { if (resolved.Count == 0) throw new InvalidDataException("Escaping relationship."); resolved.RemoveAt(resolved.Count - 1); }
                    else resolved.Add(component);
                }
                if (!result.TryAdd((string?)rel.Attribute("Id") ?? "", string.Join('/', resolved))) throw new InvalidDataException("Duplicate relationship.");
            }
            return result;
        }
        var workbook = Xml("xl/workbook.xml") ?? throw new InvalidDataException("Missing workbook.");
        var ns = workbook.Root!.Name.Namespace;
        if (ns.NamespaceName is not ("http://schemas.openxmlformats.org/spreadsheetml/2006/main" or "http://purl.oclc.org/ooxml/spreadsheetml/main"))
            throw new InvalidDataException("Unsupported workbook namespace.");
        var relationships = Relationships("xl/workbook.xml");
        string CellText(XElement element) => string.Concat(element.Descendants(ns + "t")
            .Where(t => !t.Ancestors(ns + "rPh").Any()).Select(t => t.Value));
        var shared = Xml("xl/sharedStrings.xml")?.Root?.Elements(ns + "si").Select(CellText).ToArray() ?? [];
        var styles = Xml("xl/styles.xml");
        var formats = styles?.Root?.Element(ns + "numFmts")?.Elements(ns + "numFmt")
            .ToDictionary(e => Int(e, "numFmtId"), e => (string?)e.Attribute("formatCode") ?? "") ?? [];
        var fonts = styles?.Root?.Element(ns + "fonts")?.Elements(ns + "font").Select(e => e.Element(ns + "b") is { } b && (string?)b.Attribute("val") is not ("0" or "false")).ToArray() ?? [];
        var xfs = styles?.Root?.Element(ns + "cellXfs")?.Elements(ns + "xf").ToArray() ?? [];
        var date1904 = workbook.Root.Element(ns + "workbookPr") is { } properties && IsTrue((string?)properties.Attribute("date1904"));
        var sheets = new List<SpreadsheetSheet>();
        var cellCount = 0;
        foreach (var sheet in workbook.Root.Element(ns + "sheets")?.Elements(ns + "sheet") ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (sheets.Count >= 1024) throw Failure("spreadsheet_size", "シート数が上限を超えています。");
            var sheetName = (string?)sheet.Attribute("name") ?? throw new InvalidDataException("Missing sheet name.");
            if (sheets.Any(s => s.Name == sheetName)) throw new InvalidDataException("Duplicate sheet name.");
            var id = sheet.Attributes().SingleOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
            if (!relationships.TryGetValue(id, out var part)) throw new InvalidDataException("Missing sheet relationship.");
            var data = Xml(part) ?? throw new InvalidDataException("Missing sheet.");
            if (data.Root?.Name.LocalName != "worksheet") { notices.Add("spreadsheet_nonworksheet_omitted"); continue; }
            if (data.Root.Name.Namespace != ns) throw new InvalidDataException("Unexpected worksheet namespace.");
            var hidden = (string?)sheet.Attribute("state") is "hidden" or "veryHidden";
            var hiddenColumns = data.Root?.Element(ns + "cols")?.Elements(ns + "col").Where(e => IsTrue((string?)e.Attribute("hidden"))).Select(e => (Min: Int(e, "min"), Max: Int(e, "max"))).ToArray() ?? [];
            var cells = new List<SpreadsheetCell>();
            var seen = new HashSet<(int, int)>();
            var rowNumber = 0;
            foreach (var row in data.Root?.Element(ns + "sheetData")?.Elements(ns + "row") ?? [])
            {
                token.ThrowIfCancellationRequested();
                rowNumber = row.Attribute("r") is not null ? Int(row, "r") : row.Elements(ns + "c").FirstOrDefault()?.Attribute("r") is { } firstAddress
                    ? ParseCell(firstAddress.Value).Row : rowNumber + 1;
                var column = 0;
                foreach (var c in row.Elements(ns + "c"))
                {
                    if (++cellCount > settings.MaxCells) throw Failure("spreadsheet_cells", "Excel・CSVのセル上限を超えています。");
                    var position = c.Attribute("r") is { } a ? ParseCell(a.Value) : (Row: rowNumber, Column: column + 1);
                    column = position.Column;
                    if (position.Row != rowNumber || !seen.Add(position)) throw new InvalidDataException("Invalid or duplicate cell coordinate.");
                    if (position.Row is < 1 or > 1048576 || position.Column is < 1 or > 16384) throw new InvalidDataException("Cell coordinate outside Excel bounds.");
                    var value = c.Element(ns + "v")?.Value;
                    var formula = c.Element(ns + "f");
                    var type = (string?)c.Attribute("t") ?? "n";
                    var text = value ?? "";
                    var kind = "text";
                    var styleIndex = Int(c, "s");
                    if (styleIndex < 0 || styleIndex >= Math.Max(1, xfs.Length)) throw new InvalidDataException("Invalid cell style.");
                    var style = styleIndex < xfs.Length ? xfs[styleIndex] : null;
                    var formatId = style is null ? 0 : Int(style, "numFmtId");
                    var format = formats.GetValueOrDefault(formatId) ?? BuiltinFormat(formatId);
                    var fontId = style is null ? 0 : Int(style, "fontId");
                    var bold = fontId >= 0 && fontId < fonts.Length && fonts[fontId];
                    switch (type)
                    {
                        case "s":
                            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= shared.Length) throw new InvalidDataException("Invalid shared string.");
                            text = shared[index]; break;
                        case "inlineStr": text = c.Element(ns + "is") is { } inline ? CellText(inline) : ""; break;
                        case "b": if (value is not ("0" or "1")) throw new InvalidDataException("Invalid Boolean."); kind = "boolean"; text = value == "1" ? "TRUE" : "FALSE"; break;
                        case "e": kind = "error"; notices.Add("spreadsheet_cell_errors"); break;
                        case "d": kind = "date"; break;
                        case "str": break;
                        case "n":
                            kind = "number";
                            if (value is not null && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))) throw new InvalidDataException("Invalid number.");
                            if (value is not null && IsDateFormat(formatId, format) && double.TryParse(value, CultureInfo.InvariantCulture, out var serial))
                            {
                                kind = "date";
                                if (!date1904 && serial >= 60 && serial < 61) { text = "1900-02-29 (Excel serial " + value + ")"; notices.Add("spreadsheet_excel_leap_day"); }
                                else
                                {
                                    try { text = (date1904 ? new DateTime(1904, 1, 1) : serial < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30)).AddDays(serial).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'); }
                                    catch (ArgumentOutOfRangeException) { notices.Add("spreadsheet_date_unconverted"); }
                                }
                            }
                            break;
                        default: throw new InvalidDataException("Unknown cell type.");
                    }
                    string? formulaText = null;
                    if (formula is not null)
                    {
                        notices.Add("spreadsheet_formula_cached");
                        formulaText = formula.Value;
                        if (string.IsNullOrEmpty(formulaText)) formulaText = "[shared/array formula; si=" + (string?)formula.Attribute("si") + "; ref=" + (string?)formula.Attribute("ref") + "]";
                        if (value is null) notices.Add("spreadsheet_formula_cache_missing");
                    }
                    var cellHidden = IsTrue((string?)row.Attribute("hidden")) || hiddenColumns.Any(h => column >= h.Min && column <= h.Max);
                    if (hidden || cellHidden) notices.Add("spreadsheet_hidden_cells");
                    if (text.Length > 0 || formula is not null)
                        cells.Add(new(position.Row, position.Column, text, kind, value, formulaText, format, bold, cellHidden));
                }
            }
            var merges = data.Root?.Element(ns + "mergeCells")?.Elements(ns + "mergeCell").Select(e => (string?)e.Attribute("ref") ?? "").ToList() ?? [];
            foreach (var merge in merges) _ = ParseRange(merge);
            var tables = new List<SpreadsheetTable>();
            var sheetRelationships = Relationships(part);
            foreach (var tablePart in data.Root?.Element(ns + "tableParts")?.Elements(ns + "tablePart") ?? [])
            {
                var tableId = tablePart.Attributes().SingleOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
                if (!sheetRelationships.TryGetValue(tableId, out var tablePath)) throw new InvalidDataException("Missing table relationship.");
                var table = Xml(tablePath)?.Root ?? throw new InvalidDataException("Missing table definition.");
                var range = (string?)table.Attribute("ref") ?? ""; _ = ParseRange(range);
                tables.Add(new((string?)table.Attribute("displayName") ?? (string?)table.Attribute("name") ?? "Table", range, table.Attribute("headerRowCount") is null ? 1 : Int(table, "headerRowCount")));
            }
            sheets.Add(new(sheetName, cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList(), merges, tables, hidden));
        }
        return new(sheets, notices.Order(StringComparer.Ordinal).ToList());
    }

    private static int Int(XElement e, string name) => e.Attribute(name) is { } a ? int.Parse(a.Value, CultureInfo.InvariantCulture) : 0;
    private static bool IsTrue(string? value) => value is "1" or "true";
    private static string? BuiltinFormat(int id) => id switch { 14 => "mm-dd-yy", 15 => "d-mmm-yy", 16 => "d-mmm", 17 => "mmm-yy", 18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM", 20 => "h:mm", 21 => "h:mm:ss", 22 => "m/d/yy h:mm", 45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 9 => "0%", 10 => "0.00%", _ => null };
    private static bool IsDateFormat(int id, string? format)
    {
        // Elapsed durations and time-only values stay numeric with their format, not invented calendar dates.
        if (id is >= 18 and <= 21 or >= 45 and <= 47) return false;
        var unquoted = Regex.Replace(format ?? "", "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return id is >= 14 and <= 17 or 22 || Regex.IsMatch(unquoted, @"(?i)[yd]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static SpreadsheetDocument ReadCsv(string text, SpreadsheetSettings settings, CancellationToken token = default)
    {
        var notices = new List<string> { "spreadsheet_csv_extracted" };
        var delimiter = settings.CsvDelimiter;
        if (delimiter == "auto")
        {
            var candidates = new List<(char Delimiter, int Score)>();
            foreach (var candidate in new[] { ',', ';', '\t', '|' })
            {
                try
                {
                    var sample = ParseCsv(text, candidate, token, 20).Where(r => r.Any(v => v.Length > 0)).ToList();
                    var group = sample.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
                    candidates.Add((candidate, group is { Key: > 1 } ? group.Count() * 1000 + Math.Min(group.Key, 999) - (sample.Count - group.Count()) * 1000 : 0));
                }
                catch (PipelineValidationException) { }
            }
            var ranked = candidates.OrderByDescending(c => c.Score).ToList();
            if (ranked.Count == 0) throw Failure("csv_invalid", "CSVの引用符または区切りが不正です。");
            delimiter = ranked[0].Delimiter.ToString();
            if (ranked[0].Score == 0 || ranked.Count > 1 && ranked[0].Score == ranked[1].Score) notices.Add("spreadsheet_csv_delimiter_ambiguous");
        }
        if (delimiter.Length != 1 || delimiter is not ("," or ";" or "\t" or "|")) throw Failure("spreadsheet_settings", "CSV区切り文字が不正です。");
        var cells = new List<SpreadsheetCell>(); var row = 0; var count = 0; int? columns = null;
        foreach (var values in ParseCsv(text, delimiter[0], token))
        {
            row++;
            if (row > 1048576 || values.Count > 16384 || (long)count + values.Count > settings.MaxCells) throw Failure("spreadsheet_cells", "CSVのセル数または行列数が上限を超えています。");
            count += values.Count;
            if (columns is null) columns = values.Count; else if (columns != values.Count && !notices.Contains("spreadsheet_csv_ragged")) notices.Add("spreadsheet_csv_ragged");
            for (var column = 0; column < values.Count; column++)
            {
                var value = values[column]; if (value.Length == 0) continue;
                // Keep leading-zero identifiers textual. CSV has no authoritative number/date type.
                var numeric = Regex.IsMatch(value, @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                cells.Add(new(row, column + 1, value, numeric ? "number" : "text", value));
            }
        }
        return new([new("CSV", cells, [], [])], notices);
    }

    private static IEnumerable<List<string>> ParseCsv(string text, char delimiter, CancellationToken token, int limit = int.MaxValue)
    {
        var values = new List<string>(); var field = new StringBuilder(); var quoted = false; var closed = false; var started = false; var records = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; closed = true; } }
                else field.Append(c);
                continue;
            }
            if (c == delimiter) { values.Add(field.ToString()); field.Clear(); closed = false; started = false; continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                values.Add(field.ToString()); yield return values;
                if (++records >= limit) yield break;
                values = []; field.Clear(); closed = false; started = false; continue;
            }
            if (closed) throw Failure("csv_invalid", "CSVの閉じ引用符の後に区切り以外の文字があります。");
            if (c == '"') { if (started) throw Failure("csv_invalid", "CSVフィールド途中の引用符が不正です。"); quoted = true; started = true; }
            else { field.Append(c); started = true; }
        }
        if (quoted) throw Failure("csv_invalid", "CSVの引用符が閉じていません。");
        if (started || closed || values.Count > 0) { values.Add(field.ToString()); yield return values; }
    }

    public static (int StartRow, int EndRow, int StartColumn, int EndColumn) ParseRange(string range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        if (range.Length > 40) throw new ArgumentException("Invalid A1 range.", nameof(range));
        var parts = range.Split(':');
        if (parts.Length is < 1 or > 2) throw new ArgumentException("Invalid A1 range.", nameof(range));
        var first = ParseCell(parts[0]); var last = ParseCell(parts[^1]);
        if (first.Row > last.Row || first.Column > last.Column) throw new ArgumentException("Reversed A1 range.", nameof(range));
        return (first.Row, last.Row, first.Column, last.Column);
    }
    private static (int Row, int Column) ParseCell(string address)
    {
        var match = Address.Match(address);
        if (!match.Success) throw new ArgumentException("Invalid A1 cell.", nameof(address));
        var column = 0; foreach (var c in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + c - 'A' + 1;
        var row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (row > 1048576 || column > 16384) throw new ArgumentException("A1 cell outside Excel bounds.", nameof(address));
        return (row, column);
    }
    public static string CellAddress(int row, int column)
    {
        if (row is < 1 or > 1048576 || column is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(row));
        var letters = "";
        while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
    public static string Render(SpreadsheetDocument document, bool includeHidden = true) => string.Join("\n\n",
        document.Sheets.Where(s => includeHidden || !s.Hidden).Select(s => "# Sheet: " + s.Name + "\n" + string.Join("\n", s.Cells.Where(c => includeHidden || !c.Hidden).Select(RenderCell))));

    public static string RenderRange(SpreadsheetDocument document, string sheet, string range, int maxCells = 10000, bool includeHidden = false)
    {
        var bounds = ParseRange(range);
        if (maxCells < 1 || (long)(bounds.EndRow - bounds.StartRow + 1) * (bounds.EndColumn - bounds.StartColumn + 1) > maxCells)
            throw new ArgumentException("Requested range exceeds cell limit.", nameof(range));
        var source = document.Sheets.SingleOrDefault(s => s.Name == sheet) ?? throw new ArgumentException("Unknown sheet.", nameof(sheet));
        var cells = source.Cells.Where(c => c.Row >= bounds.StartRow && c.Row <= bounds.EndRow && c.Column >= bounds.StartColumn && c.Column <= bounds.EndColumn).ToArray();
        if (!includeHidden && (source.Hidden || cells.Any(c => c.Hidden))) throw new ArgumentException("Range contains excluded hidden data.", nameof(range));
        return "# Sheet: " + sheet + " / Range: " + range + "\nOnly stored nonempty cells follow; omitted coordinates are blank. Formula values are saved caches, not recalculated.\n" + string.Join("\n", cells.Select(RenderCell));
    }
    private static string RenderCell(SpreadsheetCell cell) => CellAddress(cell.Row, cell.Column) + " [" + cell.Kind + "] " +
        cell.Text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) +
        (cell.Formula is null ? "" : " {formula=" + cell.Formula + "; cached=" + (cell.RawValue ?? "<missing>") + "}") +
        (cell.NumberFormat is null ? "" : " {format=" + cell.NumberFormat + "; raw=" + cell.RawValue + "}");
    private static PipelineValidationException Failure(string code, string message) => new(code, message);
}
