using System.Threading.Channels;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private readonly SemaphoreSlim _scans = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private async Task<FileDiscovery> DiscoverAsync(CancellationToken token)
    {
        var checkedAt = _clock.GetUtcNow();
        return await Task.Run(() => FileDiscoveryPolicy.Select(
            TextFiles.Scan(settings.Folders, dataDirectory, token, settings.AllowedTextExtensions(), settings.ExcludeDotHiddenEntries, settings.GlobalIgnorePatterns),
            settings.Folders, checkedAt, settings.UpdateExclusionMinutes), token).ConfigureAwait(false);
    }

    public async Task<TextCycleResult> ScanAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        await _scans.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            var discovery = await DiscoverAsync(token).ConfigureAwait(false);
            var result = await ReconcileAsync(discovery, token).ConfigureAwait(false);
            foreach (var file in discovery.Eligible)
                result = Add(result, await ProcessFileAsync(file, progress, token).ConfigureAwait(false));
            return result;
        }
        finally { _scans.Release(); }
    }

    public async Task RunMonitoringAsync(PersistentMonitor monitor, IProgress<string>? progress,
        Func<TextCycleResult, Task> onResult, Func<Exception, Task> onFailure, Func<DateTimeOffset, Task> onWaiting, CancellationToken token)
    {
        await _scans.WaitAsync(token).ConfigureAwait(false);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            RequireModels();
            var queue = new FileWorkQueue();
            var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            async Task Work()
            {
                try
                {
                    while (await wake.Reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
                    {
                        while (wake.Reader.TryRead(out _)) { }
                        while (true)
                        {
                            stopping.Token.ThrowIfCancellationRequested();
                            var discovery = queue.TakeReconciliation();
                            if (discovery is not null) await onResult(await ReconcileAsync(discovery, stopping.Token).ConfigureAwait(false)).ConfigureAwait(false);
                            // Select only after the AI lane is available, so waiting behind a search
                            // does not prematurely pin a lower-priority file as already running.
                            await _operations.WaitAsync(stopping.Token).ConfigureAwait(false);
                            TextCycleResult result;
                            try
                            {
                                var file = queue.Take();
                                if (file is null) break;
                                try { result = await ProcessFileAsync(file, progress, stopping.Token, laneHeld: true).ConfigureAwait(false); }
                                finally { queue.Complete(); }
                            }
                            finally { _operations.Release(); }
                            await onResult(result).ConfigureAwait(false);
                        }
                    }
                }
                catch { stopping.Cancel(); throw; }
            }
            var worker = Work();
            var checks = monitor.RunAsync(async ct =>
            {
                // Filesystem discovery never waits for the AI/database operation lane.
                var discovery = await DiscoverAsync(ct).ConfigureAwait(false);
                queue.Replace(discovery);
                wake.Writer.TryWrite(true);
                progress?.Report($"チェック完了：処理待ち {queue.Count}、更新除外 {discovery.Deferred}、未探索フォルダ {discovery.Scan.FailedRoots}。実行中のファイルは継続します。");
            }, TimeSpan.FromMinutes(settings.MonitoringIntervalMinutes), onFailure, onWaiting, stopping.Token);
            await Task.WhenAny(worker, checks).ConfigureAwait(false);
            stopping.Cancel();
            try { await Task.WhenAll(worker, checks).ConfigureAwait(false); }
            catch { if (checks.IsFaulted) await checks.ConfigureAwait(false); throw; }
        }
        finally { _scans.Release(); }
    }

    private async Task<TextCycleResult> ReconcileAsync(FileDiscovery discovery, CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var seen = discovery.Scan.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignore = new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns);
            var deleted = 0;
            foreach (var old in (await repository.DocumentsAsync(token).ConfigureAwait(false)).Where(d => d.Status != "deleted" && !seen.Contains(d.Path)))
            {
                var stillConfigured = settings.Folders.Any(f => TextFiles.InFolder(old.Path, f));
                if (stillConfigured)
                {
                    try { stillConfigured = !ignore.IsIgnored(old.Path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                }
                if (stillConfigured && (!discovery.Scan.CompletedRoots.Any(f => TextFiles.InFolder(old.Path, f)) ||
                    File.Exists(old.Path) && settings.AllowedTextExtensions().Contains(Path.GetExtension(old.Path), StringComparer.OrdinalIgnoreCase))) continue;
                await repository.DeactivateAsync(old, token).ConfigureAwait(false);
                await repository.SaveAsync(old with { Status = "deleted" }, token).ConfigureAwait(false);
                deleted++;
            }
            return new(0, 0, 0, 0, deleted, discovery.Scan.FailedRoots, discovery.Deferred);
        }
        finally { _operations.Release(); }
    }

    private static TextCycleResult Add(TextCycleResult a, TextCycleResult b) => new(a.Indexed + b.Indexed, a.Excluded + b.Excluded,
        a.Unchanged + b.Unchanged, a.Failed + b.Failed, a.Deleted + b.Deleted, a.InaccessibleRoots + b.InaccessibleRoots, a.Deferred + b.Deferred);
}
