using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class AiEndpointWindow : Window
{
    private sealed record ProviderChoice(AiProvider Provider, string Label);
    private readonly AiEndpointSettings _original;
    private bool _loading = true;
    public AiEndpointSettings? Result { get; private set; }
    public string? ApiKey { get; private set; }
    public AiEndpointWindow(AiEndpointSettings endpoint)
    {
        InitializeComponent(); SettingHelp.Attach(this); _original = endpoint;
        ProviderBox.ItemsSource = new[] { new ProviderChoice(AiProvider.Lemonade, "Lemonade Server"), new(AiProvider.OpenVino, "OpenVINO Model Server"), new(AiProvider.LmStudio, "LM Studio") };
        ProviderBox.SelectedValue = endpoint.Provider;
        NameBox.Text = endpoint.Name; UrlBox.Text = endpoint.BaseUrl;
        AutoStartBox.IsChecked = endpoint.AutoStartServer; StopBox.IsChecked = endpoint.StopServerOnExit;
        ExecutableBox.Text = endpoint.ServerExecutablePath; ConfigBox.Text = endpoint.ModelConfigPath;
        ChatLimitBox.Text = endpoint.ChatContextTokens.ToString(); EmbeddingLimitBox.Text = endpoint.EmbeddingContextTokens.ToString();
        JsonBox.IsChecked = endpoint.JsonOutput; _loading = false; RefreshOptions();
    }
    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var provider = (AiProvider)ProviderBox.SelectedValue;
        UrlBox.Text = provider switch { AiProvider.OpenVino => "http://localhost:8001/v3/", AiProvider.LmStudio => "http://localhost:1234/v1/", _ => "http://localhost:13305/api/v1/" };
        NameBox.Text = provider switch { AiProvider.OpenVino => "OpenVINO Model Server", AiProvider.LmStudio => "LM Studio", _ => "Lemonade" };
        AutoStartBox.IsChecked = false; ExecutableBox.Clear(); ConfigBox.Clear(); KeyBox.Clear();
        ChatLimitBox.Text = provider == AiProvider.OpenVino ? "2048" : "0";
        EmbeddingLimitBox.Text = provider != AiProvider.Lemonade ? "512" : "0";
        JsonBox.IsChecked = AiEndpointSettings.DefaultJsonOutputFor(provider);
        RefreshOptions();
    }
    private void OptionsChanged(object sender, RoutedEventArgs e) { if (!_loading) RefreshOptions(); }
    private void RefreshOptions()
    {
        var local = Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == "http";
        AutoStartBox.IsEnabled = local;
        if (!local) AutoStartBox.IsChecked = false;
        StopBox.IsEnabled = local;
        LaunchPanel.Visibility = AutoStartBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var provider = (AiProvider)ProviderBox.SelectedValue;
        OvmsPanel.Visibility = provider == AiProvider.OpenVino ? Visibility.Visible : Visibility.Collapsed;
        ExecutableLabel.Text = provider == AiProvider.OpenVino ? "ovms.exeの絶対パス" : "実行ファイル（空欄で自動検出）";
        LaunchHelp.Text = provider switch
        {
            AiProvider.Lemonade => "LemonadeServer.exeまたはlemond.exeを指定できます。",
            AiProvider.LmStudio => "lms.exeを使用します。終了連動ではHTTPサーバーを停止し、共有のLM Studioアプリ・デーモンは残します。別ポートで起動済みの場合は変更しません。",
            _ => "モデル設定の相対パスはovms.exeのフォルダを基準に解決されます。絶対パスを推奨します。"
        };
    }
    private void Apply(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ChatLimitBox.Text, out var chat) || !int.TryParse(EmbeddingLimitBox.Text, out var embedding)) { ErrorText.Text = "入力上限は整数で指定してください。"; return; }
        var result = _original with { Name = NameBox.Text.Trim(), Provider = (AiProvider)ProviderBox.SelectedValue, BaseUrl = UrlBox.Text.Trim(),
            AutoStartServer = AutoStartBox.IsChecked == true, StopServerOnExit = StopBox.IsChecked == true, ServerExecutablePath = ExecutableBox.Text.Trim(),
            ModelConfigPath = ConfigBox.Text.Trim(), ChatContextTokens = chat, EmbeddingContextTokens = embedding, JsonOutput = JsonBox.IsChecked == true };
        var errors = result.Validate();
        if (errors.Count > 0) { ErrorText.Text = string.Join(Environment.NewLine, errors); return; }
        ApiKey = ClearKeyBox.IsChecked == true ? "" : KeyBox.Password.Length > 0 ? KeyBox.Password : null;
        Result = ApiKey is null ? result : result with { CredentialsRevision = Guid.NewGuid().ToString("N") };
        DialogResult = true;
    }
}
