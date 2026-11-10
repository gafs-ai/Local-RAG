using System.Threading.Channels;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private readonly SemaphoreSlim _scans = new(1, 1);
    private readonly SemaphoreSlim _discoveries = new(1, 1);
    private readonly object _queueSync = new();
    private FileWorkQueue? _activeQueue;
    private Action? _wakeQueue;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Refresh waiting files without waiting for or cancelling an active AI operation.</summary>
    public async Task<bool> RefreshFileListAsync(CancellationToken token = default)
    {
        await _discoveries.WaitAsync(token).ConfigureAwait(false);
        try
        {
            FileWorkQueue? queue;
            lock (_queueSync) queue = _activeQueue;
            if (queue is null) return false;
            var discovery = await DiscoverAsync(token).ConfigureAwait(false);
            lock (_queueSync)
            {
                if (!ReferenceEquals(queue, _activeQueue)) return false;
                queue.Replace(discovery);
                ShowQueue(queue);
                _wakeQueue?.Invoke();
            }
            return true;
        }
        finally { _discoveries.Release(); }
    }

    private void SetActiveQueue(FileWorkQueue? queue, Action? wake = null)
    {
        lock (_queueSync) { _activeQueue = queue; _wakeQueue = wake; }
    }

    private async Task<FileDiscovery> DiscoverAsync(CancellationToken token)
    {
        var checkedAt = _clock.GetUtcNow();
        var discovery = await Task.Run(() => FileDiscoveryPolicy.Select(
            TextFiles.Scan(settings.Folders, dataDirectory, token, settings.AllowedTextExtensions(), settings.ExcludeDotHiddenEntries, settings.GlobalIgnorePatterns),
            settings.Folders, checkedAt, settings.UpdateExclusionMinutes), token).ConfigureAwait(false);
        var failures = await repository.FailuresAsync(token).ConfigureAwait(false);
        return discovery with { Eligible = discovery.Eligible.Where(file => !failures.TryGetValue(file.Path, out var failure) ||
            IngestionRetry.CanAttempt(failure, IngestionRetry.ConfigurationId(settings, file.Path), file.LastWriteUtc, file.Length, checkedAt)).ToArray() };
    }

    public async Task<TextCycleResult> ScanAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        await _scans.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            var queue = new FileWorkQueue();
            SetActiveQueue(queue);
            await RefreshFileListAsync(token).ConfigureAwait(false);
            var result = new TextCycleResult(0, 0, 0, 0, 0, 0, 0);
            while (true)
            {
                var discovery = queue.TakeReconciliation();
                if (discovery is not null) result = Add(result, await ReconcileAsync(discovery, token).ConfigureAwait(false));
                await _operations.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    FileWorkItem? file;
                    lock (_queueSync)
                    {
                        // A refresh may have arrived while reconciliation waited for the AI lane.
                        if (queue.HasReconciliation) continue;
                        file = queue.Take();
                        if (file is null) { _activeQueue = null; break; }
                    }
                    ShowQueue(queue);
                    var remember = false;
                    try
                    {
                        var processed = await ProcessFileAsync(file, progress, token, laneHeld: true).ConfigureAwait(false);
                        remember = processed.Failed == 0 && processed.Deferred == 0 && processed.RetrySuppressed == 0;
                        result = Add(result, processed);
                    }
                    finally { queue.Complete(remember); ShowQueue(queue); }
                }
                finally { _operations.Release(); }
            }
            return result;
        }
        finally { SetActiveQueue(null); ChangeActivity(null, "待機中", []); _scans.Release(); }
    }

    public async Task RunMonitoringAsync(PersistentMonitor monitor, IProgress<string>? progress,
        Func<TextCycleResult, Task> onResult, Func<Exception, Task> onFailure, Func<DateTimeOffset, Task> onWaiting, CancellationToken token, bool immediate = false)
    {
        await _scans.WaitAsync(token).ConfigureAwait(false);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            RequireModels();
            var queue = new FileWorkQueue();
            var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            SetActiveQueue(queue, () => wake.Writer.TryWrite(true));
            async Task NotifyResult(TextCycleResult result)
            {
                try { await onResult(result).ConfigureAwait(false); }
                catch (Exception ex) when (!stopping.IsCancellationRequested && MonitoringRecovery.IsTransient(ex))
                {
                    // Refreshing the UI is independent from processing the next file.
                    OperationalDiagnostics.Write(dataDirectory, "monitor.result-refresh", ex);
                    await onFailure(ex).ConfigureAwait(false);
                }
            }
            async Task Work()
            {
                var stage = "monitor.waiting";
                try
                {
                    while (await wake.Reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
                    {
                        while (wake.Reader.TryRead(out _)) { }
                        while (true)
                        {
                            stopping.Token.ThrowIfCancellationRequested();
                            stage = "monitor.reconcile";
                            var discovery = queue.TakeReconciliation();
                            if (discovery is not null) await NotifyResult(await ReconcileAsync(discovery, stopping.Token).ConfigureAwait(false)).ConfigureAwait(false);
                            // Select only after the AI lane is available, so waiting behind a search
                            // does not prematurely pin a lower-priority file as already running.
                            await _operations.WaitAsync(stopping.Token).ConfigureAwait(false);
                            TextCycleResult result;
                            try
                            {
                                var file = queue.Take();
                                if (file is null) break;
                                ShowQueue(queue);
                                var remember = false;
                                try
                                {
                                    stage = "monitor.file";
                                    result = await ProcessFileAsync(file, progress, stopping.Token, laneHeld: true).ConfigureAwait(false);
                                    remember = result.Failed == 0 && result.Deferred == 0 && result.RetrySuppressed == 0;
                                }
                                finally { queue.Complete(remember); ShowQueue(queue); }
                            }
                            finally { _operations.Release(); }
                            stage = "monitor.result";
                            await NotifyResult(result).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping.IsCancellationRequested) OperationalDiagnostics.Write(dataDirectory, stage, ex);
                    stopping.Cancel(); throw;
                }
            }
            var worker = Work();
            var checks = monitor.RunAsync(async ct =>
            {
                // Filesystem discovery never waits for the AI/database operation lane.
                await RefreshFileListAsync(ct).ConfigureAwait(false);
                progress?.Report($"チェック完了：処理待ち {queue.Count}。実行中のファイルは継続します。");
            }, TimeSpan.FromMinutes(settings.MonitoringIntervalMinutes), onFailure, onWaiting, stopping.Token, immediate);
            await Task.WhenAny(worker, checks).ConfigureAwait(false);
            stopping.Cancel();
            try { await Task.WhenAll(worker, checks).ConfigureAwait(false); }
            catch
            {
                if (checks.IsFaulted)
                {
                    OperationalDiagnostics.Write(dataDirectory, "monitor.discovery", checks.Exception!.GetBaseException());
                    await checks.ConfigureAwait(false);
                }
                throw;
            }
        }
        finally { SetActiveQueue(null); ChangeActivity(null, "待機中", []); _scans.Release(); }
    }

    private async Task<TextCycleResult> ReconcileAsync(FileDiscovery discovery, CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var seen = discovery.Scan.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignore = new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns);
            var deleted = 0;
            foreach (var old in (await repository.DocumentsAsync(token).ConfigureAwait(false)).Where(d =>
                (d.Status != "deleted" || d.WithdrawalReason is null) && !seen.Contains(d.Path)))
            {
                var stillConfigured = settings.Folders.Any(f => TextFiles.InFolder(old.Path, f));
                string? reason = stillConfigured ? null : "outside_roots";
                if (stillConfigured)
                {
                    try
                    {
                        stillConfigured = !ignore.IsIgnored(old.Path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries);
                        if (!stillConfigured) reason = "ignored";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                }
                if (stillConfigured && (!discovery.Scan.CompletedRoots.Any(f => TextFiles.InFolder(old.Path, f)) ||
                    File.Exists(old.Path) && settings.AllowedTextExtensions().Contains(Path.GetExtension(old.Path), StringComparer.OrdinalIgnoreCase))) continue;
                await repository.DeactivateAsync(old, token).ConfigureAwait(false);
                reason ??= File.Exists(old.Path) ? "extension_disabled" : "source_missing";
                await repository.SaveAsync(old with { Status = "deleted", WithdrawalReason = reason,
                    ErrorCode = null, ErrorStage = null, ErrorDetailCode = null, Failure = null }, token).ConfigureAwait(false);
                if (old.Status != "deleted")
                {
                    deleted++;
                    var now = _clock.GetUtcNow();
                    await repository.RecordHistoryAsync(new(Guid.NewGuid(), now, now, old.Path, "deleted", WithdrawalReason: reason,
                        ConfigurationId: _configuration), token).ConfigureAwait(false);
                    HistoryChanged?.Invoke();
                }
            }
            return new(0, 0, 0, 0, deleted, discovery.Scan.FailedRoots, discovery.Deferred);
        }
        finally { _operations.Release(); }
    }

    private static TextCycleResult Add(TextCycleResult a, TextCycleResult b) => new(a.Indexed + b.Indexed, a.Excluded + b.Excluded,
        a.Unchanged + b.Unchanged, a.Failed + b.Failed, a.Deleted + b.Deleted, a.InaccessibleRoots + b.InaccessibleRoots, a.Deferred + b.Deferred,
        a.RetrySuppressed + b.RetrySuppressed);
}
