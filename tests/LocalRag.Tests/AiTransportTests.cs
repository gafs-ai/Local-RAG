using System.Collections.Concurrent;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LocalRag.Tests;

public sealed class AiTransportTests
{
    private static WebApplication Server()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    [Theory]
    [InlineData(AiProvider.Lemonade)]
    [InlineData(AiProvider.OpenVino)]
    [InlineData(AiProvider.LmStudio)]
    public async Task RealHttpTransportNeverFollowsRedirectOrForwardsBearer(AiProvider provider)
    {
        await using var target = Server(); var received = 0;
        target.Run(context => { Interlocked.Increment(ref received); return context.Response.WriteAsync("{}"); });
        await target.StartAsync();
        await using var origin = Server();
        origin.Run(context => { context.Response.Redirect(target.Urls.Single() + "/capture"); return Task.CompletedTask; });
        await origin.StartAsync();
        var endpoint = new AiEndpointSettings { Provider = provider, BaseUrl = origin.Urls.Single() + "/v1/", AutoStartServer = false };
        using var client = AiModelClient.Create(endpoint, "must-not-forward");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetModelsAsync());
        Assert.Equal(0, Volatile.Read(ref received));
    }

    [Fact]
    public async Task IndependentHttpServersReceiveOnlyTheirAssignedRoleAndCredential()
    {
        var calls = new ConcurrentQueue<(string Role, string Path, string Key, string Model)>();
        await using var chat = Server(); await using var embedding = Server();
        static void Map(WebApplication app, string role, ConcurrentQueue<(string Role, string Path, string Key, string Model)> calls)
        {
            app.MapGet("/v3/models", () => Results.Json(new { data = new[] { new { id = role, type = role == "chat" ? "llm" : "embedding" } } }));
            app.MapPost("/v3/" + (role == "chat" ? "chat/completions" : "embeddings"), async context =>
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body);
                calls.Enqueue((role, context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(), body.RootElement.GetProperty("model").GetString()!));
                if (role == "chat") await context.Response.WriteAsJsonAsync(new { choices = new[] { new { finish_reason = "stop", message = new { content = "{\"category\":\"general\"}" } } } });
                else await context.Response.WriteAsJsonAsync(new { data = new[] { new { index = 0, embedding = new[] { 1f, 0f, 0f } } } });
            });
        }
        Map(chat, "chat", calls); Map(embedding, "embed", calls);
        await chat.StartAsync(); await embedding.StartAsync();
        var endpoints = new List<AiEndpointSettings>
        {
            new() { Id = "chat", Provider = AiProvider.OpenVino, BaseUrl = chat.Urls.Single() + "/v3/", ChatContextTokens = 2048 },
            new() { Id = "embed", Provider = AiProvider.OpenVino, BaseUrl = embedding.Urls.Single() + "/v3/", EmbeddingContextTokens = 512 }
        };
        var settings = new AppSettings { Ai = new() { Connections = endpoints, ChatConnectionId = "chat", EmbeddingConnectionId = "embed", ChatModel = "chat", EmbeddingModel = "embed" } };
        using var ai = new LemonadeTextAi(settings, apiKeys: new Dictionary<string, string> { ["chat"] = "chat-key", ["embed"] = "embed-key" });
        Assert.Contains("general", await ai.CompleteAsync("classify", "synthetic document", default));
        Assert.Equal(3, (await ai.EmbedAsync(["synthetic query"], default)).Dimension);
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.Role == "chat" && c.Key == "Bearer chat-key" && c.Model == "chat");
        Assert.Contains(calls, c => c.Role == "embed" && c.Key == "Bearer embed-key" && c.Model == "embed");
    }
}
