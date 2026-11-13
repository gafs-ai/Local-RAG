using System.Security.Cryptography;
using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed class AiCredentialStore(string dataDirectory)
{
    private string FilePath(AiEndpointSettings endpoint) => Path.Combine(Path.GetFullPath(dataDirectory), "ai-credentials",
        TextRagSettings.Hash(endpoint.Id + "\n" + endpoint.Provider + "\n" + endpoint.GetBaseUri().AbsoluteUri) + ".dpapi");

    public async Task<string?> LoadAsync(AiEndpointSettings endpoint, CancellationToken token = default)
    {
        var path = FilePath(endpoint);
        if (!File.Exists(path)) return null;
        var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, token), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // null means unchanged; empty means explicitly remove the key.
    public async Task SaveAsync(AiEndpointSettings endpoint, string? key, CancellationToken token = default)
    {
        if (key is null) return;
        if (key.Length > 8192 || key.Any(char.IsControl)) throw new InvalidDataException("APIキーの形式が不正です。");
        var path = FilePath(endpoint);
        if (key.Length == 0) { File.Delete(path); return; }
        var plain = Encoding.UTF8.GetBytes(key);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, encrypted, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(AiConnectionSettings settings, CancellationToken token)
    {
        var result = new Dictionary<string, string>();
        foreach (var endpoint in new[] { settings.ChatEndpoint(), settings.EmbeddingEndpoint() }.DistinctBy(e => e.Id))
            if (await LoadAsync(endpoint, token) is { } key) result[endpoint.Id] = key;
        return result;
    }
}
