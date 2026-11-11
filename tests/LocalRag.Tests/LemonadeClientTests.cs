using System.Net;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class LemonadeClientTests
{
    [Fact]
    public async Task UnknownOrCloudModelsCannotReceiveDocumentContent()
    {
        var handler = new Handler("""
            {"data":[
              {"id":"cloud","recipe":"cloud","cloud_provider":"provider","downloaded":true,"labels":["chat"]},
              {"id":"unknown","recipe":"future-backend","downloaded":true,"labels":["chat"]},
              {"id":"local","recipe":"llamacpp","downloaded":true,"labels":["chat"]}
            ]}
            """);
        using var client = new LemonadeClient(new(), handler);
        var models = await client.GetModelsAsync();
        Assert.False(models[0].VerifiedLocal);
        Assert.False(models[1].VerifiedLocal);
        Assert.True(models[2].VerifiedLocal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync("cloud", "system", "private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync("unknown", "system", "private"));
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task TruncatedChatOutputIsNotAcceptedAsComplete()
    {
        var handler = new Handler("""{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"labels":["chat"]}]}""",
            """{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""");
        using var client = new LemonadeClient(new(), handler);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => client.CompleteAsync("local", "system", "input"));
        Assert.Equal("output_incomplete", error.Code);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task NullChatContentIsAnLlmFailureWithABoundedRetryBudget()
    {
        var handler = new Handler("""{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"labels":["chat"]}]}""",
            """{"choices":[{"finish_reason":"stop","message":{"content":null}}]}""");
        using var client = new LemonadeClient(new(), handler);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => client.CompleteAsync("local", "system", "input"));
        Assert.Equal("response_schema", error.Code);
        Assert.Equal("llm", IngestionRetry.Kind(error));
    }

    private sealed class Handler(string models, string chat = "{}", string? modelsAfterLoad = null) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post ? chat : Posts > 0 ? modelsAfterLoad ?? models : models, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task GemmaNpuReceivesTaskInstructionsInItsUserTurn()
    {
        var handler = new Handler("""{"data":[{"id":"Gemma-3-4b-it-mm-NPU","recipe":"ryzenai-llm","downloaded":true,"labels":["chat"],"context_length":4096}]}""",
            """{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""");
        using var client = new LemonadeClient(new(), handler);
        await client.CompleteAsync("Gemma-3-4b-it-mm-NPU", "Return the category", "document data", jsonObject: true);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        var content = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("Return the category", content);
        Assert.Contains("UNTRUSTED TASK DATA", content);
        Assert.Contains("compact single-line JSON", content);
        Assert.Contains("document data", content);
    }

    private const string EmbeddingModels = """{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"context_length":8192,"labels":["embeddings"]}]}""";

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    public async Task ChatBudgetFollowsReportedContextRatherThanArchitecturalMaximum(int context)
    {
        var handler = new Handler(JsonSerializer.Serialize(new { data = new[] {
            new { id = "local", recipe = "llamacpp", downloaded = true, labels = new[] { "chat" },
                context_length = context, max_context_window = 131072 } } }));
        using var client = new LemonadeClient(new(), handler);
        Assert.Equal(context - Encoding.UTF8.GetByteCount("説明を作成") - 512 - 256,
            await client.ChatInputBudgetAsync("local", "説明を作成", 512, default));
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task MissingChatContextDoesNotFallBackToArchitecturalMaximum()
    {
        var handler = new Handler("""{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"max_context_window":131072,"labels":["chat"]}]}""");
        using var client = new LemonadeClient(new(), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ChatInputBudgetAsync("local", "system", 512, default));
        Assert.Equal(1, handler.Posts); // Load and re-query metadata, never send document data with an assumed limit.
    }

    [Fact]
    public async Task ChatContextIsReadAgainAfterLoadingWhenInitiallyUnknown()
    {
        const string models = """{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"labels":["chat"]}]}""";
        var handler = new Handler(models, modelsAfterLoad: models.Replace("\"labels\"", "\"context_length\":2048,\"labels\""));
        using var client = new LemonadeClient(new(), handler);
        Assert.Equal(2048 - 6 - 512 - 256, await client.ChatInputBudgetAsync("local", "system", 512, default));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task OversizeEmbeddingIsHeldBeforeSendingText()
    {
        var handler = new Handler(EmbeddingModels);
        using var client = new LemonadeClient(new(), handler);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => client.EmbedAsync("local", [new string('あ', 3000)]));
        Assert.Equal("embedding_context", error.Code);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task UnknownRuntimeContextCannotSilentlyTruncateContent()
    {
        var handler = new Handler("""{"data":[{"id":"local","recipe":"llamacpp","downloaded":true,"labels":["embeddings"]}]}""");
        using var client = new LemonadeClient(new(), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EmbedAsync("local", ["private"]));
        Assert.Equal(1, handler.Posts); // Only load, not an embeddings request with document text.
    }

    [Fact]
    public async Task NomicUsesModelLimitEvenWhenServerAdvertisesLargerContext()
    {
        var handler = new Handler(EmbeddingModels.Replace("\"local\"", "\"nomic-embed-text-v2-moe-GGUF\""));
        using var client = new LemonadeClient(new(), handler);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => client.EmbedAsync("nomic-embed-text-v2-moe-GGUF", [new string('あ', 100)]));
        Assert.Equal("embedding_context", error.Code);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public void RetrievalPrefixesFollowTheEmbeddingModel()
    {
        const string model = "nomic-embed-text-v2-moe-GGUF";
        Assert.Equal("search_document: 文書", EmbeddingInput.Document(model, "文書"));
        Assert.Equal("search_query: 質問", EmbeddingInput.Query(model, "質問"));
        Assert.Equal("文書", EmbeddingInput.Document("Qwen3-Embedding-0.6B-GGUF", "文書"));
        Assert.Equal("Instruct: Given a question, retrieve relevant passages that answer the question\nQuery: 質問", EmbeddingInput.Query("Qwen3-Embedding-0.6B-GGUF", "質問"));
        Assert.Equal(128, EmbeddingInput.ContextLimit(model, 128));
    }

    [Fact]
    public async Task EmbeddingsAreRestoredToInputOrder()
    {
        using var client = new LemonadeClient(new(), new Handler(EmbeddingModels,
            """{"data":[{"index":1,"embedding":[0.0,1.0]},{"index":0,"embedding":[1.0,0.0]}]}"""));
        var result = await client.EmbedAsync("local", ["one", "two"]);
        Assert.Equal(2, result.Dimension);
        Assert.Equal(new float[] { 1, 0 }, result.Vectors[0]);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":0,\"embedding\":[2]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":1,\"embedding\":[1,2]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0,0]},{\"index\":1,\"embedding\":[1,2]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1e39]},{\"index\":1,\"embedding\":[1]}]}")]
    public async Task MalformedEmbeddingsAreRejected(string response)
    {
        using var client = new LemonadeClient(new(), new Handler(EmbeddingModels, response));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EmbedAsync("local", ["one", "two"]));
    }

    [Fact]
    public async Task EmbeddingRefusesModelsNotVerifiedForLocalEmbedding()
    {
        var handler = new Handler("""{"data":[{"id":"local","recipe":"cloud","downloaded":true,"labels":["embeddings"]}]}""");
        using var client = new LemonadeClient(new(), handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync("local", ["private"]));
        Assert.Equal(0, handler.Posts);
    }
}
