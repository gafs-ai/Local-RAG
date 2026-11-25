using SurrealDb.Net.Models.Response;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    private enum RetentionMode { Manual, DuringOpen, Background }
    private sealed class AutomaticRetentionSpaceException : Exception;
    private static readonly TimeSpan InitialDeferredRetry = TimeSpan.FromSeconds(30);
    private static TimeSpan NextDeferredRetry(TimeSpan previous) =>
        TimeSpan.FromSeconds(Math.Min(300, previous.TotalSeconds * 2));

    private RetentionRun CreateRetentionRun(RetentionMode mode, CancellationToken token) =>
        new(mode, token, _hasMaintenanceHeadroom, RetentionQueryAsync, Task.Delay);

    internal Task RunAutomaticRetentionAsync(CancellationToken token, IProgress<int>? deferredDocuments,
        Func<string, string, Dictionary<string, object?>?, CancellationToken, Task<SurrealDbResponse>> query,
        Func<TimeSpan, CancellationToken, Task> delay) =>
        UpgradeRetentionAsync(new RetentionRun(RetentionMode.Background, token, _hasMaintenanceHeadroom, query, delay), deferredDocuments);

    private sealed class RetentionRun(RetentionMode mode, CancellationToken token, Func<bool> hasHeadroom,
        Func<string, string, Dictionary<string, object?>?, CancellationToken, Task<SurrealDbResponse>> query,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        public CancellationToken Token => token;
        public bool IsBackground => mode == RetentionMode.Background;
        public void CheckHeadroom()
        {
            token.ThrowIfCancellationRequested();
            if (hasHeadroom()) return;
            if (mode == RetentionMode.Manual)
                throw new IOException("DBを安全に整理するための空き容量（1 GiB）が不足しています。空きを増やしてから再実行してください。");
            throw new AutomaticRetentionSpaceException();
        }

        public async Task WaitAsync(TimeSpan interval)
        {
            await delay(interval, token).ConfigureAwait(false);
            CheckHeadroom();
        }

        public async Task<SurrealDbResponse> QueryAsync(string phase, string sql,
            Dictionary<string, object?>? parameters, bool rowScoped = false)
        {
            var shortRetries = 0;
            var deferredRetry = InitialDeferredRetry;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return await query(phase, sql, parameters, token).ConfigureAwait(false); }
                catch (RetentionQueryFailure ex) when (ex.IsConflict && !token.IsCancellationRequested)
                {
                    if (shortRetries < 2)
                    {
                        var wait = TimeSpan.FromMilliseconds(shortRetries == 0 ? 250 : 1000);
                        shortRetries++;
                        await WaitAsync(wait).ConfigureAwait(false);
                        continue;
                    }
                    // Document/revision conflicts return to the sweep so other keys get a turn.
                    // Source-wide steps such as the final marker have no key to defer.
                    if (rowScoped || !IsBackground) throw;
                    await WaitAsync(deferredRetry).ConfigureAwait(false);
                    deferredRetry = NextDeferredRetry(deferredRetry);
                    shortRetries = 0;
                }
            }
        }
    }

    private static async Task RetryQueueOnceAsync(Queue<string> pending, Func<string, Task> process, IProgress<int>? progress)
    {
        // Process each key once this round, even if it rejoins the tail on another conflict.
        var remaining = pending.Count;
        for (var i = 0; i < remaining; i++)
        {
            var key = pending.Dequeue();
            try { await process(key).ConfigureAwait(false); }
            catch (RetentionQueryFailure ex) when (ex.IsConflict)
            {
                pending.Enqueue(key);
                continue;
            }
            progress?.Report(pending.Count);
        }
    }
}
