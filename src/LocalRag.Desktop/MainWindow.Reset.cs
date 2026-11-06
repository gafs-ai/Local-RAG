using System.Windows;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private Task _resetTask = Task.CompletedTask;
    private bool _resettingData;

    private async void ResetIngestionClick(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || _resettingData || _ragChanging || !_startupTask.IsCompleted || !_saveTask.IsCompleted ||
            !_databaseTask.IsCompleted || !_aiTask.IsCompleted || _server.Connection is null) return;
        var answer = MessageBox.Show(this,
            "取り込んだ文書、すべてのベクトル索引、処理履歴、途中までの処理結果・再開情報を削除します。元に戻せません。\n\n" +
            "元ファイルと設定（監視フォルダ、モデル、プロンプト、カテゴリ、パイプライン、接続情報）は残ります。\n" +
            "取り込み・検索・MCPをいったん停止して初期化し、監視は停止状態にします。再取り込みには「監視再開」または「今すぐ実行」を押してください。\n\n" +
            $"対象DB：{_loadedSettings.Database.Endpoint}\n名前空間：localrag ／ データベース：application\n" +
            (_loadedSettings.Database.UsesExternalEndpoint ? "外部DBを共有している場合、同じLocal RAGデータを使うほかの利用者にも影響します。\n" : "") +
            "\nデータを初期化しますか？",
            "取り込みデータの初期化", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || _shuttingDown) return;
        await (_resetTask = ResetIngestionAsync());
    }

    private async Task ResetIngestionAsync()
    {
        _resettingData = true;
        ResetIngestionButton.IsEnabled = SaveButton.IsEnabled = SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = false;
        DataResetStatus.Text = "取り込みと検索を停止しています…";
        var cleared = false;
        await _ragConfiguration.WaitAsync();
        _ragChanging = true;
        RagPanel.IsEnabled = SearchPanel.IsEnabled = false;
        try
        {
            await StopRagAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            DataResetStatus.Text = "取り込みデータを初期化しています…";
            await using var repository = await TextRagRepository.OpenAsync(_server.Connection ?? throw new InvalidOperationException("DBに接続されていません。"), _lifetime.Token);
            await repository.ResetIngestionDataAsync(_lifetime.Token);
            cleared = true;
            SearchList.ItemsSource = null;
            SearchOutput.Clear(); SearchStatus.Text = "";
            DocumentStatusList.ItemsSource = null;
            IngestionQueueGrid.ItemsSource = IngestionHistoryGrid.ItemsSource = null;
            QueueStatusText.Text = "取り込み待ち：0件";
            HistoryStatusText.Text = "処理履歴：0件";
            CurrentFileText.Text = CurrentStageText.Text = "停止中";
            _currentFile = null;
            _operationError = null;
            DataResetStatus.Text = "取り込みデータを初期化しました。監視は停止しています。再取り込みは「監視再開」または「今すぐ実行」で開始できます。";
        }
        catch (OperationCanceledException)
        {
            DataResetStatus.Text = "初期化処理が中断されました。再接続後に索引と処理履歴の状態を確認してください。";
        }
        catch (Exception ex)
        {
            _operationError = "データを初期化できませんでした。";
            DataResetStatus.Text = "データを初期化できませんでした。" + SafeMessage(ex);
        }
        finally
        {
            _ragChanging = false;
            _ragConfiguration.Release();
        }
        try
        {
            // Recreate the service to discard its queue and caches; never immediately repopulate the cleared index.
            if (!_shuttingDown) await ReconfigureRagAsync(startMonitoring: false);
            if (cleared && _operationError is not null)
                DataResetStatus.Text += " 接続・設定を確認してから再開してください。";
        }
        finally
        {
            _resettingData = false;
            ResetIngestionButton.IsEnabled = !_shuttingDown && _server.Connection is not null;
            SaveButton.IsEnabled = SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = !_shuttingDown && _settingsLoaded;
            UpdateApplicationStatus();
        }
    }
}
