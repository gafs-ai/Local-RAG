using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private readonly UsageStatisticsAccumulator _usageStatistics = new();
    private readonly SemaphoreSlim _usageStatisticsStorage = new(1, 1);
    private CancellationTokenSource? _usageStatisticsCancellation;
    private Task _usageStatisticsTask = Task.CompletedTask;
    private bool _usageStatisticsUiLoading;
    private UsageStatisticsHardwareInput? _usageStatisticsHardware;
    private UsageStatisticsSender? _usageStatisticsSender;
    private bool _usageStatisticsStateLoaded;
    private string UsageSchedulePath => System.IO.Path.Combine(_dataDirectory, "usage-statistics-attempt.json");
    private string UsageContextPath => System.IO.Path.Combine(_dataDirectory, "usage-statistics-context.txt");
    private sealed record UsageSchedule(DateTimeOffset LastAttemptUtc);
    private sealed record UsageTransportConfig(string Endpoint, string ReleaseKeyId, string SigningKeyBase64);

    private async Task InitializeUsageStatisticsAsync()
    {
        var preference = _loadedSettings.UsageStatistics;
        if (!preference.NoticeAcknowledged)
        {
            var notice = new UsageStatisticsNoticeWindow { Owner = this };
            var confirmed = notice.ShowDialog() == true;
            preference = preference with { Enabled = confirmed && notice.StatisticsEnabled, NoticeAcknowledged = true };
            var settings = _loadedSettings with { UsageStatistics = preference };
            try { await _settingsStore.SaveAsync(settings, _lifetime.Token); _loadedSettings = settings; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Never enable collection when the notice decision could not be saved.
                _loadedSettings = _loadedSettings with { UsageStatistics = new() { Enabled = false } };
            }
        }
        _usageStatisticsSender = LoadUsageStatisticsSender();
        RefreshUsageStatisticsConfiguration();
        if (_loadedSettings.UsageStatistics is not { Enabled: true, NoticeAcknowledged: true })
            try { await ClearUsageStatisticsAsync(); } catch { UsageStatisticsStatus.Text = "利用統計：停止中。未送信分を削除できませんでした。保存先を確認してください。"; }
    }

    private static UsageStatisticsSender? LoadUsageStatisticsSender()
    {
        try
        {
            var settings = LoadUsageStatisticsTransportSettings();
            return settings is null ? null : new UsageStatisticsSender(settings);
        }
        catch { return null; }
    }

    private static UsageStatisticsTransportSettings? LoadUsageStatisticsTransportSettings()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalRag.UsageStatisticsTransport.json");
            if (stream is null || stream.Length > 4096) return null;
            var config = JsonSerializer.Deserialize<UsageTransportConfig>(stream, UsageStatisticsPrivacy.JsonOptions);
            if (config is null) return null;
            return new(new Uri(config.Endpoint, UriKind.Absolute), config.ReleaseKeyId,
                Convert.FromBase64String(config.SigningKeyBase64));
        }
        catch { return null; } // Missing/invalid release configuration is an inert, fail-closed build.
    }

    private void RefreshUsageStatisticsConfiguration()
    {
        _usageStatisticsUiLoading = true;
        try { UsageStatisticsBox.IsChecked = _loadedSettings.UsageStatistics.Enabled; }
        finally { _usageStatisticsUiLoading = false; }
        _usageStatistics.Configure(_loadedSettings.UsageStatistics);
        if (_loadedSettings.Ai.UsesExternalInference())
        {
            StopUsageStatistics();
            _usageStatistics.Configure(new() { Enabled = false, NoticeAcknowledged = true });
            UsageStatisticsStatus.Text = "利用統計：外部AI接続では処理速度をこのPCの機材に対応付けられないため、自動収集・送信を停止します。";
            return;
        }
        if (_loadedSettings.UsageStatistics is not { Enabled: true, NoticeAcknowledged: true })
        {
            StopUsageStatistics();
            UsageStatisticsStatus.Text = "利用統計：停止中（未送信分は削除します）。";
            return;
        }
        UsageStatisticsStatus.Text = _usageStatisticsSender is null
            ? "利用統計：送信先の設定待ち（まだ送信されません）。"
            : "利用統計：有効。処理件数があるとき、1日に最大1回送信します。";
        if (_usageStatisticsTask.IsCompleted)
        {
            _usageStatisticsCancellation?.Dispose();
            _usageStatisticsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _usageStatisticsTask = RunUsageStatisticsAsync(_usageStatisticsCancellation.Token);
        }
    }

    private async void UsageStatisticsChanged(object sender, RoutedEventArgs e)
    {
        if (_usageStatisticsUiLoading || !_settingsLoaded || _shuttingDown || !_saveTask.IsCompleted) return;
        var enabled = UsageStatisticsBox.IsChecked == true;
        if (!enabled)
        {
            StopUsageStatistics(); // Immediate cancellation, even while saving is pending or fails.
            _usageStatistics.Configure(new() { Enabled = false, NoticeAcknowledged = true });
        }
        await (_saveTask = SaveUsageStatisticsPreferenceAsync(enabled));
    }

    private async Task SaveUsageStatisticsPreferenceAsync(bool enabled)
    {
        SaveButton.IsEnabled = SettingsPanel.IsEnabled = false;
        try
        {
            await _usageStatisticsTask;
            if (!enabled) await ClearUsageStatisticsAsync();
            var settings = _loadedSettings with { UsageStatistics = new() { Enabled = enabled, NoticeAcknowledged = true } };
            await _settingsStore.SaveAsync(settings, _lifetime.Token);
            _loadedSettings = settings;
            RefreshUsageStatisticsConfiguration();
            SettingsStatus.Text = enabled ? "利用統計の設定を保存しました。" : "利用統計をOFFにしました。";
        }
        catch (OperationCanceledException) { }
        catch
        {
            StopUsageStatistics();
            _loadedSettings = _loadedSettings with { UsageStatistics = new() { Enabled = false, NoticeAcknowledged = true } };
            RefreshUsageStatisticsConfiguration();
            SettingsStatus.Text = "利用統計を停止しましたが、設定の保存に失敗しました。再起動前に保存先を確認し、設定を保存してください。";
        }
        finally { SaveButton.IsEnabled = SettingsPanel.IsEnabled = !_shuttingDown; }
    }

    private async Task RunUsageStatisticsAsync(CancellationToken token)
    {
        try
        {
            await _usageStatisticsStorage.WaitAsync(token);
            try
            {
                var context = UsageStatisticsContext(_loadedSettings);
                var previousContext = System.IO.File.Exists(UsageContextPath) && new System.IO.FileInfo(UsageContextPath).Length <= 128
                    ? (await System.IO.File.ReadAllTextAsync(UsageContextPath, token)).Trim() : "";
                var store = new UsageStatisticsLocalStore(_dataDirectory);
                if (previousContext != context)
                {
                    _usageStatistics.Clear();
                    await store.ClearAsync(token);
                    await System.IO.File.WriteAllTextAsync(UsageContextPath + ".tmp", context, token);
                    token.ThrowIfCancellationRequested();
                    System.IO.File.Move(UsageContextPath + ".tmp", UsageContextPath, true);
                    _usageStatisticsStateLoaded = true;
                }
                else if (!_usageStatisticsStateLoaded)
                {
                    await store.LoadAsync(_usageStatistics, token);
                    _usageStatisticsStateLoaded = true;
                }
            }
            finally { _usageStatisticsStorage.Release(); }
            _usageStatisticsHardware ??= await UsageStatisticsHardwareDetector.DetectAsync(token);
            // Notice acknowledgment never triggers an immediate background transmission.
            await Task.Delay(TimeSpan.FromMinutes(5), token);
            while (!token.IsCancellationRequested)
            {
                await PersistUsageStatisticsAsync(token);
                if (_usageStatisticsSender is not null)
                {
                    var lastAttempt = await ReadUsageStatisticsAttemptAsync(token);
                    if (DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromDays(1))
                    {
                        var batch = _usageStatistics.Capture(_loadedSettings, ProductInfo.Version, _usageStatisticsHardware);
                        if (batch is not null)
                        {
                            if (_usageStatisticsSender.IsExpired(batch))
                            {
                                _usageStatistics.Acknowledge(batch);
                                await PersistUsageStatisticsAsync(token);
                                continue;
                            }
                            // Persist the captured body and report nonce before transmission so a
                            // lost acknowledgment can be retried after restart without double counting.
                            await PersistUsageStatisticsAsync(token);
                            // Write the rate-limit state before the request, also covering crashes/restarts.
                            await WriteUsageStatisticsAttemptAsync(token);
                            if (await _usageStatisticsSender.SendAsync(batch, token))
                            {
                                _usageStatistics.Acknowledge(batch);
                                await PersistUsageStatisticsAsync(token);
                                UsageStatisticsStatus.Text = "利用統計：送信済み。次回は24時間以降です。";
                            }
                            else UsageStatisticsStatus.Text = "利用統計：送信できませんでした。24時間以降に再試行します。取り込みは継続します。";
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!_shuttingDown) UsageStatisticsStatus.Text = "利用統計：保存または送信の準備に失敗しました。取り込みは継続します。";
        }
    }

    private async Task PersistUsageStatisticsAsync(CancellationToken token)
    {
        await _usageStatisticsStorage.WaitAsync(token);
        try { await new UsageStatisticsLocalStore(_dataDirectory).SaveAsync(_usageStatistics, token); }
        finally { _usageStatisticsStorage.Release(); }
    }

    private async Task ClearUsageStatisticsAsync()
    {
        await _usageStatisticsStorage.WaitAsync();
        try
        {
            _usageStatistics.Clear();
            await new UsageStatisticsLocalStore(_dataDirectory).ClearAsync();
            _usageStatisticsStateLoaded = true;
        }
        finally { _usageStatisticsStorage.Release(); }
    }

    private async Task<DateTimeOffset> ReadUsageStatisticsAttemptAsync(CancellationToken token)
    {
        if (!System.IO.File.Exists(UsageSchedulePath)) return DateTimeOffset.MinValue;
        var info = new System.IO.FileInfo(UsageSchedulePath);
        if (info.Length > 1024) throw new InvalidDataException("Invalid statistics schedule.");
        var schedule = JsonSerializer.Deserialize<UsageSchedule>(await System.IO.File.ReadAllTextAsync(UsageSchedulePath, token));
        return schedule?.LastAttemptUtc ?? throw new InvalidDataException("Invalid statistics schedule.");
    }

    private async Task WriteUsageStatisticsAttemptAsync(CancellationToken token)
    {
        var temp = UsageSchedulePath + ".tmp";
        await System.IO.File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new UsageSchedule(DateTimeOffset.UtcNow)), token);
        token.ThrowIfCancellationRequested();
        System.IO.File.Move(temp, UsageSchedulePath, true);
    }

    private void StopUsageStatistics() => _usageStatisticsCancellation?.Cancel();

    private static string UsageStatisticsContext(AppSettings settings) => TextRagSettings.Hash("hardware-catalog-v2\n" + ProductInfo.Version + "\n" +
        settings.PipelineConfigurationId() + "\n" + JsonSerializer.Serialize(UsageStatisticsPrivacy.Configuration(settings), UsageStatisticsPrivacy.JsonOptions));

    private async Task PrepareUsageStatisticsForSettingsAsync(AppSettings next)
    {
        // These values are compared only on this PC. They are never included in reports.
        if (next.Ai.ChatModel == _loadedSettings.Ai.ChatModel && next.Ai.EmbeddingModel == _loadedSettings.Ai.EmbeddingModel &&
            next.PipelineConfigurationId() == _loadedSettings.PipelineConfigurationId() &&
            JsonSerializer.Serialize(UsageStatisticsPrivacy.Configuration(next), UsageStatisticsPrivacy.JsonOptions) ==
            JsonSerializer.Serialize(UsageStatisticsPrivacy.Configuration(_loadedSettings), UsageStatisticsPrivacy.JsonOptions)) return;
        StopUsageStatistics();
        _usageStatistics.Configure(new() { Enabled = false, NoticeAcknowledged = true });
        await _usageStatisticsTask;
        await ClearUsageStatisticsAsync();
    }

    private async Task CompleteUsageStatisticsShutdownAsync()
    {
        try
        {
            await _usageStatisticsTask;
            if (_loadedSettings.UsageStatistics is { Enabled: true, NoticeAcknowledged: true })
                await PersistUsageStatisticsAsync(CancellationToken.None);
            else await ClearUsageStatisticsAsync();
        }
        catch { } // Telemetry storage must never prevent application shutdown.
    }

    private void PreviewUsageStatistics(object sender, RoutedEventArgs e)
    {
        var report = _usageStatistics.Preview(_loadedSettings, ProductInfo.Version, _usageStatisticsHardware);
        var text = report is null ? "送信対象の統計はありません。OFFの場合や処理がまだない場合は何も送信しません。" :
            JsonSerializer.Serialize(report, new JsonSerializerOptions(UsageStatisticsPrivacy.JsonOptions) { WriteIndented = true });
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(16) };
        new Window { Title = "送信対象の利用統計（本文・パス等は含みません）", Owner = this, Width = 720, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = box }.ShowDialog();
    }
}
