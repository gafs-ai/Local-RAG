using System.Text.Json.Serialization;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PdfInputPolicy>))]
public enum PdfInputPolicy { Automatic, ExtractText, Skip }

public sealed record PdfSettings
{
    public PdfInputPolicy Policy { get; init; } = PdfInputPolicy.Automatic;
    public int MaxSamplePages { get; init; } = 15;
    public bool RemoveHeadersFooters { get; init; } = true;
    public IReadOnlyList<string> Validate() => !Enum.IsDefined(Policy) || MaxSamplePages is < 3 or > 63
        ? ["PDFの判定方式、または調査ページ上限（3～63）が不正です。"] : [];
    public static string DefaultRepairPrompt => WordSettings.DefaultRepairPrompt.Replace("Word", "PDF");
}

public sealed record PdfPageAssessment(int Page, int Characters, int ProseLines, double ImageCoverage,
    double DrawingCoverage, string Kind);
public sealed record PdfAssessment(int PageCount, List<PdfPageAssessment> Samples, string Decision,
    string Reason, List<int> TextUnavailablePages);
