namespace LocalRag.Core;

/// <summary>Metadata only: never contains source contents, model answers, or exception messages.</summary>
public sealed record IngestionHistoryEntry(Guid Id, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    string Path, string Result, string? ErrorStage = null, string? ErrorDetailCode = null,
    string? WithdrawalReason = null, string[]? Notices = null, string? ConfigurationId = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string CompletedDisplay => CompletedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss.fff");
    public string ResultDisplay => Result switch
    {
        "unchanged" => "変更なし", "deferred" => "更新中のため延期", "interrupted" => "中断・再開待ち",
        _ => PipelineErrors.Status(Result, WithdrawalReason)
    };
    public string DetailDisplay => (ErrorDetailCode is null ? "" : PipelineErrors.Describe(ErrorStage, ErrorDetailCode)) +
        (Notices is { Length: > 0 } ? " / " + string.Join("、", Notices.Select(PipelineErrors.Notice)) : "");
}
