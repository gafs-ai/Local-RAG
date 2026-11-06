namespace LocalRag.Core;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public int MonitoringIntervalMinutes { get; init; } = 5;
    public int UpdateExclusionMinutes { get; init; } = 10;
    public bool ExcludeDotHiddenEntries { get; init; } = true;
    public List<string> GlobalIgnorePatterns { get; init; } = [];
    public bool AutomaticMonitoring { get; init; } = true;
    public List<WatchedFolder> Folders { get; init; } = [];
    public AiConnectionSettings Ai { get; init; } = new();
    public DatabaseSettings Database { get; init; } = new();
    public McpSettings Mcp { get; init; } = new();
    public TextRagSettings TextPipeline { get; init; } = new();
    public ConfidentialityPolicy Confidentiality { get; init; } = new();
    public ManagementSettings? Management { get; init; }

    public TextRagSettings PipelineFor(string path) => Management?.Resolve(TextPipeline, path) ?? TextPipeline;
    public TextRagSettings RetrievalPrompts() => Management?.ApplyRetrieval(TextPipeline) ?? TextPipeline;
    public ConfidentialityPolicy DisclosurePolicy() => Management?.Retrieval.Policy() ?? Confidentiality;
    public string PipelineConfigurationId() => Management?.ConfigurationId(TextPipeline, Ai) ?? TextPipeline.ConfigurationId(Ai);
    public IReadOnlyList<string> AllowedTextExtensions() => Management?.AllowedExtensions() ?? [".txt", ".md"];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (GlobalIgnorePatterns is null) errors.Add("全体のignore設定がありません。");
        else
        {
            try { _ = IgnorePattern.Parse(GlobalIgnorePatterns); }
            catch (InvalidDataException ex) { errors.Add(ex.Message); }
        }
        if (SchemaVersion is not (1 or 2)) errors.Add("この設定ファイルの版には対応していません。");
        if (MonitoringIntervalMinutes is < 1 or > 10080)
            errors.Add("監視間隔は1～10080分で指定してください。");
        if (UpdateExclusionMinutes is < 0 or > 10080)
            errors.Add("更新除外期間は0～10080分で指定してください（0で無効）。");
        if (Ai is null) errors.Add("AI接続設定がありません。");
        else errors.AddRange(Ai.Validate());
        if (Database is null) errors.Add("DB設定がありません。");
        else errors.AddRange(Database.Validate());
        if (Mcp is null) errors.Add("MCP設定がありません。");
        else
        {
            errors.AddRange(Mcp.Validate());
            if (Database is { UsesExternalEndpoint: false } && Mcp.Port == Database.Port) errors.Add("MCPとDBには異なるポートを指定してください。");
        }
        if (TextPipeline is null) errors.Add("テキスト処理設定がありません。");
        else errors.AddRange(TextPipeline.Validate());
        if (Management is not null) errors.AddRange(Management.Validate());
        if (Confidentiality is null || Confidentiality.Patterns is null || Confidentiality.Patterns.Count > 100 ||
            Confidentiality.Patterns.Any(p => p is null || p.Length > 2000)) errors.Add("機密検査パターンが不正です。");
        if (Folders is null) errors.Add("フォルダ設定がありません。");
        else
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Folders)
            {
                if (folder is null || string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathFullyQualified(folder.Path))
                {
                    errors.Add("監視フォルダには絶対パスを指定してください。");
                    continue;
                }
                try
                {
                    if (folder.Priority is < 0 or > 100) errors.Add("フォルダ優先度は0～100で指定してください（大きいほど先に処理）。");
                    var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
                    if (!paths.Add(path)) errors.Add("同じ監視フォルダが重複しています。");
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    errors.Add("監視フォルダのパスが無効です。");
                }
            }
        }
        return errors;
    }
}

public sealed record WatchedFolder(string Path, bool Recursive = true, int Priority = 0);

public sealed record DatabaseSettings
{
    public int Port { get; init; } = 8000;
    public bool AutoStartServer { get; init; } = true;
    public string ExternalEndpoint { get; init; } = "";
    public bool StopOnExit { get; init; } = true;

    public bool UsesExternalEndpoint => !string.IsNullOrWhiteSpace(ExternalEndpoint);
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Port is < 1024 or > 65535) errors.Add("DBポートは1024～65535で指定してください。");
        if (ExternalEndpoint is null || UsesExternalEndpoint &&
            (!Uri.TryCreate(ExternalEndpoint, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Host) ||
             uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
             uri.AbsolutePath is not ("/" or "/rpc")))
            errors.Add("外部DB接続先はws://またはwss://で指定してください（パスは空または/rpc、認証情報・クエリは含めません）。");
        return errors;
    }

    public Uri Endpoint => UsesExternalEndpoint ? new(ExternalEndpoint.Trim()) : new($"http://127.0.0.1:{Port}");
}

public sealed record AiConnectionSettings
{
    public string BaseUrl { get; init; } = "http://localhost:13305/api/v1/";
    public bool AutoStartServer { get; init; } = true;
    public bool StopServerOnExit { get; init; } = true;
    public string ServerExecutablePath { get; init; } = "";
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public string SpeechModel { get; init; } = "";

    public IReadOnlyList<string> Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return ["AI接続先には、このPCのHTTP/HTTPSアドレスを指定してください（認証情報・クエリは含めません）。"];
        if (ServerExecutablePath is null || ServerExecutablePath.Length > 0 &&
            (!Path.IsPathFullyQualified(ServerExecutablePath) || !Path.GetExtension(ServerExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            return ["Lemonadeの実行ファイルは絶対パスの.exeを指定してください。空欄なら自動検出します。"];
        return [];
    }

    public Uri GetBaseUri()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new ArgumentException(errors[0]);
        return new Uri(BaseUrl.TrimEnd('/') + "/");
    }
}
