using LocalRag.Core;

namespace LocalRag.Infrastructure;

public interface IAiModelClient : IDisposable
{
    Task<IReadOnlyList<LemonadeModel>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<string> CompleteAsync(string model, string system, string input, int maxTokens = 128, CancellationToken cancellationToken = default, bool jsonObject = false);
    Task<int> ChatInputBudgetAsync(string model, string system, int outputTokens, CancellationToken token);
    Task<int> EmbeddingInputBudgetAsync(string model, CancellationToken token);
    Task<EmbeddingBatch> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

public static class AiModelClient
{
    public static IAiModelClient Create(AiEndpointSettings endpoint, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        if (endpoint.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join("\n", errors));
        return endpoint.Provider == AiProvider.Lemonade
            ? new LemonadeClient(AiConnectionSettings.ForEndpoint(endpoint), handler, apiKey, endpoint)
            : new OpenAiModelClient(endpoint, apiKey, handler);
    }
}
