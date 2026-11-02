using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private sealed record AdvancedSettings(TextRagSettings TextPipeline, ConfidentialityPolicy Confidentiality);
    private static readonly JsonSerializerOptions AdvancedJson = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record SearchRow(Guid DocumentId, string Label);
    private TextRagRepository? _repository;
    private LemonadeTextAi? _ragAi;
    private TextRagService? _rag;
    private LocalMcpHost? _mcp;
    private PersistentMonitor? _persistentMonitor;
    private CancellationTokenSource? _monitorCancellation;
    private CancellationTokenSource? _ragCancellation;
    private Task _monitorTask = Task.CompletedTask;
    private Task _ragTask = Task.CompletedTask;
    private bool _ragChanging;
    private readonly SemaphoreSlim _ragConfiguration = new(1, 1);

    private async Task ReconfigureRagAsync()
    {
        await _ragConfiguration.WaitAsync();
        _ragChanging = true;
        RagPanel.IsEnabled = false;
        SearchList.ItemsSource = null;
        SearchOutput.Clear();
        SearchStatus.Text = "";
        try
        {
            await StopRagAsync();
            SearchList.ItemsSource = null;
            SearchOutput.Clear();
            SearchStatus.Text = "";
            if (_shuttingDown || _lifetime.IsCancellationRequested) return;
            if (_server.Connection is null || _loadedSettings.Folders.Count == 0 ||
                string.IsNullOrWhiteSpace(_loadedSettings.Ai.ChatModel) || string.IsNullOrWhiteSpace(_loadedSettings.Ai.EmbeddingModel))
            {
                RagStatus.Text = "DB接続とフォルダ・チャット・Embeddingモデルの設定を確認し、保存してください。";
                return;
            }
            _repository = await TextRagRepository.OpenAsync(_server.Connection, _lifetime.Token);
            _ragCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ragAi = new LemonadeTextAi(_loadedSettings);
            _rag = new TextRagService(_repository, _ragAi, _loadedSettings, _dataDirectory);
            _persistentMonitor = new PersistentMonitor(_repository, MonitoringCheckpoint.ScopeFor(_loadedSettings));
            ShowDocuments(await _repository.DocumentsAsync(_lifetime.Token));
            PolicyStatus.Text = _loadedSettings.Confidentiality.Enabled
                ? $"機密検査：パターン {(_loadedSettings.Confidentiality.PatternsEnabled ? "ON" : "OFF")} ／ ローカルLLM {(_loadedSettings.Confidentiality.LlmEnabled ? "ON" : "OFF")}" : "機密検査は無効です。未検査の本文・出典を表示します。";
            RagPanel.IsEnabled = true;
            RagStatus.Text = "取り込みと検索を利用できます。";
            McpStatus.Text = "MCPは無効です。";
            if (_loadedSettings.Mcp.Enabled)
            {
                try
                {
                    var secret = await new McpTokenStore(_dataDirectory).GetOrCreateAsync(_lifetime.Token);
                    _mcp = await LocalMcpHost.StartAsync(_rag, _loadedSettings.Mcp.Port, secret, _lifetime.Token);
                    McpStatus.Text = $"MCP待機中：127.0.0.1:{_loadedSettings.Mcp.Port}（このPCのみ・認証必須）。接続にはLocalRag.Mcpのstdio中継を使用します。";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { McpStatus.Text = "MCPを開始できません。ポートの競合・設定を確認し、設定を保存して再試行してください。"; }
            }
            if (_loadedSettings.AutomaticMonitoring) StartMonitoring();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RagStatus.Text = SafeMessage(ex); }
        finally { _ragChanging = false; _ragConfiguration.Release(); }
    }

    private void StartMonitoring()
    {
        if (_rag is null || !_monitorTask.IsCompleted) return;
        _monitorCancellation?.Dispose();
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_ragCancellation!.Token);
        _monitorTask = MonitorAsync(_monitorCancellation.Token);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        ScanButton.IsEnabled = false;
        try
        {
            await _persistentMonitor!.RunAsync(async ct =>
            {
                await RunCycleAsync(ct).ConfigureAwait(false);
            }, TimeSpan.FromMinutes(_loadedSettings.MonitoringIntervalMinutes), ex => Dispatcher.InvokeAsync(() =>
            {
                RagStatus.Text = $"取り込みを保留しました（{ex.GetType().Name}）。指定間隔の後に再試行します。";
            }).Task, due => Dispatcher.InvokeAsync(() =>
            {
                RagStatus.Text = $"前回の処理完了から監視間隔を待っています。次回探索予定：{due.ToLocalTime():yyyy/MM/dd HH:mm:ss}。";
            }).Task, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { RagStatus.Text = $"自動監視を停止しました（{ex.GetType().Name}）。DB接続と設定を確認し、設定を保存して再開してください。"; }
        finally { ScanButton.IsEnabled = !_shuttingDown; }
    }

    private async Task RunCycleAsync(CancellationToken token)
    {
        var progress = new Progress<string>(message => Dispatcher.Invoke(() => RagStatus.Text = message));
        var result = await _rag!.ScanAsync(progress, token).ConfigureAwait(false);
        var documents = await _repository!.DocumentsAsync(token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            RagStatus.Text = $"完了：更新 {result.Indexed}、対象外 {result.Excluded}、変更なし {result.Unchanged}、保留 {result.Failed}、削除 {result.Deleted}、未探索フォルダ {result.InaccessibleRoots}。" +
                (_loadedSettings.AutomaticMonitoring && _monitorCancellation is { IsCancellationRequested: false } ? $" 次回は完了から{_loadedSettings.MonitoringIntervalMinutes}分後です。" : "");
            ShowDocuments(documents);
        });
    }

    private void ShowDocuments(IReadOnlyList<TextDocumentState> documents) =>
        DocumentStatusList.ItemsSource = documents.Select(d => $"{d.Path} — {d.Status}" +
            (d.ErrorCode is null ? "" : $"（{PipelineErrors.Describe(d.ErrorStage, d.ErrorDetailCode)}）")).ToArray();

    private async void PauseMonitoring(object sender, RoutedEventArgs e)
    {
        if (_ragChanging || _shuttingDown) return;
        _monitorCancellation?.Cancel();
        await _monitorTask;
        RagStatus.Text = "自動監視を一時停止しました。設定を保存すると自動監視設定に従って再開します。";
    }

    private async void ScanNow(object sender, RoutedEventArgs e)
    {
        if (_rag is null || _ragChanging || _shuttingDown || !_ragTask.IsCompleted || !_monitorTask.IsCompleted) return;
        await (_ragTask = RunUserActionAsync(() => _persistentMonitor!.RunCycleAsync(RunCycleAsync, _ragCancellation!.Token)));
    }

    private async void SearchText(object sender, RoutedEventArgs e)
    {
        if (_rag is null || _ragChanging || _shuttingDown || !_ragTask.IsCompleted) return;
        var query = QueryBox.Text;
        SearchList.ItemsSource = null;
        SearchOutput.Clear();
        await (_ragTask = RunUserActionAsync(async () =>
        {
            SearchStatus.Text = "検索と機密検査を実行しています。取り込み中はその完了を待ちます…";
            var results = await _rag.SearchAsync(query, 5, _ragCancellation!.Token);
            SearchList.ItemsSource = results.Select(r => new SearchRow(r.DocumentId, r.SourcePath ?? $"存在のみ：{r.DocumentId}（{r.Decision}）")).ToArray();
            SearchOutput.Text = string.Join("\n\n---\n\n", results.Select(Describe));
            SearchStatus.Text = results.Count == 0 ? "現在の設定と元ファイルに一致する検索結果はありません。" : $"{results.Count}件の結果です。";
        }));
    }

    private async void FetchText(object sender, RoutedEventArgs e)
    {
        if (_rag is null || _ragChanging || _shuttingDown || !_ragTask.IsCompleted || SearchList.SelectedItem is not SearchRow row) return;
        SearchOutput.Clear();
        await (_ragTask = RunUserActionAsync(async () =>
        {
            var result = await _rag.FetchAsync(row.DocumentId, _ragCancellation!.Token);
            SearchOutput.Text = result is null ? "文書が変更・削除されたか、現在の設定では取得できません。" : Describe(result);
        }));
    }

    private static string Describe(TextSearchResult result) => result.Content is null
        ? $"文書ID: {result.DocumentId}\n存在のみ通知（{result.Decision}）。本文・出典は表示しません。"
        : $"{result.SourcePath}\n文書ID: {result.DocumentId} ／ {result.Decision}" +
            (result.StartParagraph is null ? "\n" : $" ／ 段落 {result.StartParagraph}～{result.EndParagraph}\n") + result.Content;

    private async Task RunUserActionAsync(Func<Task> action)
    {
        SearchButton.IsEnabled = FetchButton.IsEnabled = ScanButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { SearchStatus.Text = "処理を中断しました。"; }
        catch (Exception ex) { SearchStatus.Text = SafeMessage(ex); }
        finally
        {
            SearchButton.IsEnabled = FetchButton.IsEnabled = !_shuttingDown;
            ScanButton.IsEnabled = !_shuttingDown && _monitorTask.IsCompleted;
        }
    }

    private async Task StopRagAsync()
    {
        _ragCancellation?.Cancel();
        _monitorCancellation?.Cancel();
        if (_mcp is not null) { await _mcp.DisposeAsync(); _mcp = null; }
        McpStatus.Text = "MCPは停止しています。";
        await Task.WhenAll(_monitorTask, _ragTask);
        _rag?.Dispose(); _rag = null;
        _persistentMonitor = null;
        _ragAi?.Dispose(); _ragAi = null;
        if (_repository is not null) { await _repository.DisposeAsync(); _repository = null; }
        _ragCancellation?.Dispose(); _ragCancellation = null;
    }
}
