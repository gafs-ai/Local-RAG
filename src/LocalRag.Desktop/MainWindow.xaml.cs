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
    public sealed class FolderDraft
    {
        public string Path { get; set; } = "";
        public string Priority { get; set; } = "0";
        public bool Recursive { get; set; } = true;
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<FolderDraft> _folderRows = [];
    private readonly string _dataDirectory;
    private readonly SettingsStore _settingsStore;
    private readonly Forms.NotifyIcon _tray;
    private CancellationTokenSource _lifetime = new();
    private readonly SurrealServerManager _server;
    private readonly LemonadeServerManager _lemonade = new();
    private Task _aiTask = Task.CompletedTask;
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
            foreach (var folder in settings.Folders) _folderRows.Add(new() { Path = folder.Path, Recursive = folder.Recursive, Priority = folder.Priority.ToString() });
            FolderGrid.ItemsSource = _folderRows;
            IntervalBox.Text = settings.MonitoringIntervalMinutes.ToString();
            UpdateExclusionBox.Text = settings.UpdateExclusionMinutes.ToString();
            ExcludeDotHiddenBox.IsChecked = settings.ExcludeDotHiddenEntries;
            GlobalIgnoreBox.Text = string.Join(Environment.NewLine, settings.GlobalIgnorePatterns);
            AutomaticMonitoringBox.IsChecked = settings.AutomaticMonitoring;
            BaseUrlBox.Text = settings.Ai.BaseUrl;
            AutoStartAiBox.IsChecked = settings.Ai.AutoStartServer;
            StopAiBox.IsChecked = settings.Ai.StopServerOnExit;
            AiExecutableBox.Text = settings.Ai.ServerExecutablePath;
            ChatModelBox.Text = settings.Ai.ChatModel;
            EmbeddingModelBox.Text = settings.Ai.EmbeddingModel;
            SpeechModelBox.Text = settings.Ai.SpeechModel;
            DbPortBox.Text = settings.Database.Port.ToString();
            StopDbBox.IsChecked = settings.Database.StopOnExit;
            McpEnabledBox.IsChecked = settings.Mcp.Enabled;
            McpPortBox.Text = settings.Mcp.Port.ToString();
            _settingsLoaded = true;
            SettingsStatus.Text = "設定はローカルに保存します。一致パターン検査は既定ON、LLM機密判定は試験的な機能で既定OFFです。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SettingsPanel.IsEnabled = false;
            AiButton.IsEnabled = false;
            AiStatus.Text = "設定を読み込めません。元の設定を上書きしないため編集を停止しました。";
            SettingsStatus.Text = SafeMessage(ex);
        }
        if (_settingsLoaded && !_shuttingDown)
            await Task.WhenAll(_databaseTask = StartServerAsync(), _aiTask = RefreshModelsAsync(_loadedSettings.Ai));
        if (_settingsLoaded && !_shuttingDown) await ReconfigureRagAsync();
    }

    private void BrowseAiExecutable(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Lemonadeサーバーの実行ファイルを選択", Filter = "実行ファイル (*.exe)|*.exe", CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) AiExecutableBox.Text = picker.FileName;
    }

    private void BrowseFolders(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown || !_saveTask.IsCompleted) return;
        if (!FolderGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true) ||
            !FolderGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true))
        {
            SettingsStatus.Text = "編集中のフォルダ設定を確認してください。";
            return;
        }
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "監視するフォルダを選択", Multiselect = true
            };
            if (FolderGrid.SelectedItem is FolderDraft selected && Directory.Exists(selected.Path))
                picker.InitialDirectory = selected.Path;
            if (picker.ShowDialog(this) != true) return;

            static string? Normalize(string path)
            {
                try { return Path.IsPathFullyQualified(path.Trim()) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())) : null; }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            }
            var added = 0;
            FolderDraft? last = null;
            foreach (var path in picker.FolderNames)
            {
                var normalized = Normalize(path);
                if (normalized is null) continue;
                last = _folderRows.FirstOrDefault(row => string.Equals(Normalize(row.Path), normalized, StringComparison.OrdinalIgnoreCase));
                if (last is not null) continue;
                last = new FolderDraft { Path = normalized };
                _folderRows.Add(last);
                added++;
            }
            if (last is not null) { FolderGrid.SelectedItem = last; FolderGrid.ScrollIntoView(last); }
            SettingsStatus.Text = added > 0
                ? $"{added}件のフォルダを追加しました。優先度・サブフォルダ設定を確認し、「設定を保存」で反映してください。"
                : "選択したフォルダはすでに一覧にあります。";
        }
        catch (Exception ex) { SettingsStatus.Text = SafeMessage(ex); }
    }

    private AppSettings ReadSettings()
    {
        if (!int.TryParse(IntervalBox.Text, out var interval)) throw new InvalidDataException("監視間隔を整数で入力してください。");
        if (!int.TryParse(UpdateExclusionBox.Text, out var exclusion)) throw new InvalidDataException("更新除外期間を整数で入力してください。");
        FolderGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        FolderGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var folders = _folderRows.Select(row => new WatchedFolder(row.Path.Trim(), row.Recursive,
            int.TryParse(row.Priority, out var priority) ? priority : throw new InvalidDataException("フォルダ優先度を整数で入力してください。"))).ToList();
        if (!int.TryParse(DbPortBox.Text, out var port)) throw new InvalidDataException("DBポートを整数で入力してください。");
        if (!int.TryParse(McpPortBox.Text, out var mcpPort)) throw new InvalidDataException("MCPポートを整数で入力してください。");
        var settings = _loadedSettings with
        {
            MonitoringIntervalMinutes = interval,
            UpdateExclusionMinutes = exclusion,
            ExcludeDotHiddenEntries = ExcludeDotHiddenBox.IsChecked == true,
            GlobalIgnorePatterns = GlobalIgnoreBox.Text.Split('\n').Select(line => line.TrimEnd('\r')).ToList(),
            AutomaticMonitoring = AutomaticMonitoringBox.IsChecked == true,
            Mcp = new McpSettings { Enabled = McpEnabledBox.IsChecked == true, Port = mcpPort },
            Database = new DatabaseSettings { Port = port, StopOnExit = StopDbBox.IsChecked == true },
            Folders = folders,
            Ai = new AiConnectionSettings
            {
                BaseUrl = BaseUrlBox.Text.Trim(), ChatModel = ChatModelBox.Text.Trim(),
                EmbeddingModel = EmbeddingModelBox.Text.Trim(), SpeechModel = SpeechModelBox.Text.Trim(),
                AutoStartServer = AutoStartAiBox.IsChecked == true, StopServerOnExit = StopAiBox.IsChecked == true,
                ServerExecutablePath = AiExecutableBox.Text.Trim()
            }
        };
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return settings;
    }

    private async void SaveSettings(object sender, RoutedEventArgs e) => await (_saveTask = SaveSettingsAsync());

    private async void OpenManager(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown || !_saveTask.IsCompleted || !_startupTask.IsCompleted) return;
        var tab = int.Parse((string)((System.Windows.Controls.Button)sender).Tag);
        var editor = new ManagementWindow(_loadedSettings, tab) { Owner = this };
        if (editor.ShowDialog() != true) return;
        await (_saveTask = SaveManagementAsync(editor.Result!, editor.Limits));
    }

    private async Task SaveManagementAsync(ManagementSettings management, TextRagSettings limits)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var settings = _loadedSettings with { SchemaVersion = 2, Management = management, TextPipeline = limits };
            await _settingsStore.SaveAsync(settings, _lifetime.Token);
            _loadedSettings = settings;
            await ReconfigureRagAsync();
            SettingsStatus.Text = "処理設定を保存して適用しました。取り込み設定を変更した文書は次回探索で再処理します。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SettingsStatus.Text = SafeMessage(ex); }
        finally { SaveButton.IsEnabled = true; }
    }

    private async Task SaveSettingsAsync()
    {
        if (!_settingsLoaded || _shuttingDown || !_startupTask.IsCompleted) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _aiTask;
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
            await ReconfigureRagAsync(refreshAi: true);
            SettingsStatus.Text = "設定を保存しました。取り込み・検索タブで状態を確認できます。DBポート変更は再起動後に反映します。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SettingsStatus.Text = SafeMessage(ex); }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void CheckAi(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown || !_aiTask.IsCompleted || !_startupTask.IsCompleted || !_saveTask.IsCompleted) return;
        await (_aiTask = RefreshModelsAsync(_loadedSettings.Ai with
        {
            BaseUrl = BaseUrlBox.Text.Trim(), AutoStartServer = AutoStartAiBox.IsChecked == true,
            ServerExecutablePath = AiExecutableBox.Text.Trim()
        }));
    }

    private async Task RefreshModelsAsync(AiConnectionSettings settings)
    {
        AiButton.IsEnabled = false;
        AiStatus.Text = "接続とモデル一覧を確認しています…";
        try
        {
            var status = await _lemonade.EnsureRunningAsync(settings,
                new Progress<string>(message => AiStatus.Text = message), _lifetime.Token);
            if (_shuttingDown || BaseUrlBox.Text.Trim() != settings.BaseUrl) return;
            var models = status.Models;
            ModelList.ItemsSource = models.Select(m => $"{m.Id}   ·   {string.Join(", ", m.Labels)}   ·   {(m.VerifiedLocal ? "ローカル" : "ローカル性未確認／利用不可")}");
            FillModels(ChatModelBox, models, "chat");
            FillModels(EmbeddingModelBox, models, "embeddings");
            FillModels(SpeechModelBox, models, "transcription");
            var hasEmbedding = models.Any(m => m.VerifiedLocal && m.Labels.Contains("embeddings"));
            AiStatus.Text = $"接続できました（{models.Count}モデル）。" +
                (status.ManagedByApplication ? "このアプリが起動したLemonadeです。" : "既存のLemonadeです。アプリ終了時にも停止しません。") +
                (hasEmbedding ? "Embedding候補があります。推論の実動作は別途検証します。" : "Embedding用モデルが未準備です。索引作成には追加設定が必要です。");
        }
        catch (OperationCanceledException) { ClearModels(); AiStatus.Text = "接続確認を中断しました（時間切れまたは終了）。"; }
        catch (Exception ex) { ClearModels(); AiStatus.Text = SafeMessage(ex); }
        finally { AiButton.IsEnabled = !_shuttingDown; }
    }

    private void ClearModels()
    {
        ModelList.ItemsSource = null;
        FillModels(ChatModelBox, [], "chat");
        FillModels(EmbeddingModelBox, [], "embeddings");
        FillModels(SpeechModelBox, [], "transcription");
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
            await Task.WhenAll(_startupTask, _databaseTask, _saveTask, _aiTask);
            await StopRagAsync();
            await _lemonade.ShutdownAsync(_loadedSettings.Ai.StopServerOnExit);
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
        _lemonade.DisposeAsync().GetAwaiter().GetResult();
    }
}
