using System.Net;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class IngestionRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData("encoding_invalid")]
    [InlineData("document_size")]
    [InlineData("context_capacity")]
    [InlineData("code_syntax")]
    [InlineData("embedding_context")]
    public void DeterministicFailuresWaitForChangedInputOrSettings(string code)
    {
        var f = IngestionRetry.Record(null, "recipe", Now.UtcDateTime, 100, new PipelineValidationException(code, "private"), Now, 5);
        Assert.Null(f.NextAttemptAtUtc);
        Assert.False(IngestionRetry.CanAttempt(f, "recipe", Now.UtcDateTime, 100, Now.AddYears(1)));
        Assert.True(IngestionRetry.CanAttempt(f, "new-recipe", Now.UtcDateTime, 100, Now));
        Assert.True(IngestionRetry.CanAttempt(f, "recipe", Now.AddSeconds(1).UtcDateTime, 100, Now));
        Assert.True(IngestionRetry.CanAttempt(f, "recipe", Now.UtcDateTime, 101, Now));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(f));
    }

    [Fact]
    public void LlmBudgetSurvivesPersistenceAndInterleavedConnectionErrors()
    {
        IngestionFailure? f = null;
        for (var i = 1; i <= 3; i++)
        {
            f = IngestionRetry.Record(f, "r", Now.UtcDateTime, 10, new PipelineValidationException("category_unknown", "bad"), Now, 5);
            f = JsonSerializer.Deserialize<IngestionFailure>(JsonSerializer.Serialize(f))!;
            Assert.Equal(i, f.LlmFailures);
            if (i < 3)
            {
                Assert.False(IngestionRetry.CanAttempt(f, "r", Now.UtcDateTime, 10, Now));
                Assert.True(IngestionRetry.CanAttempt(f, "r", Now.UtcDateTime, 10, Now.AddMinutes(5)));
                f = IngestionRetry.Record(f, "r", Now.UtcDateTime, 10, new HttpRequestException(), Now, 5);
            }
        }
        Assert.Null(f!.NextAttemptAtUtc);
        Assert.Equal(1, IngestionRetry.Record(f, "changed", Now.UtcDateTime, 10,
            new PipelineValidationException("category_unknown", "bad"), Now, 5).LlmFailures);
    }

    [Theory]
    [InlineData(400, "permanent")]
    [InlineData(401, "permanent")]
    [InlineData(404, "permanent")]
    [InlineData(408, "transient")]
    [InlineData(429, "transient")]
    [InlineData(503, "transient")]
    public void HttpStatusDistinguishesRejectedRequestsFromTemporaryOutages(int status, string kind) =>
        Assert.Equal(kind, IngestionRetry.Kind(new HttpRequestException("private", null, (HttpStatusCode)status)));

    [Fact]
    public void TransientBackoffIsBoundedAndDoesNotConsumeLlmBudget()
    {
        IngestionFailure? f = null;
        for(var i = 0; i < 20; i++) f = IngestionRetry.Record(f, "r", Now.UtcDateTime, 10, new TimeoutException(), Now, 5);
        Assert.Equal(Now.AddHours(1), f!.NextAttemptAtUtc);
        Assert.Equal(0, f.LlmFailures);
    }

    [Fact]
    public void OnlyIngestionChangesResetTheFailureBudget()
    {
        var s = new AppSettings { Management = ManagementSettings.CreateDefault() };
        var id = IngestionRetry.ConfigurationId(s, "file.txt");
        var other = s with { Management = s.Management with { Retrieval = s.Management.Retrieval with { LlmEnabled = true } } };
        Assert.Equal(id, IngestionRetry.ConfigurationId(other, "file.txt"));
        other = s with { Management = s.Management with { Routes = [..s.Management.Routes!, new() { Extensions = [".other"] }] } };
        Assert.Equal(id, IngestionRetry.ConfigurationId(other, "file.txt"));
        Assert.NotEqual(id, IngestionRetry.ConfigurationId(s with { Ai = s.Ai with { ChatModel = "new" } }, "file.txt"));
        Assert.NotEqual(id, IngestionRetry.ConfigurationId(s with { TextPipeline = s.TextPipeline with { MaxDocumentCharacters = 500000 } }, "file.txt"));
    }

    [Theory]
    [InlineData(" GENERAL ", "general")]
    [InlineData("一般文書", "general")]
    public async Task ClassificationNormalizesOnlyRegisteredEquivalents(string answer, string expected)
    {
        var ai = new AnswerAi(answer);
        var pipeline = new TextAiPipeline(ai, new());
        Assert.Equal(expected, await pipeline.ClassifyAsync("MIT license", default));
        Assert.Equal(1, ai.Calls);
        Assert.Contains("category_id_normalized", pipeline.Notices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLicenseTypeFallsBackOnlyWhenNoExclusionIsPossible(bool exclusion)
    {
        var options = new TextRagSettings();
        if (exclusion) options = options with { Categories = [..options.Categories, new() { Id = "excluded", Excluded = true }] };
        var ai = new AnswerAi("license");
        var pipeline = new TextAiPipeline(ai, options);
        if (exclusion) await Assert.ThrowsAsync<PipelineValidationException>(() => pipeline.ClassifyAsync("MIT license", default));
        else
        {
            Assert.Equal("general", await pipeline.ClassifyAsync("MIT license", default));
            Assert.Contains("category_default_unknown", pipeline.Notices);
        }
        Assert.Equal(2, ai.Calls); // Existing immediate correction remains bounded at one retry.
        Assert.Contains("defaultCategory", ai.Input!);
    }

    private sealed class AnswerAi(string category) : ITextRagAi
    {
        public int Calls { get; private set; }
        public string? Input { get; private set; }
        public Task<string> CompleteAsync(string s, string i, CancellationToken t)
        { Calls++; Input = i; return Task.FromResult(JsonSerializer.Serialize(new { category })); }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> i, CancellationToken t) => throw new NotSupportedException();
    }
}
