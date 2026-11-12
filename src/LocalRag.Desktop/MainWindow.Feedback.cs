using System.Windows;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private void OpenFeedback(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown) return;
        var existing = OwnedWindows.OfType<FeedbackWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        FeedbackSender? feedbackSender = null;
        try
        {
            var settings = LoadUsageStatisticsTransportSettings();
            if (settings is not null) feedbackSender = new FeedbackSender(settings);
        }
        catch { } // An absent or invalid distribution configuration cannot send feedback.
        new FeedbackWindow(feedbackSender, SnapshotForFeedbackAsync, _lifetime.Token) { Owner = this }.Show();
    }

    private async Task<UsageStatisticsReport> SnapshotForFeedbackAsync(CancellationToken token)
    {
        if (!_settingsLoaded) throw new InvalidOperationException("Settings are not ready.");
        // Manual attachment is independent of automatic sending and never consumes its counters.
        var hardware = _usageStatisticsHardware ?? await UsageStatisticsHardwareDetector.DetectAsync(token);
        token.ThrowIfCancellationRequested();
        return _usageStatistics.SnapshotForFeedback(_loadedSettings, ProductInfo.Version, hardware);
    }

    private async Task CompleteFeedbackShutdownAsync()
    {
        foreach (var window in OwnedWindows.OfType<FeedbackWindow>().ToArray())
            await window.RequestCloseAsync();
    }
}
