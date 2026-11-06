using System.Security.Cryptography;
using System.Text.Json;

namespace LocalRag.Infrastructure;

public sealed record ServerCredentials(string Username, string Password)
{
    public override string ToString() => "ServerCredentials [protected]";
}

public sealed class ServerCredentialStore(string dataDirectory, Uri? externalEndpoint = null)
{
    // Keep local-server credentials and each external server's credentials independent.
    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "server", externalEndpoint is null
        ? "credentials.dpapi" : "external-" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(externalEndpoint.GetLeftPart(UriPartial.Authority)))) + ".dpapi");

    public async Task<ServerCredentials?> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return null;
        var encrypted = await File.ReadAllBytesAsync(_path, token).ConfigureAwait(false);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<ServerCredentials>(plain)
                ?? throw new InvalidDataException("DB認証設定を読み取れません。");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(ServerCredentials credentials, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrEmpty(credentials.Password))
            throw new InvalidDataException("DBのユーザー名とパスワードを指定してください。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, token).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ServerCredentials Generate() => new("localrag", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
}
