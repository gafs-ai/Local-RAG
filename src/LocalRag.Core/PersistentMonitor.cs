// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.ExceptionServices;

namespace LocalRag.Core;

/// <summary>Persists running/completed cycles, and resumes the remaining completion-based interval.</summary>
public sealed class PersistentMonitor(
    IMonitoringCheckpointStore store, string scope, TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly CompletionScheduler _scheduler = new();
    private MonitoringCheckpoint? _checkpoint;
    private int _cycleRunning;

    public async Task RunAsync(Func<CancellationToken, Task> cycle, TimeSpan interval,
        Func<Exception, Task> onFailure, Func<DateTimeOffset, Task> onWaiting, CancellationToken token)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        MonitoringCheckpoint? checkpoint;
        try { checkpoint = await store.LoadMonitoringAsync(token).ConfigureAwait(false); checkpoint?.Validate(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new MonitoringPersistenceException(ex); }
        var initialDelay = checkpoint?.Remaining(scope, _clock.GetUtcNow(), interval) ?? TimeSpan.Zero;
        _checkpoint = checkpoint;
        await _scheduler.RunAsync(ct => RunCycleAsync(cycle, ct), interval,
            ex => ex is MonitoringPersistenceException ? Task.FromException(ex) : onFailure(ex), token,
            async (duration, ct) =>
            {
                var now = _clock.GetUtcNow();
                var remaining = _checkpoint?.Remaining(scope, now, interval) ?? duration;
                var due = now + remaining;
                await onWaiting(due).ConfigureAwait(false);
                await WaitAsync(due, interval, ct).ConfigureAwait(false);
            }, initialDelay).ConfigureAwait(false);
    }

    // Manual scans also update the completion timestamp, but bypass any pending interval.
    public async Task RunCycleAsync(Func<CancellationToken, Task> cycle, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _cycleRunning, 1, 0) != 0)
            throw new InvalidOperationException("取り込みはすでに実行中です。");
        try
        {
            await SaveAsync(new(scope, true, null), token).ConfigureAwait(false);
            Exception? failure = null;
            try { await cycle(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failure = ex; }
            token.ThrowIfCancellationRequested();
            await SaveAsync(new(scope, false, _clock.GetUtcNow()), token).ConfigureAwait(false);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Volatile.Write(ref _cycleRunning, 0); }
    }

    private async Task SaveAsync(MonitoringCheckpoint checkpoint, CancellationToken token)
    {
        try { await store.SaveMonitoringAsync(checkpoint, token).ConfigureAwait(false); _checkpoint = checkpoint; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new MonitoringPersistenceException(ex); }
    }

    private async Task WaitAsync(DateTimeOffset due, TimeSpan maximumWait, CancellationToken token)
    {
        var previous = _clock.GetUtcNow();
        if (due - previous > maximumWait) due = previous + maximumWait;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            if (now < previous) due = now + (due - previous); // Preserve remaining time on a backward adjustment.
            previous = now;
            var remaining = due - now;
            if (remaining <= TimeSpan.Zero) return;
            // Recheck wall time after resume; do not replay every missed cycle after a long sleep.
            await _delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
    }
}
