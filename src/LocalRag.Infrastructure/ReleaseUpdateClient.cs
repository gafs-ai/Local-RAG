using System.Globalization;
using System.Net;
using System.Text.Json;

namespace LocalRag.Infrastructure;

public sealed record ReleaseUpdate(string Version)
{
    public const string RepositoryUrl = "https://github.com/gafs-ai/Local-RAG";
    // Never launch an arbitrary URL supplied by the network or a local cache file.
    public Uri Page => ParseVersion(Version) is not null
        ? new(RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(Version))
        : throw new InvalidDataException("更新情報のバージョンが不正です。");

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 40) return null;
        var text = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = text.Split('.');
        if (parts.Length != 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || parts[i].Any(c => c is < '0' or > '9') ||
                !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new(numbers[0], numbers[1], numbers[2]);
    }
}

public interface IReleaseUpdateSource
{
    Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken);
}

public sealed class ReleaseUpdateClient : IReleaseUpdateSource, IDisposable
{
    public const string FeedUrl = "https://api.github.com/repos/gafs-ai/Local-RAG/releases/latest";
    private readonly HttpClient _http;

    public ReleaseUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
        }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalRAG-UpdateCheck");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(FeedUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("更新確認のアクセスが制限されています。時間をおいて確認してください。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            ReleaseUpdate.ParseVersion(tag.GetString()) is null)
            throw new InvalidDataException("正式リリースの更新情報を確認できませんでした。");
        return new(tag.GetString()!);
    }

    public void Dispose() => _http.Dispose();
}
