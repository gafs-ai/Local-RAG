using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using LocalRag.Core;
using LocalRag.Infrastructure;
using Forms = System.Windows.Forms;

namespace LocalRag.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private readonly string _dataDirectory;
    private readonly SettingsStore _settingsStore;
    private readonly Forms.NotifyIcon _tray;
    private CancellationTokenSource _lifetime = new();
    private readonly SurrealServerManager _server;
    private Task _startupTask = Task.CompletedTask;
    private Task _databaseTask = Task.CompletedTask;
    private Task _saveTask = Task.CompletedTask;
    private bool _shuttingDown;
    private bool _disposed;
    private bool _settingsLoaded;
    private AppSettings _loadedSettings = new();

    public MainWindow(string dataDirectory)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory;
        _settingsStore = new SettingsStore(dataDirectory);
        _server = new SurrealServerManager(dataDirectory);
        DataPathText.Text = dataDirectory;
        _tray = new Forms.NotifyIcon
        {
            Text = "Local RAG — 開発版",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("管理画面を開く", null, (_, _) => Dispatcher.Invoke(ShowManager));
        _tray.ContextMenuStrip.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(() => ((App)System.Windows.Application.Current).ExitApplication()));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManager);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await (_startupTask = InitializeAsync());

    private async Task InitializeAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(_lifetime.Token);
            _loadedSettings = settings;
            FoldersBox.Text = string.Join(Environment.NewLine, settings.Folders.Select(x => x.Path));
            IntervalBox.Text = settings.MonitoringIntervalMinutes.ToString();
            AutomaticMonitoringBox.IsChecked = settings.AutomaticMonitoring;
            AdvancedBox.Text = JsonSerializer.Serialize(new AdvancedSettings(settings.TextPipeline, settings.Confidentiality), AdvancedJson);
            BaseUrlBox.Text = settings.Ai.BaseUrl;
            ChatModelBox.Text = settings.Ai.ChatModel;
            EmbeddingModelBox.Text = settings.Ai.EmbeddingModel;
            SpeechModelBox.Text = settings.Ai.SpeechModel;
            DbPortBox.Text = settings.Database.Port.ToString();
            StopDbBox.IsChecked = settings.Database.StopOnExit;
            _settingsLoaded = true;
            SettingsStatus.Text = "設定はローカルに保存します。機密検査は標準で有効です。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SettingsPanel.IsEnabled = false;
            AiButton.IsEnabled = false;
            AiStatus.Text = "設定を読み込めません。元の設定を上書きしないため編集を停止しました。";
            SettingsStatus.Text = SafeMessage(ex);
        }
        if (_settingsLoaded && !_shuttingDown) await (_databaseTask = StartServerAsync());
        if (_settingsLoaded && !_shuttingDown) await ReconfigureRagAsync();
    }

    private AppSettings ReadSettings()
    {
        if (!int.TryParse(IntervalBox.Text, out var interval)) throw new InvalidDataException("監視間隔を整数で入力してください。");
        if (!int.TryParse(DbPortBox.Text, out var port)) throw new InvalidDataException("DBポートを整数で入力してください。");
        var advanced = JsonSerializer.Deserialize<AdvancedSettings>(AdvancedBox.Text, AdvancedJson) ?? throw new InvalidDataException("詳細設定が空です。");
        var settings = _loadedSettings with
        {
            MonitoringIntervalMinutes = interval,
            AutomaticMonitoring = AutomaticMonitoringBox.IsChecked == true,
            TextPipeline = advanced.TextPipeline,
            Confidentiality = advanced.Confidentiality,
            Database = new DatabaseSettings { Port = port, StopOnExit = StopDbBox.IsChecked == true },
            Folders = FoldersBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => new WatchedFolder(path,
                    _loadedSettings.Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Recursive ?? true))
                .ToList(),
            Ai = new AiConnectionSettings
            {
                BaseUrl = BaseUrlBox.Text.Trim(), ChatModel = ChatModelBox.Text.Trim(),
                EmbeddingModel = EmbeddingModelBox.Text.Trim(), SpeechModel = SpeechModelBox.Text.Trim()
            }
        };
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return settings;
    }

    private async void SaveSettings(object sender, RoutedEventArgs e) => await (_saveTask = SaveSettingsAsync());

    private async Task SaveSettingsAsync()
    {
        if (!_settingsLoaded || _shuttingDown) return;
        SaveButton.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            if (DbPasswordBox.Password.Length > 0 || DbUserBox.Text.Length > 0)
            {
                if (_server.Connection is not null || !_databaseTask.IsCompleted)
                    throw new InvalidDataException("DBの接続中は認証情報を変更できません。接続できない状態で設定してください。");
                await new ServerCredentialStore(_dataDirectory).SaveAsync(new ServerCredentials(DbUserBox.Text.Trim(), DbPasswordBox.Password), _lifetime.Token);
                DbPasswordBox.Clear();
                DbUserBox.Clear();
            }
            await _settingsStore.SaveAsync(settings, _lifetime.Token);
            _loadedSettings = settings;
            await ReconfigureRagAsync();
            SettingsStatus.Text = "設定を保存しました。取り込み・検索タブで状態を確認できます。DBポート変更は再起動後に反映します。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SettingsStatus.Text = SafeMessage(ex); }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void CheckAi(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown) return;
        AiButton.IsEnabled = false;
        AiStatus.Text = "接続とモデル一覧を確認しています…";
        try
        {
            using var client = new LemonadeClient(new AiConnectionSettings { BaseUrl = BaseUrlBox.Text.Trim() });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var models = await client.GetModelsAsync(timeout.Token);
            ModelList.ItemsSource = models.Select(m => $"{m.Id}   ·   {string.Join(", ", m.Labels)}   ·   {(m.VerifiedLocal ? "ローカル" : "ローカル性未確認／利用不可")}");
            FillModels(ChatModelBox, models, "chat");
            FillModels(EmbeddingModelBox, models, "embeddings");
            FillModels(SpeechModelBox, models, "transcription");
            var hasEmbedding = models.Any(m => m.VerifiedLocal && m.Labels.Contains("embeddings"));
            AiStatus.Text = $"接続できました（{models.Count}モデル）。" +
                (hasEmbedding ? "Embedding候補があります。推論の実動作は別途検証します。" : "Embedding用モデルが未準備です。索引作成には追加設定が必要です。");
        }
        catch (OperationCanceledException) { AiStatus.Text = "接続確認を中断しました（時間切れまたは終了）。"; }
        catch (Exception ex) { AiStatus.Text = SafeMessage(ex); }
        finally { AiButton.IsEnabled = true; }
    }

    private static void FillModels(System.Windows.Controls.ComboBox combo, IReadOnlyList<LemonadeModel> models, string label)
    {
        var current = combo.Text;
        combo.ItemsSource = models.Where(m => m.VerifiedLocal && m.Labels.Contains(label)).Select(m => m.Id).ToArray();
        combo.Text = string.IsNullOrWhiteSpace(current) && combo.Items.Count == 1 ? (string)combo.Items[0] : current;
    }

    private async void CheckDatabase(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown || !_databaseTask.IsCompleted) return;
        await (_databaseTask = CheckDatabaseAsync());
    }

    private async Task StartServerAsync()
    {
        DbButton.IsEnabled = false;
        try
        {
            var status = await _server.EnsureRunningAsync(_loadedSettings.Database,
                new Progress<string>(message => DatabaseStatus.Text = message), _lifetime.Token);
            DatabaseStatus.Text = $"SurrealDB {status.Version} に接続しました（ポート {status.Port}）。" +
                (status.ManagedByApplication ? "このアプリが起動したDBです。" : "既存のDBです。アプリ終了時にも停止しません。");
        }
        catch (OperationCanceledException) { DatabaseStatus.Text = "DBの準備を中断しました。"; }
        catch (Exception ex) { DatabaseStatus.Text = SafeMessage(ex); }
        finally { DbButton.IsEnabled = !_shuttingDown; }
    }

    private async Task CheckDatabaseAsync()
    {
        DbButton.IsEnabled = false;
        DatabaseStatus.Text = "保存と再読み込みを確認しています…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            await _server.EnsureRunningAsync(_loadedSettings.Database,
                new Progress<string>(message => DatabaseStatus.Text = message), timeout.Token);
            var result = await DatabaseProbe.RunAsync(_server.Connection!, timeout.Token);
            DatabaseStatus.Text = result.Detail;
            if (_rag is null && !_shuttingDown) await ReconfigureRagAsync();
        }
        catch (OperationCanceledException) { DatabaseStatus.Text = "保存機能の確認を中断しました。"; }
        catch (Exception ex) { DatabaseStatus.Text = SafeMessage(ex); }
        finally { DbButton.IsEnabled = !_shuttingDown; }
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        PipelineValidationException validation => PipelineErrors.Describe(null, validation.Code),
        InvalidDataException or InvalidOperationException or TimeoutException => ex.Message,
        ArgumentException => "入力内容が正しくありません。接続先やパスを確認してください。",
        HttpRequestException => "接続できません。接続先とネットワークの状態を確認してください。",
        JsonException => "設定または応答の形式が正しくありません。元の設定ファイルは保持しています。",
        IOException or UnauthorizedAccessException => "保存先にアクセスできません。ファイルの利用状況とアクセス権を確認してください。",
        _ => $"処理に失敗しました（{ex.GetType().Name}）。"
    };

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!((App)System.Windows.Application.Current).IsExiting) { e.Cancel = true; Hide(); }
    }

    private void ShowManager() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ExitClick(object sender, RoutedEventArgs e) => ((App)System.Windows.Application.Current).ExitApplication();

    internal async Task PrepareShutdownAsync()
    {
        _shuttingDown = true;
        RagPanel.IsEnabled = false;
        SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = false;
        DatabaseStatus.Text = "終了処理を待っています…";
        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_startupTask, _databaseTask, _saveTask);
            await StopRagAsync();
            await _server.ShutdownAsync(_loadedSettings.Database.StopOnExit);
        }
        catch
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            _shuttingDown = false;
            SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = _settingsLoaded;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _lifetime.Dispose();
        _monitorCancellation?.Dispose();
        _server.DisposeAsync().GetAwaiter().GetResult();
    }
}
