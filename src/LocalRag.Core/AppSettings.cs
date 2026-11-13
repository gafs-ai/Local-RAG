namespace LocalRag.Core;

public sealed record AppSettings
{
    public static string DefaultDataSourceName => "Local-" + Environment.UserName;
    public string DataSourceName { get; init; } = DefaultDataSourceName;
    public string? DataSourceOwner { get; init; }
    public int SchemaVersion { get; init; } = 2;
    public int MonitoringIntervalMinutes { get; init; } = 5;
    public int UpdateExclusionMinutes { get; init; } = 10;
    public bool ExcludeDotHiddenEntries { get; init; } = true;
    public List<string> GlobalIgnorePatterns { get; init; } = [];
    public bool AutomaticMonitoring { get; init; } = true;
    public bool AutomaticUpdateChecks { get; init; } = true;
    public List<WatchedFolder> Folders { get; init; } = [];
    public AiConnectionSettings Ai { get; init; } = new();
    public DatabaseSettings Database { get; init; } = new();
    public McpSettings Mcp { get; init; } = new();
    public UsageStatisticsSettings UsageStatistics { get; init; } = new();
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
        if (!ValidDataSourceName(DataSourceName)) errors.Add("データソース名は前後の空白・制御文字を含まない1～200文字で指定してください。");
        if (GlobalIgnorePatterns is null) errors.Add("全体のignore設定がありません。");
        else
        {
            try { _ = IgnorePattern.Parse(GlobalIgnorePatterns); }
            catch (InvalidDataException ex) { errors.Add(ex.Message); }
        }
        if (SchemaVersion is not (1 or 2 or 3)) errors.Add("この設定ファイルの版には対応していません。");
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
        if (UsageStatistics is null) errors.Add("利用統計設定がありません。");
        if (TextPipeline is null) errors.Add("テキスト処理設定がありません。");
        else errors.AddRange(TextPipeline.Validate());
        if (Management is not null) errors.AddRange(Management.Validate());
        if (Confidentiality is null) errors.Add("機密検査設定がありません。");
        else errors.AddRange(Confidentiality.Validate());
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

    public static bool ValidDataSourceName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 &&
        name == name.Trim() && !name.Any(char.IsControl);
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
    public List<AiEndpointSettings> Connections { get; init; } = [];
    public string ChatConnectionId { get; init; } = "";
    public string EmbeddingConnectionId { get; init; } = "";
    public string BaseUrl { get; init; } = "http://localhost:13305/api/v1/";
    public bool AutoStartServer { get; init; } = true;
    public bool StopServerOnExit { get; init; } = true;
    public string ServerExecutablePath { get; init; } = "";
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public string SpeechModel { get; init; } = "";

    public IReadOnlyList<string> Validate()
    {
        if (Connections is null) return ["AI接続先の一覧がありません。"];
        var endpoints = Endpoints();
        var errors = endpoints.SelectMany(e => e is null ? new[] { "AI接続先が不正です。" } : e.Validate()).ToList();
        if (endpoints.Count > 32 || endpoints.Where(e => e is not null).GroupBy(e => e.Id).Any(g => g.Count() != 1)) errors.Add("AI接続先は重複しない32件以内で指定してください。");
        if (Connections.Count > 0 && (!endpoints.Any(e => e?.Id == ChatConnectionId) || !endpoints.Any(e => e?.Id == EmbeddingConnectionId))) errors.Add("言語モデルとEmbeddingモデルの接続先を選択してください。");
        return errors;
    }

    public IReadOnlyList<AiEndpointSettings> Endpoints() => Connections.Count > 0 ? Connections :
        [new() { Id = "lemonade-default", Name = "Lemonade", BaseUrl = BaseUrl, AutoStartServer = AutoStartServer, StopServerOnExit = StopServerOnExit, ServerExecutablePath = ServerExecutablePath }];
    public AiEndpointSettings ChatEndpoint() => Connections.Count == 0 ? Endpoints()[0] : Connections.Single(e => e.Id == ChatConnectionId);
    public AiEndpointSettings EmbeddingEndpoint() => Connections.Count == 0 ? Endpoints()[0] : Connections.Single(e => e.Id == EmbeddingConnectionId);
    public AiConnectionSettings WithConnections() => Connections.Count > 0 ? this : this with
        { Connections = Endpoints().ToList(), ChatConnectionId = "lemonade-default", EmbeddingConnectionId = "lemonade-default" };
    public static AiConnectionSettings ForEndpoint(AiEndpointSettings endpoint) => new()
        { BaseUrl = endpoint.BaseUrl, AutoStartServer = endpoint.AutoStartServer, StopServerOnExit = endpoint.StopServerOnExit, ServerExecutablePath = endpoint.ServerExecutablePath };
    public string CombinedIdentity() => ChatEndpoint().ChatIdentity == EmbeddingEndpoint().EmbeddingProcessingIdentity ? ChatEndpoint().ChatIdentity :
        ChatEndpoint().ChatIdentity + "\nembedding=" + EmbeddingEndpoint().EmbeddingProcessingIdentity;
    public bool UsesExternalInference() => !ChatEndpoint().IsLocal || !EmbeddingEndpoint().IsLocal;
    public string RetryIdentity() => ChatEndpoint().CredentialsRevision.Length == 0 && EmbeddingEndpoint().CredentialsRevision.Length == 0 ? CombinedIdentity() :
        CombinedIdentity() + "\ncredentials=" + ChatEndpoint().CredentialsRevision + "|" + EmbeddingEndpoint().CredentialsRevision;

    public Uri GetBaseUri()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new ArgumentException(errors[0]);
        return new Uri(BaseUrl.TrimEnd('/') + "/");
    }
}
