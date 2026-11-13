using System.Net;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class AiConnectionTests
{
    private static AiEndpointSettings Endpoint(AiProvider provider = AiProvider.OpenVino, string host = "localhost") => new()
    { Id = "endpoint", Name = "test", Provider = provider, BaseUrl = $"http://{host}:18001/v3/", ChatContextTokens = 2048, EmbeddingContextTokens = 512 };
    private static AppSettings Multi() => new() { Ai = new() { ChatModel = "chat", EmbeddingModel = "embed", ChatConnectionId = "chat", EmbeddingConnectionId = "embed",
        Connections = [Endpoint() with { Id = "chat" }, Endpoint(AiProvider.LmStudio) with { Id = "embed", BaseUrl = "http://localhost:1234/v1/" }] } };

    [Fact]
    public void LegacyMigrationPreservesProcessingAndVectorIdentity()
    {
        var original = new AppSettings { Ai = new() { ChatModel = "Gemma", EmbeddingModel = "nomic-embed-text-v2-moe" } };
        var migrated = original with { Ai = original.Ai.WithConnections() };
        Assert.Empty(migrated.Validate());
        Assert.Equal(original.PipelineConfigurationId(), migrated.PipelineConfigurationId());
        Assert.Equal(ProcessingPlan.Create(original, "a.txt", "general").Id, ProcessingPlan.Create(migrated, "a.txt", "general").Id);
        Assert.Equal(original.TextPipeline.SpaceId(original.Ai, 8), migrated.TextPipeline.SpaceId(migrated.Ai, 8));
        Assert.True(migrated.Ai.ChatEndpoint().AutoStartServer);
    }

    [Fact]
    public void SeparateEndpointsInvalidateOnlyRelevantStages()
    {
        var original = Multi(); var plan = ProcessingPlan.Create(original, "a.txt", "general");
        var chat = original with { Ai = original.Ai with { Connections = [original.Ai.Connections[0] with { BaseUrl = "https://chat.example/v3/" }, original.Ai.Connections[1]] } };
        var changed = ProcessingPlan.Create(chat, "a.txt", "general");
        Assert.NotEqual(plan.Classify.Id, changed.Classify.Id); Assert.Equal(plan.Embedding.Id, changed.Embedding.Id); Assert.Equal(plan.Read.Id, changed.Read.Id);
        Assert.Equal(original.TextPipeline.SpaceId(original.Ai, 8), chat.TextPipeline.SpaceId(chat.Ai, 8));
        var embed = original with { Ai = original.Ai with { Connections = [original.Ai.Connections[0], original.Ai.Connections[1] with { BaseUrl = "https://embed.example/v1/" }] } };
        changed = ProcessingPlan.Create(embed, "a.txt", "general");
        Assert.Equal(plan.Classify.Id, changed.Classify.Id); Assert.NotEqual(plan.Embedding.Id, changed.Embedding.Id);
        Assert.NotEqual(original.TextPipeline.SpaceId(original.Ai, 8), embed.TextPipeline.SpaceId(embed.Ai, 8));
        Assert.True(embed.Ai.UsesExternalInference()); Assert.False(original.Ai.UsesExternalInference());
    }

    [Fact]
    public void DisplayLifecycleAndUnusedProfilesDoNotInvalidateIndex()
    {
        var original = Multi();
        var updated = original with { Ai = original.Ai with { Connections = original.Ai.Connections.Select(e => e with { Name = "renamed", StopServerOnExit = false }).Append(Endpoint() with { Id = "unused", BaseUrl = "https://unused.example/v3/" }).ToList() } };
        Assert.Equal(original.PipelineConfigurationId(), updated.PipelineConfigurationId());
        Assert.Equal(ProcessingPlan.Create(original, "a.txt", "general").Id, ProcessingPlan.Create(updated, "a.txt", "general").Id);
    }

    [Fact]
    public void CredentialChangeRetriesFailuresWithoutInvalidatingSuccessfulIndex()
    {
        var original = Multi();
        var updated = original with { Ai = original.Ai with { Connections = original.Ai.Connections.Select(e => e with { CredentialsRevision = Guid.NewGuid().ToString("N") }).ToList() } };
        Assert.Equal(original.PipelineConfigurationId(), updated.PipelineConfigurationId());
        Assert.Equal(ProcessingPlan.Create(original, "a.txt", "general").Id, ProcessingPlan.Create(updated, "a.txt", "general").Id);
        Assert.NotEqual(IngestionRetry.ConfigurationId(original, "a.txt"), IngestionRetry.ConfigurationId(updated, "a.txt"));
    }

    [Theory]
    [InlineData("https://host.example/prefix/v1/", false, true)]
    [InlineData("http://192.168.1.20:8001/v3/", false, true)]
    [InlineData("https://host.example/v1/", true, false)]
    [InlineData("ftp://host.example/v1/", false, false)]
    [InlineData("https://user:secret@host.example/v1/", false, false)]
    [InlineData("https://host.example/v1/?token=secret", false, false)]
    [InlineData("http://localhost:1234/v1/", true, true)]
    public void ExplicitRemoteUrlsAreAllowedButCannotLaunchProcesses(string url, bool start, bool valid)
        => Assert.Equal(valid, (Endpoint(AiProvider.LmStudio) with { BaseUrl = url, AutoStartServer = start }).Validate().Count == 0);

    [Fact]
    public async Task ProfilesRoundTripAndCredentialsAreBoundToExactDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var settings = Multi(); var store = new SettingsStore(directory);
        await store.SaveAsync(settings); var loaded = await store.LoadAsync();
        Assert.Equal(3, loaded.SchemaVersion); // Old clients must reject rather than use the obsolete single endpoint.
        Assert.Equal(settings.Ai.Connections, loaded.Ai.Connections); Assert.Equal("embed", loaded.Ai.EmbeddingConnectionId);
        var credentials = new AiCredentialStore(directory); var endpoint = settings.Ai.ChatEndpoint();
        await credentials.SaveAsync(endpoint, "test-secret-value");
        Assert.Equal("test-secret-value", await credentials.LoadAsync(endpoint));
        Assert.Null(await credentials.LoadAsync(endpoint with { BaseUrl = "https://other.example/v3/" }));
        Assert.Null(await credentials.LoadAsync(endpoint with { Provider = AiProvider.LmStudio }));
        Assert.Null(await credentials.LoadAsync(endpoint with { Id = "other" }));
        Assert.DoesNotContain("test-secret-value", await File.ReadAllTextAsync(store.FilePath));
        var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(directory, "ai-credentials"))));
        Assert.DoesNotContain("test-secret-value", Encoding.UTF8.GetString(bytes));
        await credentials.SaveAsync(endpoint, null); Assert.NotNull(await credentials.LoadAsync(endpoint));
        await credentials.SaveAsync(endpoint, ""); Assert.Null(await credentials.LoadAsync(endpoint));
    }

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(Uri Url, string? Key, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Calls.Add((request.RequestUri!, request.Headers.Authorization?.Parameter, body));
            return response(request, body);
        }
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private const string Models = """{"data":[{"id":"chat","type":"llm"},{"id":"embed","type":"embedding"}]}""";
    private const string Chat = """{"choices":[{"finish_reason":"stop","message":{"content":"{\"category\":\"general\"}"}}]}""";

    [Fact]
    public async Task ChatAndEmbeddingRouteToTheirOwnHostsModelsAndKeys()
    {
        var settings = Multi();
        // Force standard LM compatibility fallback, without confusing a Lemonade /load call.
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath.StartsWith("/api/") ? Json("{}", HttpStatusCode.NotFound) :
            r.RequestUri.AbsolutePath.EndsWith("models") ? Json(Models) : r.RequestUri.AbsolutePath.EndsWith("embeddings") ? Json("""{"data":[{"index":0,"embedding":[1,0]}]}""") : Json(Chat));
        using var routed = new LemonadeTextAi(settings, handler, new Dictionary<string, string> { ["chat"] = "chat-key", ["embed"] = "embed-key" });
        Assert.Contains("general", await routed.CompleteAsync("system", "document", default));
        Assert.Equal(2, (await routed.EmbedAsync(["query"], default)).Dimension);
        Assert.Contains(handler.Calls, c => c.Url.Port == 18001 && c.Url.AbsolutePath == "/v3/chat/completions" && c.Key == "chat-key" && c.Body!.Contains("\"model\":\"chat\""));
        Assert.Contains(handler.Calls, c => c.Url.Port == 1234 && c.Url.AbsolutePath == "/v1/embeddings" && c.Key == "embed-key" && c.Body!.Contains("\"model\":\"embed\""));
        Assert.DoesNotContain(handler.Calls, c => c.Url.AbsolutePath.EndsWith("/load"));
    }

    [Fact]
    public async Task LmStudioUsesLoadedRuntimeContextAndKeepsProxyPrefix()
    {
        var handler = new Handler((r, _) => Json("""{"models":[{"type":"llm","key":"gemma","max_context_length":262144,"loaded_instances":[{"id":"loaded-gemma","config":{"context_length":4096}}]}]}"""));
        using var client = new OpenAiModelClient(Endpoint(AiProvider.LmStudio) with { BaseUrl = "https://server.example/proxy/v1/", ChatContextTokens = 0 }, "key", handler);
        Assert.Equal(4096 - 128 - 256 - 6, await client.ChatInputBudgetAsync("gemma", "説明", 128, default));
        Assert.All(handler.Calls, c => { Assert.Equal("/proxy/api/v1/models", c.Url.AbsolutePath); Assert.Equal("key", c.Key); });
    }

    [Fact]
    public async Task LmStudioLoadsUnloadedModelAndUsesItsInstanceIdentifier()
    {
        var handler = new Handler((r, _) => Json(r.RequestUri!.AbsolutePath switch
        {
            "/api/v1/models" => """{"models":[{"type":"llm","key":"gemma","max_context_length":262144,"loaded_instances":[]}]}""",
            "/api/v1/models/load" => """{"instance_id":"instance-gemma","load_config":{"context_length":2048}}""",
            _ => Chat
        }));
        using var client = new OpenAiModelClient(Endpoint(AiProvider.LmStudio) with { BaseUrl = "http://localhost:1234/v1/", ChatContextTokens = 0 }, handler: handler);
        await client.CompleteAsync("gemma", "system", "input", jsonObject: true);
        var completion = handler.Calls.Single(c => c.Url.AbsolutePath.EndsWith("completions"));
        using var json = JsonDocument.Parse(completion.Body!);
        Assert.Equal("instance-gemma", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.DoesNotContain(handler.Calls, c => c.Url.AbsolutePath == "/v1/load");
    }

    [Theory]
    [InlineData(AiProvider.Lemonade, true)]
    [InlineData(AiProvider.LmStudio, true)]
    [InlineData(AiProvider.OpenVino, false)]
    public void JsonOutputDefaultDependsOnProviderAndPreservesExplicitChoice(AiProvider provider, bool expected)
    {
        var profile = new AiEndpointSettings { Provider = provider, JsonOutput = AiEndpointSettings.DefaultJsonOutputFor(provider) };
        Assert.Equal(expected, profile.JsonOutput);
        foreach (var chosen in new[] { false, true })
        {
            var saved = profile with { JsonOutput = chosen };
            var restored = JsonSerializer.Deserialize<AiEndpointSettings>(JsonSerializer.Serialize(saved))!;
            Assert.Equal(chosen, restored.JsonOutput);
        }
        var missing = JsonSerializer.Deserialize<AiEndpointSettings>($$"""{"Provider":"{{provider}}"}""")!;
        Assert.True(missing.JsonOutput); // Preserve the earlier stored format when the field was omitted.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public async Task LmStudioLoadPassesOnlyAnExplicitContextLimit(int configured)
    {
        var handler = new Handler((r, _) => Json(r.RequestUri!.AbsolutePath switch
        {
            "/api/v1/models" => """{"models":[{"type":"llm","key":"gemma","loaded_instances":[]}]}""",
            "/api/v1/models/load" => """{"instance_id":"instance-gemma","load_config":{"context_length":4096}}""",
            _ => Chat
        }));
        using var client = new OpenAiModelClient(Endpoint(AiProvider.LmStudio) with { BaseUrl = "http://localhost:1234/v1/", ChatContextTokens = configured }, handler: handler);
        await client.ChatInputBudgetAsync("gemma", "system", 128, default);
        using var body = JsonDocument.Parse(handler.Calls.Single(c => c.Url.AbsolutePath.EndsWith("/load")).Body!);
        Assert.Equal(configured > 0, body.RootElement.TryGetProperty("context_length", out var limit));
        if (configured > 0) Assert.Equal(configured, limit.GetInt32());
    }

    [Theory]
    [InlineData(0, 1024, 1024)]
    [InlineData(2048, 4096, 2048)]
    [InlineData(4096, 1024, 1024)]
    public async Task ConfiguredCeilingNeverExceedsRuntimeCeiling(int configured, int actual, int expected)
    {
        var handler = new Handler((_, _) => Json(JsonSerializer.Serialize(new { data = new[] { new { id = "chat", context_length = actual } } })));
        using var client = new OpenAiModelClient(Endpoint() with { ChatContextTokens = configured }, handler: handler);
        Assert.Equal(expected - 128 - 256, await client.ChatInputBudgetAsync("chat", "", 128, default));
    }

    [Fact]
    public async Task UnknownRuntimeLimitFailsClosedAndDoesNotTrustArchitecturalMaximum()
    {
        var handler = new Handler((_, _) => Json("""{"data":[{"id":"chat","max_context_length":262144}]}"""));
        using var client = new OpenAiModelClient(Endpoint() with { ChatContextTokens = 0 }, handler: handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CompleteAsync("chat", "system", "secret"));
        Assert.All(handler.Calls, c => Assert.Null(c.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LmAuthenticationOrServerErrorDoesNotFallBack(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Json("{}", status));
        using var client = new OpenAiModelClient(Endpoint(AiProvider.LmStudio), handler: handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetModelsAsync()); Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task JsonModeCanBeDisabledButTruncationIsStillRejected()
    {
        var handler = new Handler((r, _) => Json(r.Method == HttpMethod.Get ? Models : """{"choices":[{"finish_reason":"length","message":{"content":"{}"}}]}"""));
        using var client = new OpenAiModelClient(Endpoint() with { JsonOutput = false }, handler: handler);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => client.CompleteAsync("chat", "", "input", jsonObject: true));
        Assert.Equal("output_incomplete", error.Code);
        Assert.DoesNotContain("response_format", handler.Calls.Last().Body!);
    }

    [Theory]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0,0]}]}")]
    [InlineData("{\"data\":[{\"index\":1,\"embedding\":[1,0]}]}")]
    [InlineData("{\"data\":[]}")]
    public async Task InvalidEmbeddingResponsesAreRejected(string response)
    {
        var handler = new Handler((r, _) => Json(r.Method == HttpMethod.Get ? Models : response));
        using var client = new OpenAiModelClient(Endpoint(), handler: handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EmbedAsync("embed", ["text"]));
    }

    [Fact]
    public void ExternalInferenceDoesNotAttributeSpeedToLocalHardwareOrDiscloseEndpoint()
    {
        var local = Multi() with { UsageStatistics = new() { Enabled = true, NoticeAcknowledged = true } };
        var accumulator = new UsageStatisticsAccumulator(); accumulator.Configure(local.UsageStatistics);
        accumulator.Record(".txt", "ready", TimeSpan.FromSeconds(3));
        Assert.NotNull(accumulator.Capture(local, "0.26.7"));
        var remote = local with { Ai = local.Ai with { Connections = [local.Ai.Connections[0] with { BaseUrl = "https://private-server.example/v3/" }, local.Ai.Connections[1]] } };
        Assert.Null(accumulator.Capture(remote, "0.26.7")); Assert.Null(accumulator.Preview(remote, "0.26.7"));
        var report = accumulator.SnapshotForFeedback(remote, "0.26.7");
        Assert.Empty(report.Processing); Assert.DoesNotContain("private-server", JsonSerializer.Serialize(report));
    }
}
