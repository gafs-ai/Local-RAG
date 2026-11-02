using LocalRag.Infrastructure;

namespace LocalRag.Mcp;

public sealed class McpTokenStore(string dataDirectory)
{
    // Separate from database credentials, protected for the current Windows user by DPAPI.
    private readonly ServerCredentialStore _store = new(Path.Combine(dataDirectory, "mcp"));
    public async Task<string> LoadAsync(CancellationToken token = default) =>
        (await _store.LoadAsync(token).ConfigureAwait(false))?.Password ?? throw new InvalidOperationException("Start Local RAG before connecting MCP.");

    public async Task<string> GetOrCreateAsync(CancellationToken token = default)
    {
        var credentials = await _store.LoadAsync(token).ConfigureAwait(false);
        if (credentials is not null) return credentials.Password;
        credentials = ServerCredentialStore.Generate();
        await _store.SaveAsync(credentials, token).ConfigureAwait(false);
        return credentials.Password;
    }
}
