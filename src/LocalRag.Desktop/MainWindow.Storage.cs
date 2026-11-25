using System.Windows;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private async Task ShowStorageHintAsync()
    {
        if (_server.Status?.ManagedByApplication != true || _loadedSettings.Database.UsesExternalEndpoint) return;
        try
        {
            if (_server.HasCompactBackup)
            {
                DatabaseStatus.Text += " 退避した旧DBがあります。「取り込み」で整理を完了できます。";
                return;
            }
            var bytes = await Task.Run(() => new DirectoryInfo(Path.Combine(_dataDirectory, "server", "data", "vlog"))
                .EnumerateFiles().Sum(f => f.Length));
            // Physical length is not reclaimable length; don't promise savings until the copy is verified.
            if (bytes >= 8L * 1024 * 1024 * 1024)
            {
                var hint = $"DBの値ログは {bytes / 1073741824d:N1} GiB です。「取り込み」の「DBを整理する」で容量を見直せます。";
                DatabaseStatus.Text += " " + hint;
                StorageStatusText.Text = hint;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Size is advisory only. */ }
    }

    private void RefreshStorageButtons()
    {
        var owned = !_loadedSettings.Database.UsesExternalEndpoint && _server.Status?.ManagedByApplication == true;
        var enabled = owned && !_resettingData && !_shuttingDown;
        try
        {
            var pending = owned && _server.HasCompactBackup;
            CompactStorageButton.IsEnabled = enabled && !pending;
            ReleaseStorageButton.IsEnabled = enabled && pending;
            ReleaseStorageButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (pending && string.IsNullOrWhiteSpace(StorageStatusText.Text))
                StorageStatusText.Text = "整理前のDBを退避しています。検索結果を確認後、「退避した旧DBを削除」でSSDの空きを回収できます。";
        }
        catch (Exception ex)
        {
            CompactStorageButton.IsEnabled = ReleaseStorageButton.IsEnabled = false;
            StorageStatusText.Text = SafeMessage(ex);
        }
    }
    private async void CompactStorageClick(object sender, RoutedEventArgs e) => await RequestStorageWorkAsync(false);
    private async void ReleaseStorageClick(object sender, RoutedEventArgs e) => await RequestStorageWorkAsync(true);
    private async Task RequestStorageWorkAsync(bool release)
    {
        if (_shuttingDown || _resettingData || _ragChanging || !_startupTask.IsCompleted || !_saveTask.IsCompleted ||
            !_databaseTask.IsCompleted || !_aiTask.IsCompleted || _server.Status?.ManagedByApplication != true) return;
        var question = release
            ? "整理後のDBで検索・全文取得できることを確認しましたか？ 整理前に退避した旧DBを完全に削除してディスクの空きを回収します。旧DBには戻せなくなります。"
            : "取り込み・検索を一時停止し、既存の索引からローカルDBを作り直します。他のアプリからのDB利用も終了してください。元ファイルの再取り込みやAIの再実行は不要です。\n\n新しいDB用の一時的な空き容量が必要です。検証後も旧DBを退避し、削除は検索結果を確認してから行えます。開始しますか？";
        if (MessageBox.Show(this, question, "DBの容量整理", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await (_resetTask = StorageWorkAsync(release));
    }
    private async Task StorageWorkAsync(bool release)
    {
        var monitoring = _monitoringEnabled;
        _resettingData = true;
        RefreshStorageButtons();
        ResetIngestionButton.IsEnabled = SaveButton.IsEnabled = SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = false;
        var settings = _loadedSettings.Database with { Port = _localDbPort };
        try
        {
            if (release)
            {
                StorageStatusText.Text = "退避した旧DBを削除しています…";
                await _server.ReleaseCompactBackupAsync(settings, _lifetime.Token);
                StorageStatusText.Text = "退避した旧DBを削除し、空き容量を回収しました。";
                return;
            }
            await _ragConfiguration.WaitAsync();
            _ragChanging = true;
            RagPanel.IsEnabled = SearchPanel.IsEnabled = false;
            try
            {
                StorageStatusText.Text = "取り込み・検索を停止しています…";
                await StopRagAsync();
                var result = await _server.CompactStorageAsync(settings, _loadedSettings.DataSourceName,
                    new Progress<string>(s => StorageStatusText.Text = s), _lifetime.Token);
                static string GiB(long bytes) => (bytes / 1073741824d).ToString("N2") + " GiB";
                StorageStatusText.Text = result.BackupPending
                    ? $"DB本体：{GiB(result.PreviousBytes)} → {GiB(result.CurrentBytes)}。検索結果を確認してから「退避した旧DBを削除」で空きを回収してください。"
                    : "整理してもDBが小さくならなかったため、元のDBを使用します。";
            }
            finally { _ragChanging = false; _ragConfiguration.Release(); }
        }
        catch (OperationCanceledException) { StorageStatusText.Text = "DBの整理を中断しました。"; }
        catch (Exception ex) { StorageStatusText.Text = "DBの整理を完了できませんでした。" + (ex is IOException ? ex.Message : SafeMessage(ex)); }
        finally
        {
            if (!release && !_shuttingDown) await ReconfigureRagAsync(startMonitoring: monitoring);
            _resettingData = false;
            ResetIngestionButton.IsEnabled = !_shuttingDown && _server.Connection is not null;
            SaveButton.IsEnabled = SettingsPanel.IsEnabled = AiButton.IsEnabled = DbButton.IsEnabled = !_shuttingDown && _settingsLoaded;
            RefreshStorageButtons();
            UpdateApplicationStatus();
        }
    }
}
