// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class MonitoringTests
{
    private static readonly string Scope = new('a', 64);
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task RestartWaitsOnlyTheRemainingInterval()
    {
        var clock = new Clock { Now = Start.AddMinutes(5) };
        var store = new Store { State = new(Scope, false, Start) };
        var delays = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = new PersistentMonitor(store, Scope, clock, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays++;
            Assert.True(duration <= TimeSpan.FromSeconds(30));
            clock.Now += duration;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(token =>
        {
            Assert.Equal(Start.AddMinutes(15), clock.Now);
            Assert.Equal(20, delays);
            stop.Cancel();
            return Task.FromCanceled(token);
        }, Interval, _ => throw new Exception("Unexpected cycle failure"), due =>
        {
            Assert.Equal(Start.AddMinutes(15), due);
            return Task.CompletedTask;
        }, stop.Token));
    }

    [Fact]
    public async Task CompletionTimeIsAfterWorkAndIncludesManualScans()
    {
        var clock = new Clock();
        var store = new Store();
        var monitor = new PersistentMonitor(store, Scope, clock);
        await monitor.RunCycleAsync(_ =>
        {
            Assert.True(store.State!.Running);
            clock.Now += TimeSpan.FromMinutes(2);
            return Task.CompletedTask;
        }, default);
        Assert.False(store.State!.Running);
        Assert.Equal(Start.AddMinutes(2), store.State.LastCompletedUtc);
        Assert.Equal(Interval, store.State.Remaining(Scope, clock.Now, Interval));
    }

    [Fact]
    public async Task InterruptedCycleResumesImmediatelyAfterRestart()
    {
        var store = new Store();
        var clock = new Clock();
        using var firstStop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PersistentMonitor(store, Scope, clock).RunCycleAsync(token =>
        {
            firstStop.Cancel();
            return Task.FromCanceled(token);
        }, firstStop.Token));
        Assert.True(store.State!.Running);
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var resumed = new PersistentMonitor(store, Scope, clock, (_, _) => throw new Exception("Interrupted cycle must not wait"));
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumed.RunAsync(token =>
        {
            calls++;
            secondStop.Cancel();
            return Task.FromCanceled(token);
        }, Interval, _ => Task.CompletedTask, _ => Task.CompletedTask, secondStop.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SubsequentCycleIsDueFromCompletionNotFromStatusCallback()
    {
        var clock = new Clock();
        var store = new Store();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cycles = 0;
        var monitor = new PersistentMonitor(store, Scope, clock, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            clock.Now += duration;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(token =>
        {
            if (++cycles == 1)
            {
                clock.Now += TimeSpan.FromMinutes(2);
                return Task.CompletedTask;
            }
            Assert.Equal(Start.AddMinutes(17), clock.Now);
            stop.Cancel();
            return Task.FromCanceled(token);
        }, Interval, _ => throw new Exception("Unexpected failure"), due =>
        {
            Assert.Equal(Start.AddMinutes(17), due);
            clock.Now += TimeSpan.FromMinutes(3); // Slow status update must not extend the deadline.
            return Task.CompletedTask;
        }, stop.Token));
        Assert.Equal(2, cycles);
    }

    [Fact]
    public async Task FailedCycleRecordsCompletionAndWaitsWithoutBusyRetry()
    {
        var clock = new Clock();
        var store = new Store();
        var errors = 0;
        using var stop = new CancellationTokenSource();
        var monitor = new PersistentMonitor(store, Scope, clock, (_, token) =>
        {
            Assert.Equal(1, errors);
            Assert.False(store.State!.Running);
            Assert.Equal(clock.Now, store.State.LastCompletedUtc);
            stop.Cancel();
            return Task.FromCanceled(token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(_ => throw new IOException("synthetic"),
            Interval, _ => { errors++; return Task.CompletedTask; }, _ => Task.CompletedTask, stop.Token));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PersistenceFailureStopsAutomaticMonitoring(int failedSave)
    {
        var store = new Store { FailedSave = failedSave };
        var cycles = 0;
        var monitor = new PersistentMonitor(store, Scope, new Clock(), (_, _) => throw new Exception("Must not wait/retry after persistence failure"));
        await Assert.ThrowsAsync<MonitoringPersistenceException>(() => monitor.RunAsync(_ =>
        {
            cycles++;
            return Task.CompletedTask;
        }, Interval, _ => throw new Exception("Persistence failure must escape the cycle retry"), _ => Task.CompletedTask, default));
        Assert.Equal(failedSave - 1, cycles);
    }

    [Fact]
    public async Task InvalidCheckpointDoesNotStartAReplacementCycle()
    {
        var store = new Store { State = new("invalid", false, Start) };
        await Assert.ThrowsAsync<MonitoringPersistenceException>(() => new PersistentMonitor(store, Scope).RunAsync(
            _ => throw new Exception("Must not run"), Interval, _ => Task.CompletedTask, _ => Task.CompletedTask, default));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ForwardClockJumpRunsOneCycleWithoutReplayingMissedIntervals()
    {
        var clock = new Clock();
        var store = new Store { State = new(Scope, false, Start) };
        var delays = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = new PersistentMonitor(store, Scope, clock, (_, _) =>
        {
            delays++;
            clock.Now += TimeSpan.FromHours(2);
            return Task.CompletedTask;
        });
        var cycles = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(token =>
        {
            cycles++;
            stop.Cancel();
            return Task.FromCanceled(token);
        }, Interval, _ => Task.CompletedTask, _ => Task.CompletedTask, stop.Token));
        Assert.Equal(1, delays);
        Assert.Equal(1, cycles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackwardClockCorrectionKeepsWaitBounded(bool correctionDuringStatus)
    {
        var clock = new Clock();
        var store = new Store { State = new(Scope, false, Start.AddHours(1)) };
        var delays = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = new PersistentMonitor(store, Scope, clock, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays++;
            Assert.True(delays <= 31);
            clock.Now += delays == 1 && !correctionDuringStatus ? TimeSpan.FromHours(-1) : duration;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(token =>
        {
            Assert.Equal(Start.AddMinutes(-45), clock.Now);
            stop.Cancel();
            return Task.FromCanceled(token);
        }, Interval, _ => Task.CompletedTask, _ =>
        {
            if (correctionDuringStatus) clock.Now -= TimeSpan.FromHours(1);
            return Task.CompletedTask;
        }, stop.Token));
    }

    [Fact]
    public void FolderAndPipelineChangesInvalidateWaitButIntervalChangeDoesNot()
    {
        var settings = new AppSettings { Folders = [new(@"C:\docs"), new(@"C:\notes")] };
        var scope = MonitoringCheckpoint.ScopeFor(settings);
        Assert.Equal(scope, MonitoringCheckpoint.ScopeFor(settings with { Folders = [new(@"c:\NOTES\"), new(@"C:\docs")], MonitoringIntervalMinutes = 30 }));
        Assert.NotEqual(scope, MonitoringCheckpoint.ScopeFor(settings with { Folders = [new(@"C:\other")] }));
        Assert.NotEqual(scope, MonitoringCheckpoint.ScopeFor(settings with { Folders = [new(@"C:\docs", false), new(@"C:\notes")] }));
        var changed = MonitoringCheckpoint.ScopeFor(settings with { TextPipeline = settings.TextPipeline with { FormatPrompt = "new prompt" } });
        Assert.NotEqual(scope, changed);
        var checkpoint = new MonitoringCheckpoint(scope, false, Start);
        Assert.Equal(TimeSpan.Zero, checkpoint.Remaining(changed, Start, Interval));
        Assert.Equal(TimeSpan.FromMinutes(25), checkpoint.Remaining(scope, Start.AddMinutes(5), TimeSpan.FromMinutes(30)));
    }

    [ServerFact]
    public async Task RealDatabaseRetainsCompletedAndRunningCyclesAcrossRestart()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var expected = new MonitoringCheckpoint(Scope, false, Start);
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        {
            Assert.Null(await repository.LoadMonitoringAsync(default));
            await repository.SaveMonitoringAsync(expected, default);
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        {
            Assert.Equal(expected, await repository.LoadMonitoringAsync(default));
            await repository.SaveMonitoringAsync(new(Scope, true, null), default);
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using var reopened = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var interrupted = await reopened.LoadMonitoringAsync(default);
        Assert.True(interrupted!.Running);
        Assert.Equal(TimeSpan.Zero, interrupted.Remaining(Scope, Start, Interval));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : IMonitoringCheckpointStore
    {
        public MonitoringCheckpoint? State { get; set; }
        public int Saves { get; private set; }
        public int FailedSave { get; init; }
        public Task<MonitoringCheckpoint?> LoadMonitoringAsync(CancellationToken token) => Task.FromResult(State);
        public Task SaveMonitoringAsync(MonitoringCheckpoint checkpoint, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++Saves == FailedSave) throw new IOException("synthetic failure");
            State = checkpoint;
            return Task.CompletedTask;
        }
    }
}
