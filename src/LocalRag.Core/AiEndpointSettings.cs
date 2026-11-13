using System.Text.Json.Serialization;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AiProvider>))]
public enum AiProvider { Lemonade, OpenVino, LmStudio }

public sealed record AiEndpointSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Lemonade";
    public AiProvider Provider { get; init; }
    public string BaseUrl { get; init; } = "http://localhost:13305/api/v1/";
    public bool AutoStartServer { get; init; }
    public bool StopServerOnExit { get; init; } = true;
    public string ServerExecutablePath { get; init; } = "";
    public string ModelConfigPath { get; init; } = "";
    // Explicit fallback limits, never architectural maxima. Zero requires runtime discovery.
    public int ChatContextTokens { get; init; }
    public int EmbeddingContextTokens { get; init; }
    public bool JsonOutput { get; init; } = true;
    // Preserve stored/missing-field behavior; change only creation of new profiles.
    // OVMS 2026.4 can constrain an open-ended object schema to an empty object.
    public static bool DefaultJsonOutputFor(AiProvider provider) => provider != AiProvider.OpenVino;
    // Random change marker, never a key or a hash of a key. Only invalidates failed-attempt suppression.
    public string CredentialsRevision { get; init; } = "";

    public Uri GetBaseUri() => new(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    [JsonIgnore] public bool IsLocal => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
    [JsonIgnore] public string Identity => Provider == AiProvider.Lemonade ? BaseUrl : Provider + "|" + BaseUrl;
    [JsonIgnore] public string ChatIdentity => ChatContextTokens == 0 && JsonOutput ? Identity : Identity + $"|context={ChatContextTokens}|json={JsonOutput}";
    [JsonIgnore] public string EmbeddingProcessingIdentity => EmbeddingContextTokens == 0 ? Identity : Identity + $"|context={EmbeddingContextTokens}";
    public override string ToString() => Name;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) errors.Add("AI接続先IDが不正です。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl)) errors.Add("AI接続先名は1～100文字で指定してください。");
        if (!Enum.IsDefined(Provider)) errors.Add("AIサーバーの種類が不正です。");
        if (CredentialsRevision is null || CredentialsRevision.Length > 64 || CredentialsRevision.Any(c => !char.IsAsciiHexDigit(c))) errors.Add("AI認証設定の変更情報が不正です。");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || BaseUrl.Length > 2048 || BaseUrl.Any(char.IsControl) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            errors.Add("AI接続先はHTTP/HTTPSのURLで指定してください（認証情報・クエリは含めません）。");
        if (AutoStartServer && (uri is null || !uri.IsLoopback || uri.Scheme != "http" || uri.Port < 1024)) errors.Add("AIの自動起動はこのPCのHTTP接続先・1024以上のポートだけで使用できます。外部サーバーは自動起動をOFFにしてください。");
        if (ServerExecutablePath is null || ServerExecutablePath.Length > 0 &&
            (!Path.IsPathFullyQualified(ServerExecutablePath) || !Path.GetExtension(ServerExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))) errors.Add("AI実行ファイルは絶対パスの.exeで指定してください。");
        if (ModelConfigPath is null || ModelConfigPath.Length > 0 && !Path.IsPathFullyQualified(ModelConfigPath)) errors.Add("OVMSモデル設定は絶対パスで指定してください。");
        if (Provider == AiProvider.OpenVino && AutoStartServer && (string.IsNullOrWhiteSpace(ServerExecutablePath) || string.IsNullOrWhiteSpace(ModelConfigPath))) errors.Add("OVMSの自動起動には実行ファイルとモデル設定ファイルを指定してください。");
        if (ChatContextTokens != 0 && ChatContextTokens is < 512 or > 2097152 || EmbeddingContextTokens != 0 && EmbeddingContextTokens is < 256 or > 2097152) errors.Add("入力上限は言語モデル512以上、Embedding 256以上、最大2097152で指定してください（0は自動取得必須）。");
        return errors;
    }
}
