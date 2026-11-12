namespace LocalRag.Core;

// Every implementation must inspect both the original and returned content before disclosure.
public interface ITextRetrieval
{
    Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default);
    Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default);
    Task<TextSearchResult?> FetchRangeAsync(Guid documentId, string sheet, string range, CancellationToken token = default) =>
        Task.FromException<TextSearchResult?>(new ArgumentException("範囲取得に対応していません。"));
}

public sealed record McpSettings
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 18765;
    public IReadOnlyList<string> Validate() => Port is < 1024 or > 65535
        ? ["MCPポートは1024～65535で指定してください。"] : [];
}
