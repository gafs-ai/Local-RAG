using System.Diagnostics;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private async Task<T> RunRetrievalAsync<T>(string operation, Func<Task<T>> action, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        double? waitMilliseconds = null;
        var outcome = "completed";
        try
        {
            await _operations.WaitRetrievalAsync(token).ConfigureAwait(false);
            waitMilliseconds = timer.Elapsed.TotalMilliseconds;
            token.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = token.IsCancellationRequested ? "cancelled" : ex.GetType().Name;
            throw;
        }
        finally
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            if (waitMilliseconds is not null) _operations.Release();
            OperationalDiagnostics.WriteRetrievalTiming(dataDirectory, operation, waitMilliseconds ?? elapsed,
                waitMilliseconds is null ? 0 : elapsed - waitMilliseconds.Value, outcome);
        }
    }
}
