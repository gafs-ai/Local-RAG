// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.ExceptionServices;

namespace LocalRag.Core;

/// <summary>Persists periodic discovery checks. AI work runs separately from this schedule.</summary>
public sealed class PersistentMonitor(
    IMonitoringCheckpointStore store, string scope, TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private int _running;
    private int _cycleRunning;

    public async Task RunAsync(Func<CancellationToken, Task> cycle, TimeSpan interval,
        Func<Exception, Task> onFailure, Func<DateTimeOffset, Task> onWaiting, CancellationToken token, bool immediate = false)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        MonitoringCheckpoint? checkpoint;
        try { checkpoint = await store.LoadMonitoringAsync(token).ConfigureAwait(false); checkpoint?.Validate(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new MonitoringPersistenceException(ex); }
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("監視はすでに実行中です。");
        try
        {
            var now = _clock.GetUtcNow();
            var due = immediate ? now : now + (checkpoint?.Remaining(scope, now, interval) ?? TimeSpan.Zero);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (due > _clock.GetUtcNow())
                {
                    await onWaiting(due).ConfigureAwait(false);
                    await WaitAsync(due, interval, token).ConfigureAwait(false);
                }
                var started = _clock.GetUtcNow();
                try { await RunCycleAsync(cycle, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (MonitoringPersistenceException) { throw; }
                catch (Exception ex) { await onFailure(ex).ConfigureAwait(false); }
                due = IntervalSchedule.Next(due < started - interval ? started : due, _clock.GetUtcNow(), interval);
            }
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    // Manual checks bypass the pending interval, and preserve their start time.
    public async Task RunCycleAsync(Func<CancellationToken, Task> cycle, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _cycleRunning, 1, 0) != 0)
            throw new InvalidOperationException("取り込みはすでに実行中です。");
        try
        {
            var started = _clock.GetUtcNow();
            await SaveAsync(new(scope, true, null, LastStartedUtc: started), token).ConfigureAwait(false);
            Exception? failure = null;
            try { await cycle(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failure = ex; }
            token.ThrowIfCancellationRequested();
            await SaveAsync(new(scope, false, _clock.GetUtcNow(), LastStartedUtc: started), token).ConfigureAwait(false);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Volatile.Write(ref _cycleRunning, 0); }
    }

    private async Task SaveAsync(MonitoringCheckpoint checkpoint, CancellationToken token)
    {
        try { await store.SaveMonitoringAsync(checkpoint, token).ConfigureAwait(false); }
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
