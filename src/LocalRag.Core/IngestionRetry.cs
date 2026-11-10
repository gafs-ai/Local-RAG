namespace LocalRag.Core;

/// <summary>Failure metadata only; never persist provider responses or exception messages.</summary>
public sealed record IngestionFailure(string ConfigurationId, DateTime LastWriteUtc, long Length,
    string Kind, int LlmFailures, int TransientFailures, DateTimeOffset? NextAttemptAtUtc);

public static class IngestionRetry
{
    public const int MaxLlmFailures = 3;

    // Include the effective per-file pipeline, including category overrides and read limits.
    // Retrieval policy and unrelated extension routes do not restart failed ingestion.
    public static string ConfigurationId(AppSettings settings, string path) => ProcessingPlan.Hash(new
    {
        policy = "ingestion-retry/v1", classification = "classify/v2",
        pipeline = RetryPipeline(settings, path), settings.TextPipeline.MaxDocumentCharacters,
        settings.Ai.BaseUrl, settings.Ai.ChatModel, settings.Ai.EmbeddingModel
    });

    private static TextRagSettings RetryPipeline(AppSettings settings, string path)
    {
        var pipeline = settings.PipelineFor(path);
        return pipeline with { ConfidentialityPrompt = "", Word = pipeline.SourceMode == SourceCodeMode.Word ? pipeline.Word : null! };
    }

    public static bool SameInput(IngestionFailure failure, string configuration, DateTime modified, long length) =>
        failure.ConfigurationId == configuration && failure.LastWriteUtc == modified && failure.Length == length;

    public static bool CanAttempt(IngestionFailure? failure, string configuration, DateTime modified, long length, DateTimeOffset now) =>
        failure is null || !SameInput(failure, configuration, modified, length) ||
        failure.NextAttemptAtUtc is { } next && next <= now;

    public static IngestionFailure Record(IngestionFailure? previous, string configuration, DateTime modified, long length,
        Exception error, DateTimeOffset now, int intervalMinutes)
    {
        var same = previous is not null && SameInput(previous, configuration, modified, length);
        var llm = same ? previous!.LlmFailures : 0;
        var transient = same ? previous!.TransientFailures : 0;
        var kind = Kind(error);
        DateTimeOffset? next = null;
        if (kind == "llm")
        {
            llm++;
            if (llm < MaxLlmFailures) next = now.AddMinutes(Math.Max(1, intervalMinutes));
        }
        else if (kind == "transient")
        {
            transient++;
            next = now.AddMinutes(Math.Max(1, Math.Min(60, 5 * Math.Pow(2, Math.Min(transient - 1, 4)))));
        }
        return new(configuration, modified, length, kind, llm, transient, next);
    }

    public static string Kind(Exception error) => error switch
    {
        HttpRequestException http when http.StatusCode is null || (int)http.StatusCode is 408 or 429 or >= 500 => "transient",
        HttpRequestException => "permanent",
        OperationCanceledException or TimeoutException => "transient",
        // Sharing/lock violations and interrupted reads can recover without editing the file.
        IOException io when (io.HResult & 0xffff) is 32 or 33 => "transient",
        PipelineValidationException v when v.Code == "source_changed" => "transient",
        PipelineValidationException v when v.Code is "category_unknown" or "category_conflict" or "response_schema" or
            "output_incomplete" or "format_ids" or "format_destructive" or "chunk_order" or "chunk_end" or "chunk_size" or "chunk_coverage" => "llm",
        System.Text.Json.JsonException or KeyNotFoundException => "llm",
        _ => "permanent"
    };

    public static string Describe(IngestionFailure? failure) => failure switch
    {
        null => "処理保留（旧版の記録）",
        { NextAttemptAtUtc: null } => "再試行停止・設定または元ファイルの変更待ち",
        { Kind: "llm" } => $"LLM応答不正 {failure.LlmFailures}/{MaxLlmFailures}回・再試行待ち",
        _ => "一時エラー・再試行待ち"
    };
}
