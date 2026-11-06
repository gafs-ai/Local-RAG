using System.Net;
using System.Security.Cryptography;
using System.Text;
using LocalRag.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;

namespace LocalRag.Mcp;

public sealed class LocalMcpHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stopping;
    private LocalMcpHost(WebApplication app, CancellationTokenSource stopping) { _app = app; _stopping = stopping; }

    public static async Task<LocalMcpHost> StartAsync(ITextRetrieval retrieval, int port, string secret, CancellationToken token = default)
    {
        if (port is < 1024 or > 65535 || secret.Length < 32) throw new ArgumentException("Invalid MCP configuration.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(LocalMcpHost).Assembly.FullName });
        // Environment variables and appsettings cannot turn a loopback service into a public listener.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 32 * 1024;
            options.Limits.MaxConcurrentConnections = 16;
        });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(110));
        var stopping = new CancellationTokenSource();
        builder.Services.AddSingleton<IRetrievalCalls>(_ => new LocalRetrievalCalls(retrieval, stopping.Token));
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "LocalRAG", Version = ProductInfo.Version })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless).WithTools<RetrievalTools>();
        var app = builder.Build();
        var expected = Encoding.ASCII.GetBytes("Bearer " + secret);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != port ||
                context.Request.Headers.ContainsKey("Origin"))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var provided = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(expected, provided))
            { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            return new(app, stopping);
        }
        catch { stopping.Cancel(); await app.DisposeAsync().ConfigureAwait(false); stopping.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
