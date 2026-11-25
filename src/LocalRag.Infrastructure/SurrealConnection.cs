using LocalRag.Core;
using SurrealDb.Net;
using Microsoft.Extensions.DependencyInjection;

namespace LocalRag.Infrastructure;

public sealed class ServerConnection(Uri endpoint, ServerCredentials credentials, bool verifyCertificate = true) : IHttpClientFactory, IDisposable
{
    public Uri Endpoint { get; } = OutboundPolicy.RequireAllowed(endpoint);
    public bool VerifyCertificate { get; } = verifyCertificate;
    internal string? ManagedStorageDirectory { get; set; }
    internal bool HasMaintenanceHeadroom()
    {
        if (ManagedStorageDirectory is null) return true;
        try { return new DriveInfo(Path.GetPathRoot(ManagedStorageDirectory)!).AvailableFreeSpace >= 1024L * 1024 * 1024; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    internal Uri RpcEndpoint { get; } = RpcOrigin(OutboundPolicy.RequireAllowed(endpoint));
    private readonly Lazy<HttpMessageHandler> _handler = new(() => OutboundHttp.ForEndpoint(RpcOrigin(OutboundPolicy.RequireAllowed(endpoint)), verifyCertificate));
    private static Uri RpcOrigin(Uri uri) => new UriBuilder(uri)
        { Scheme = uri.Scheme switch { "wss" => "https", "ws" => "http", var scheme => scheme }, Port = uri.Port, Path = "/" }.Uri;
    public HttpClient CreateClient(string name) => new(_handler.Value, disposeHandler: false);
    public void Dispose() { if (_handler.IsValueCreated) _handler.Value.Dispose(); }
    internal ServerCredentials Credentials { get; } = credentials;
    public override string ToString() => Endpoint.ToString();
}

public static class SurrealConnection
{
    public static async Task<SurrealDbClient> OpenAsync(ServerConnection connection, string database, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(database) || database.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new ArgumentException("DB名が不正です。", nameof(database));
        // Use the same bounded transport for version checks and CBOR RPC, including legacy ws/wss addresses.
        var options = SurrealDbOptions.Create().WithEndpoint(connection.RpcEndpoint.ToString())
            .WithUsername(connection.Credentials.Username).WithPassword(connection.Credentials.Password)
            .WithNamespace("localrag").WithDatabase(database).Build();
        var client = new SurrealDbClient(options, connection);
        try
        {
            await client.Connect(token).ConfigureAwait(false);
            (await client.RawQuery($"DEFINE NAMESPACE IF NOT EXISTS localrag; DEFINE DATABASE IF NOT EXISTS {database};", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
