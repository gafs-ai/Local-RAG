namespace LocalRag.Core;

/// <summary>Model-specific retrieval inputs; these rules are part of the index identity.</summary>
public static class EmbeddingInput
{
    public static bool IsNomicV2(string model) => model.Contains("nomic-embed-text-v2-moe", StringComparison.OrdinalIgnoreCase);
    public static string Profile(string model) => IsNomicV2(model) ? "nomic-v2-prefixes-512-v1" : "instruct-query-v1";
    public static string Document(string model, string text) => IsNomicV2(model) ? "search_document: " + text : text;
    // Fixed input convention, not a user-managed prompt. Preserve the existing default for instruct models.
    public static string QueryInstruction(string model) => IsNomicV2(model) ? "" : "Given a question, retrieve relevant passages that answer the question";
    public static string Query(string model, string text) => IsNomicV2(model)
        ? "search_query: " + text
        : $"Instruct: {QueryInstruction(model)}\nQuery: {text}";
    // A server can advertise a larger runtime context than the model's trained retrieval window.
    public static int ContextLimit(string model, int runtimeContext) => IsNomicV2(model) ? Math.Min(512, runtimeContext) : runtimeContext;
}
