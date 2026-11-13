using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private sealed record QueueRow(int Position, int Priority, string Modified, string Path)
    { public string FileName => System.IO.Path.GetFileName(Path); }
    private readonly SemaphoreSlim _historyRefresh = new(1, 1);
    private void AttachIngestionActivity(TextRagService service)
    {
        long lastSequence = -1;
        IReadOnlyList<FileWorkItem>? lastWaiting = null;
        CurrentFileText.Text = "待機中"; CurrentStageText.Text = "待機中";
        _currentFile = null;
        QueueStatusText.Text = "取り込み待ち：0件（探索後に表示）";
        IngestionQueueGrid.ItemsSource = null;
        service.ActivityChanged += activity => Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown || !ReferenceEquals(_rag, service) || activity.Sequence <= lastSequence) return;
            lastSequence = activity.Sequence;
            CurrentFileText.Text = activity.CurrentFile ?? "待機中";
            CurrentStageText.Text = activity.Stage;
            _currentFile = activity.CurrentFile;
            UpdateApplicationStatus();
            if (ReferenceEquals(lastWaiting, activity.Waiting)) return;
            lastWaiting = activity.Waiting;
            QueueStatusText.Text = $"取り込み待ち：{activity.Waiting.Count:N0}件（実行中は含まない・優先度／更新日時順）";
            IngestionQueueGrid.ItemsSource = activity.Waiting.Select((f, i) =>
                new QueueRow(i + 1, f.Priority, f.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"), f.Path)).ToArray();
        });
        service.HistoryChanged += () => Dispatcher.BeginInvoke(async () => await RefreshHistoryAsync(service));
        var collectLocalSpeed = !_loadedSettings.Ai.UsesExternalInference();
        service.HistoryRecorded += entry =>
        {
            if (collectLocalSpeed) _usageStatistics.Record(System.IO.Path.GetExtension(entry.Path), entry.Result, entry.CompletedAtUtc - entry.StartedAtUtc);
        };
    }

    private async Task RefreshHistoryAsync(TextRagService service)
    {
        await _historyRefresh.WaitAsync();
        try
        {
            if (_shuttingDown || !ReferenceEquals(_rag, service) || _repository is null) return;
            var entries = await _repository.HistoryAsync(token: _ragCancellation!.Token);
            if (_shuttingDown || !ReferenceEquals(_rag, service)) return;
            IngestionHistoryGrid.ItemsSource = entries;
            HistoryStatusText.Text = $"処理履歴：{entries.Count:N0}件（完了時刻の新しい順・表示は直近1000件）";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_shuttingDown && ReferenceEquals(_rag, service)) HistoryStatusText.Text = "処理履歴を読み込めません。DB接続を確認してください。"; }
        finally { _historyRefresh.Release(); }
    }
}
