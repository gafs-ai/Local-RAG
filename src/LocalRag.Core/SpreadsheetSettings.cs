using System.Text.Json.Serialization;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SpreadsheetMode>))]
public enum SpreadsheetMode { Automatic, Records, NumericTable, Document, Form, Raw, Skip }
public sealed record SpreadsheetOverride
{
    public string Sheet { get; init; } = "";
    public string Range { get; init; } = "";
    public SpreadsheetMode Mode { get; init; } = SpreadsheetMode.Automatic;
    public int HeaderRows { get; init; } = -1;
}
public sealed record SpreadsheetSettings
{
    public SpreadsheetMode Mode { get; init; } = SpreadsheetMode.Automatic;
    // -1: automatic, 0: no header, positive: number of header rows in each region.
    public int HeaderRows { get; init; } = -1;
    public string CsvDelimiter { get; init; } = "auto";
    public bool IncludeHidden { get; init; }
    public int MaxCells { get; init; } = 100000;
    public List<SpreadsheetOverride> Overrides { get; init; } = [];
    public IReadOnlyList<string> Validate() => !Enum.IsDefined(Mode) || HeaderRows is < -1 or > 20 ||
        MaxCells is < 1 or > 1000000 || CsvDelimiter is not ("auto" or "," or ";" or "\t" or "|") ||
        Overrides is null || Overrides.Count > 1000 || Overrides.Any(o => o is null || string.IsNullOrWhiteSpace(o.Sheet) || !Enum.IsDefined(o.Mode) || o.HeaderRows is < -1 or > 20 ||
            !string.IsNullOrEmpty(o.Range) && !System.Text.RegularExpressions.Regex.IsMatch(o.Range,
                @"^\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}(:\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        ? ["Excel・CSVの処理方式、ヘッダー行数、区切り文字、セル上限または範囲指定が不正です。"] : [];
}
public sealed record SpreadsheetCell(int Row, int Column, string Text, string Kind = "text", string? RawValue = null,
    string? Formula = null, string? NumberFormat = null, bool Bold = false, bool Hidden = false);
public sealed record SpreadsheetTable(string Name, string Range, int HeaderRows = 1);
public sealed record SpreadsheetSheet(string Name, List<SpreadsheetCell> Cells, List<string> MergedRanges,
    List<SpreadsheetTable> Tables, bool Hidden = false);
public sealed record SpreadsheetDocument(List<SpreadsheetSheet> Sheets, List<string> Notices);
public sealed record SpreadsheetRegion(string Id, string Sheet, string Range, SpreadsheetMode Mode,
    string Text, string SearchText, int StartRow, int EndRow, int StartColumn, int EndColumn);
