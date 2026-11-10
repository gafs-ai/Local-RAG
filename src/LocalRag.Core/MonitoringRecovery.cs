namespace LocalRag.Core;

/// <summary>Recover a stopped monitoring session only from transient errors, never from a user cancellation.</summary>
public static class MonitoringRecovery
{
    public static bool IsTransient(Exception error) => error is MonitoringPersistenceException { InnerException: { } inner }
        ? IsTransient(inner) : IngestionRetry.Kind(error) == "transient";

    public static async Task RunAsync(Func<bool, CancellationToken, Task> run,
        Func<Exception, TimeSpan, Task> onRetry, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempt = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { await run(attempt > 0, token).ConfigureAwait(false); return; }
            catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex))
            {
                var wait = TimeSpan.FromSeconds(attempt++ switch { 0 => 5, 1 => 15, 2 => 30, _ => 60 });
                await onRetry(ex, wait).ConfigureAwait(false);
                await delay(wait, token).ConfigureAwait(false);
            }
        }
    }
}
