using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Used only by ingestion while it owns the operation lane. Retrieval uses the
/// underlying AI directly, so its embedding and disclosure checks cannot yield to themselves.</summary>
internal sealed class YieldingTextRagAi(ITextRagAi inner, RetrievalPriorityGate operations) : ITextRagAi
{
    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await operations.YieldToRetrievalAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        T result;
        try { result = await action().ConfigureAwait(false); }
        finally { await operations.YieldToRetrievalAsync().ConfigureAwait(false); }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public Task<string> CompleteAsync(string system, string input, CancellationToken token) =>
        RunAsync(() => inner.CompleteAsync(system, input, token), token);
    public Task<string> CompleteBoundedAsync(string system, string input, int outputTokens, CancellationToken token) =>
        RunAsync(() => inner.CompleteBoundedAsync(system, input, outputTokens, token), token);
    public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) =>
        RunAsync(() => inner.EmbedAsync(inputs, token), token);
    public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) =>
        RunAsync(() => inner.ChatInputBudgetAsync(system, outputTokens, token), token);
    public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) =>
        RunAsync(() => inner.DocumentEmbeddingBudgetAsync(token), token);
}
