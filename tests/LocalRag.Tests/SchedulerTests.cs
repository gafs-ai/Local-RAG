using LocalRag.Core;

namespace LocalRag.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task WaitStartsAfterCompletionAndConcurrentRunIsRejected()
    {
        var scheduler = new CompletionScheduler();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycles = 0;
        var interval = TimeSpan.FromMinutes(15);
        var run = scheduler.RunAsync(async token =>
        {
            Interlocked.Increment(ref cycles);
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
        }, interval, _ => Task.CompletedTask, cancellation.Token, async (duration, token) =>
        {
            Assert.Equal(interval, duration);
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await started.Task.WaitAsync(cancellation.Token);
        Assert.False(waiting.Task.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync(_ => Task.CompletedTask,
            interval, _ => Task.CompletedTask, cancellation.Token));
        finish.SetResult();
        await waiting.Task.WaitAsync(cancellation.Token);
        Assert.Equal(1, cycles);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task FailedCycleAlsoWaitsInsteadOfBusyLooping()
    {
        var errors = 0;
        var waits = 0;
        using var cancellation = new CancellationTokenSource();
        var scheduler = new CompletionScheduler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
            _ => throw new IOException("temporary"), TimeSpan.FromMinutes(15),
            _ => { errors++; return Task.CompletedTask; }, cancellation.Token,
            (_, token) => { waits++; cancellation.Cancel(); return Task.FromCanceled(token); }));
        Assert.Equal(1, errors);
        Assert.Equal(1, waits);
    }
}
