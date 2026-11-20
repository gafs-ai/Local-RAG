using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalRag.Tests;

public sealed class OutboundPolicyTests
{
    [Theory]
    [InlineData("http://localhost:8123/api", true)]
    [InlineData("ws://LOCALHOST/rpc", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("ws://[::1]:8000", true)]
    [InlineData("https://example.test/v1", true)]
    [InlineData("wss://example.test/rpc", true)]
    [InlineData("ws://192.168.1.20", false)]
    [InlineData("http://127.0.0.2", false)]
    [InlineData("http://127.1", false)]
    [InlineData("http://2130706433", false)]
    [InlineData("http://[::ffff:127.0.0.1]", false)]
    [InlineData("http://localhost.", false)]
    [InlineData("http://localhost.other.test", false)]
    [InlineData("http://localhost@elsewhere.test", false)]
    [InlineData("file:///C:/example", false)]
    public void ExplicitDestinations(string address, bool allowed) => Assert.Equal(allowed, OutboundPolicy.IsAllowed(new Uri(address)));

    [Fact]
    public async Task ExistingInsecureProfileCanBeCorrectedWithoutConnectingOrOverwriting()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var store = new SettingsStore(root.FullName);
            var settings = await store.LoadAsync();
            var broken = settings with { Ai = settings.Ai with { BaseUrl = "http://lan.test/api", AutoStartServer = false }, Database = settings.Database with { ExternalEndpoint = "ws://lan.test" } };
            var saved = JsonSerializer.Serialize(broken); await File.WriteAllTextAsync(store.FilePath, saved);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            var draft = await store.LoadForEditingAsync();
            Assert.Equal(saved, await File.ReadAllTextAsync(store.FilePath));
            Assert.Throws<ArgumentException>(() => new LemonadeClient(draft.Ai with { VerifyCertificate = false }));
            Assert.Throws<ArgumentException>(() => new ServerConnection(draft.Database.Endpoint, new("u", "p"), false));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(draft));
            var fixedSettings = draft with { Ai = draft.Ai with { BaseUrl = "https://lan.test/api", VerifyCertificate = false }, Database = draft.Database with { ExternalEndpoint = "wss://lan.test" } };
            await store.SaveAsync(fixedSettings);
            var loaded = await store.LoadAsync(); Assert.False(loaded.Ai.ChatEndpoint().VerifyCertificate); Assert.True(loaded.Database.VerifyCertificate);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SelfSignedCertificateRequiresExplicitPerEndpointOptOutAndRedirectIsNotFollowed()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        await using var server = builder.Build(); int calls = 0;
        server.MapGet("/models", () => { Interlocked.Increment(ref calls); return "{\"data\":[]}"; });
        server.MapGet("/redirect/models", () => Microsoft.AspNetCore.Http.Results.Redirect("/models"));
        await server.StartAsync(); var origin = server.Urls.Single();
        foreach (var provider in new[]{ AiProvider.Lemonade, AiProvider.OpenVino, AiProvider.LmStudio })
        {
            var endpoint = new AiEndpointSettings { BaseUrl = origin, Provider = provider };
            using var strict = AiModelClient.Create(endpoint);
            using var exception = AiModelClient.Create(endpoint with { VerifyCertificate = false });
            await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetModelsAsync());
            Assert.Empty(await exception.GetModelsAsync());
        }
        var credentials = new ServerCredentials("u", "p"); var wss = new Uri(origin.Replace("https:", "wss:") + "/rpc");
        using var db = new ServerConnection(wss, credentials); using var dbClient = db.CreateClient("");
        await Assert.ThrowsAsync<HttpRequestException>(() => dbClient.GetStringAsync(origin + "/models"));
        using var dbOther = new ServerConnection(wss, credentials, false); using var dbOtherClient = dbOther.CreateClient("");
        Assert.Contains("data", await dbOtherClient.GetStringAsync(origin + "/models"));
        await Assert.ThrowsAsync<HttpRequestException>(() => dbOtherClient.GetStringAsync("http://example.test/models"));
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "https://another.test/models");
        Assert.Throws<HttpRequestException>(() => dbOtherClient.Send(crossOrigin));
        using var redirect = AiModelClient.Create(new() { BaseUrl = origin + "/redirect", VerifyCertificate = false });
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => redirect.GetModelsAsync()); Assert.Equal(HttpStatusCode.Redirect, e.StatusCode);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task DownloadRejectsDowngradeBeforeSendingAnotherRequest()
    {
        var handler = new RedirectHandler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => OutboundHttp.DownloadAsync(client, "https://example.test/file", default));
        Assert.Single(handler.Requests); Assert.Equal("https", handler.Requests[0].Scheme);
    }
    private sealed class RedirectHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.test/file") } });
        }
    }
}
