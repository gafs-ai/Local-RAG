using SurrealDb.Net;
using Microsoft.Extensions.DependencyInjection;

namespace LocalRag.Infrastructure;

public sealed class ServerConnection(Uri endpoint, ServerCredentials credentials)
{
    public Uri Endpoint { get; } = endpoint;
    internal ServerCredentials Credentials { get; } = credentials;
    public override string ToString() => Endpoint.ToString();
}

public static class SurrealConnection
{
    public static async Task<SurrealDbClient> OpenAsync(ServerConnection connection, string database, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(database) || database.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new ArgumentException("DB名が不正です。", nameof(database));
        // Managed local servers use HTTP; external endpoints may use ws/wss.
        // Dispose clients before shutting down an owned server so sockets cannot delay shutdown.
        var options = SurrealDbOptions.Create().WithEndpoint(connection.Endpoint.ToString())
            .WithUsername(connection.Credentials.Username).WithPassword(connection.Credentials.Password)
            .WithNamespace("localrag").WithDatabase(database).Build();
        var client = new SurrealDbClient(options);
        try
        {
            await client.Connect(token).ConfigureAwait(false);
            (await client.RawQuery($"DEFINE NAMESPACE IF NOT EXISTS localrag; DEFINE DATABASE IF NOT EXISTS {database};", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
