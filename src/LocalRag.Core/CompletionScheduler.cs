namespace LocalRag.Core;

/// <summary>One cycle at a time; the delay starts only after the cycle finishes.</summary>
public sealed class CompletionScheduler
{
    private int _running;

    public async Task RunAsync(
        Func<CancellationToken, Task> cycle,
        TimeSpan interval,
        Func<Exception, Task> onFailure,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("監視はすでに実行中です。");
        delay ??= Task.Delay;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await cycle(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { await onFailure(ex).ConfigureAwait(false); }
                await delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
