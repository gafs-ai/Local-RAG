using System.Diagnostics;
using System.Windows;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private readonly ReleaseUpdateClient _releaseUpdateClient = new();
    private readonly ReleaseUpdateMonitor _releaseUpdates;
    private CancellationTokenSource? _updateMonitoringCancellation;
    private Task _updateMonitoringTask = Task.CompletedTask;
    private Task _updateManualTask = Task.CompletedTask;
    private ReleaseUpdate? _availableUpdate;

    private async Task ConfigureUpdateMonitoringAsync()
    {
        StopUpdateMonitoring();
        await _updateMonitoringTask;
        if (_shuttingDown) return;
        _updateMonitoringCancellation?.Dispose();
        _updateMonitoringCancellation = null;
        ShowUpdateResult(await _releaseUpdates.CachedAsync(_lifetime.Token));
        CheckUpdatesButton.IsEnabled = _updateManualTask.IsCompleted;
        if (!_loadedSettings.AutomaticUpdateChecks)
        {
            UpdateCheckStatus.Text += " 自動確認はOFFです。";
            return;
        }
        _updateMonitoringCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Do not await this task during initialization: offline update checks must not delay AI/DB startup.
        _updateMonitoringTask = MonitorUpdatesAsync(_updateMonitoringCancellation.Token);
    }

    private void StopUpdateMonitoring() => _updateMonitoringCancellation?.Cancel();

    private async Task MonitorUpdatesAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _releaseUpdates.CheckAsync(manual: false, token);
                token.ThrowIfCancellationRequested();
                ShowUpdateResult(result);
                var delay = _releaseUpdates.NextCheckDelay;
                await Task.Delay(delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Updating is advisory; errors must never stop monitoring or retrieval.
            if (!_shuttingDown) UpdateCheckStatus.Text = "自動更新確認を継続できませんでした。「今すぐ更新を確認」で再確認できます。";
        }
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _shuttingDown || !_updateManualTask.IsCompleted) return;
        await (_updateManualTask = CheckForUpdatesAsync());
    }

    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateCheckStatus.Text = "更新を確認しています…";
        try
        {
            var result = await _releaseUpdates.CheckAsync(manual: true, _lifetime.Token);
            if (!_shuttingDown) ShowUpdateResult(result);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception) { if (!_shuttingDown) UpdateCheckStatus.Text = "更新を確認できませんでした。時間をおいて再確認してください。"; }
        finally { CheckUpdatesButton.IsEnabled = !_shuttingDown; }
    }

    private void ShowUpdateResult(ReleaseUpdateResult result)
    {
        if (_shuttingDown) return;
        _availableUpdate = result.Available;
        UpdateCheckStatus.Text = result.Message;
        UpdateBanner.Visibility = result.Available is null ? Visibility.Collapsed : Visibility.Visible;
        if (result.Available is not { } available) return;
        UpdateBannerText.Text = $"Local RAG バージョン{available.Version}への更新が利用できます。";
        if (!result.Notify) return;
        try
        {
            _tray.ShowBalloonTip(10000, "Local RAGの更新があります",
                $"バージョン{available.Version}が公開されました。管理画面の「更新ページを開く」から更新できます。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { /* The persistent banner remains available when Windows cannot show a popup. */ }
    }

    private void OpenUpdatePage(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _shuttingDown) return;
        try { Process.Start(new ProcessStartInfo(_availableUpdate.Page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { UpdateCheckStatus.Text = "ブラウザを開けませんでした。GitHubのLocal RAGリリースページを開いてください。"; }
    }
}
