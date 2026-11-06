using System.Windows;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Desktop;

public partial class MainWindow
{
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
    private bool _monitoringEnabled;
    private bool _manualScanRunning;
    private Task _scanTask = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private readonly SemaphoreSlim _ragConfiguration = new(1, 1);

    private async Task ReconfigureRagAsync(bool refreshAi = false, bool? startMonitoring = null, bool reconnectDatabase = false)
    {
        await _ragConfiguration.WaitAsync();
        var resumeMonitoring = startMonitoring ?? _monitoringEnabled;
        _ragChanging = true;
        RagPanel.IsEnabled = SearchPanel.IsEnabled = false;
        SearchList.ItemsSource = null;
        SearchOutput.Clear();
        SearchStatus.Text = "";
        try
        {
            await StopRagAsync();
            if (reconnectDatabase && !_shuttingDown)
            {
                await _server.ShutdownAsync(_loadedSettings.Database.StopOnExit);
                await (_databaseTask = StartServerAsync());
            }
            if (refreshAi && !_shuttingDown) await (_aiTask = RefreshModelsAsync(_loadedSettings.Ai));
            SearchList.ItemsSource = null;
            SearchOutput.Clear();
            SearchStatus.Text = "";
            if (_shuttingDown || _lifetime.IsCancellationRequested) return;
            if (_server.Connection is null || _loadedSettings.Folders.Count == 0 ||
                string.IsNullOrWhiteSpace(_loadedSettings.Ai.ChatModel) || string.IsNullOrWhiteSpace(_loadedSettings.Ai.EmbeddingModel))
            {
                _operationError = "DB接続とフォルダ・言語モデル・Embeddingモデルの設定を確認してください。";
                return;
            }
            _repository = await TextRagRepository.OpenAsync(_server.Connection, _lifetime.Token);
            _ragCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ragAi = new LemonadeTextAi(_loadedSettings);
            _rag = new TextRagService(_repository, _ragAi, _loadedSettings, _dataDirectory);
            AttachIngestionActivity(_rag);
            _persistentMonitor = new PersistentMonitor(_repository, MonitoringCheckpoint.ScopeFor(_loadedSettings));
            ShowDocuments(await _repository.DocumentStatusesAsync(_lifetime.Token));
            await RefreshHistoryAsync(_rag);
            RagPanel.IsEnabled = SearchPanel.IsEnabled = true;
            _operationError = null;
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
            if (resumeMonitoring) StartMonitoring(immediate: !_starting);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _operationError = SafeMessage(ex); }
        finally
        {
            _ragChanging = false; _ragConfiguration.Release(); UpdateApplicationStatus();
            ResetIngestionButton.IsEnabled = !_shuttingDown && !_resettingData && _server.Connection is not null;
        }
    }

    private void StartMonitoring(bool immediate = true)
    {
        if (_rag is null || !_monitorTask.IsCompleted) return;
        _monitoringEnabled = true;
        _operationError = null;
        PauseButton.Content = "監視停止";
        _monitorCancellation?.Dispose();
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_ragCancellation!.Token);
        _monitorTask = MonitorAsync(_monitorCancellation.Token, immediate);
        UpdateApplicationStatus();
    }

    private async Task MonitorAsync(CancellationToken token, bool immediate)
    {
        try
        {
            await _rag!.RunMonitoringAsync(_persistentMonitor!, null, async result =>
            {
                var documents = await _repository!.DocumentStatusesAsync(token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowDocuments(documents);
                    _operationError = null;
                    UpdateApplicationStatus();
                });
            }, ex => Dispatcher.InvokeAsync(() =>
            {
                _operationError = $"ファイル確認に失敗しました（{ex.GetType().Name}）。次回に再試行します。";
                UpdateApplicationStatus();
            }).Task, due => Dispatcher.InvokeAsync(() =>
            {
                UpdateApplicationStatus();
            }).Task, token, immediate);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _operationError = $"監視を停止しました（{ex.GetType().Name}）。接続と設定を確認してください。"; }
        finally { _monitoringEnabled = false; PauseButton.Content = "監視再開"; UpdateApplicationStatus(); }
    }

    private async Task RunCycleAsync(CancellationToken token)
    {
        await _rag!.ScanAsync(null, token).ConfigureAwait(false);
        var documents = await _repository!.DocumentStatusesAsync(token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            ShowDocuments(documents);
        });
    }

    private void ShowDocuments(IReadOnlyList<TextDocumentState> documents)
    {
        DocumentStatusList.ItemsSource = documents.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(d => $"{d.Path} — {PipelineErrors.Status(d.Status, d.WithdrawalReason)}" +
            (ProcessingPlan.Current(_loadedSettings, d) || d.Status == "deleted" ? "" : "（旧設定の記録・再処理待ち）") +
            (d.ErrorCode is null ? "" : $"（{PipelineErrors.Describe(d.ErrorStage, d.ErrorDetailCode)}）") +
            (d.ProcessingNotices.Count == 0 ? "" : " / " + string.Join("、", d.ProcessingNotices.Select(PipelineErrors.Notice)))).ToArray();
    }

    private async void PauseMonitoring(object sender, RoutedEventArgs e)
    {
        if (_ragChanging || _shuttingDown) return;
        PauseButton.IsEnabled = false;
        try
        {
            if (_monitoringEnabled)
            {
                _monitoringEnabled = false;
                _monitorCancellation?.Cancel();
                await _monitorTask;
            }
            else
            {
                await _scanTask;
                if (!_shuttingDown && !_ragChanging) StartMonitoring();
            }
        }
        finally { PauseButton.IsEnabled = !_shuttingDown; UpdateApplicationStatus(); }
    }

    private async void ScanNow(object sender, RoutedEventArgs e)
    {
        if (_rag is null || _ragChanging || _shuttingDown || !_refreshTask.IsCompleted) return;
        await (_refreshTask = RefreshOrScanAsync());
    }

    private async Task RefreshOrScanAsync()
    {
        ScanButton.IsEnabled = false;
        try
        {
            if (!await _rag!.RefreshFileListAsync(_ragCancellation!.Token))
            {
                await _scanTask;
                if (!_shuttingDown && !_ragChanging) _scanTask = RunManualScanAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _operationError = SafeMessage(ex); }
        finally { ScanButton.IsEnabled = !_shuttingDown; UpdateApplicationStatus(); }
    }

    private async Task RunManualScanAsync()
    {
        _manualScanRunning = true;
        _operationError = null;
        UpdateApplicationStatus();
        try { await _persistentMonitor!.RunCycleAsync(RunCycleAsync, _ragCancellation!.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _operationError = SafeMessage(ex); }
        finally { _manualScanRunning = false; UpdateApplicationStatus(); }
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
            SearchList.ItemsSource = results.Select(r => new SearchRow(r.DocumentId,
                r.Content is null ? $"{r.SourcePath ?? r.DocumentId.ToString()}（本文非開示：{r.Decision}）" : r.SourcePath!)).ToArray();
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
        ? $"文書ID: {result.DocumentId}\n本文は非開示です（{result.Decision}）。" +
            (result.SourcePath is null ? "\n原本のパスは取得できません。" : $"\n原本のパス: {result.SourcePath}\n内容を確認する場合は、このパスをコピーしてエクスプローラーから開いてください。")
        : $"{result.SourcePath}\n文書ID: {result.DocumentId} ／ {result.Decision}" +
            (result.StartParagraph is null ? "\n" : $" ／ 段落 {result.StartParagraph}～{result.EndParagraph}\n") +
            (result.ParentChunkId is null ? "" : $"親チャンク: {result.SectionTitle ?? "文書全体"}\n") +
            (result.Ranking is null ? "" : $"構造検索スコア: {result.Ranking.Combined:F3} ／ 返却単位: {result.ResultUnitKind}" +
                (result.Ranking.ExpansionLimited ? "（近傍の一部のみ評価）" : "") + "\n") + result.Content;

    private async Task RunUserActionAsync(Func<Task> action)
    {
        SearchButton.IsEnabled = FetchButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { SearchStatus.Text = "処理を中断しました。"; }
        catch (Exception ex) { SearchStatus.Text = SafeMessage(ex); }
        finally
        {
            SearchButton.IsEnabled = FetchButton.IsEnabled = !_shuttingDown;
        }
    }

    private async Task StopRagAsync()
    {
        _ragCancellation?.Cancel();
        _monitorCancellation?.Cancel();
        if (_mcp is not null) { await _mcp.DisposeAsync(); _mcp = null; }
        McpStatus.Text = "MCPは停止しています。";
        await Task.WhenAll(_monitorTask, _ragTask, _refreshTask, _scanTask);
        _monitoringEnabled = false;
        _currentFile = null;
        PauseButton.Content = "監視再開";
        _rag?.Dispose(); _rag = null;
        _persistentMonitor = null;
        _ragAi?.Dispose(); _ragAi = null;
        await _historyRefresh.WaitAsync();
        try { if (_repository is not null) { await _repository.DisposeAsync(); _repository = null; } }
        finally { _historyRefresh.Release(); }
        _ragCancellation?.Dispose(); _ragCancellation = null;
    }
}
