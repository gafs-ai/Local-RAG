using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class TextRagTests
{
    [Theory]
    [InlineData("allowed", DisclosureDecision.Allowed)]
    [InlineData("allow", DisclosureDecision.Allowed)]
    [InlineData("withhold", DisclosureDecision.Withheld)]
    [InlineData("pending", DisclosureDecision.Pending)]
    public async Task ConfidentialityAcceptsCurrentAndLegacyDecisionValues(string value, DisclosureDecision expected)
    {
        var ai = new DecisionAi(value);
        var decision = await new TextConfidentialityClassifier(ai, new()).InspectAsync("original", "candidate", default);
        Assert.Equal(expected, decision);
        Assert.Equal(expected == DisclosureDecision.Allowed ? 2 : 1, ai.Calls);
    }

    [Fact]
    public async Task InvalidDecisionIsRetriedAndNeverSilentlyAllowed()
    {
        var repaired = new DecisionAi("unknown", "allowed");
        Assert.Equal(DisclosureDecision.Allowed,
            await new TextConfidentialityClassifier(repaired, new()).InspectAsync("original", "candidate", default));
        Assert.Equal(3, repaired.Calls);
        var invalid = new DecisionAi("unknown");
        await Assert.ThrowsAsync<PipelineValidationException>(() =>
            new TextConfidentialityClassifier(invalid, new()).InspectAsync("original", "candidate", default));
        Assert.Equal(2, invalid.Calls);
    }

    private sealed class DecisionAi(params string[] decisions) : ITextRagAi
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            Assert.StartsWith("ドキュメントの機密性を判定してください。", system);
            return Task.FromResult(JsonSerializer.Serialize(new { decision = decisions[Math.Min(Calls++, decisions.Length - 1)] }));
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CustomDefaultCategoryAndDisabledStagesKeepExactTextWithoutLlmCalls()
    {
        var ai = new FakeTextAi();
        var category = new TextCategory { Id = "custom", Description = "独自分類", FormatEnabled = false, ChunkEnabled = false };
        var settings = new TextRagSettings { ClassifyEnabled = false, DefaultCategory = "custom", Categories = [category] };
        Assert.Empty(settings.Validate());
        var pipeline = new TextAiPipeline(ai, settings);
        Assert.Equal("custom", await pipeline.ClassifyAsync("source", default));
        var paragraphs = await pipeline.FormatAsync([new(1, "source A"), new(2, "source B")], category, default);
        Assert.Equal("source A\n\nsource B", Assert.Single(await pipeline.ChunkAsync(paragraphs, category, default)).Text);
        Assert.Equal(0, ai.TransformCalls);
        var longChunks = await pipeline.ChunkAsync([new(1, new string('a', 1201))], category, default);
        Assert.Equal(new string('a', 1201), string.Concat(longChunks.Select(c => c.Text)));
        Assert.All(longChunks, c => Assert.True(c.Text.Length <= 1200));
    }

    [Fact]
    public async Task ClassifierInspectsTheWholeDocumentAndDoesNotTrustUnknownDecisions()
    {
        var ai = new FragmentAi();
        var classifier = new TextConfidentialityClassifier(ai, new());
        var decision = await classifier.InspectAsync(new string('x', 1700) + "restricted", "safe", default);
        Assert.Equal(DisclosureDecision.Withheld, decision);
        Assert.True(ai.Calls > 1);
    }

    private sealed class FragmentAi : ITextRagAi
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(input.Contains("restricted", StringComparison.Ordinal) ? "{\"decision\":\"withhold\"}" : "{\"decision\":\"allow\"}");
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }
    [Fact]
    public void JsonFenceIsAcceptedButAmbiguousOrSurroundingTextIsRejected()
    {
        using var parsed = TextAiPipeline.ParseResponse("```json\n{\"category\":\"general\"}\n```");
        Assert.Equal("general", parsed.RootElement.GetProperty("category").GetString());
        Assert.ThrowsAny<JsonException>(() => TextAiPipeline.ParseResponse("Here is JSON: {\"category\":\"general\"}"));
        Assert.Throws<PipelineValidationException>(() => TextAiPipeline.ParseResponse("{\"decision\":\"withhold\",\"decision\":\"allow\"}"));
    }
    [Theory]
    [InlineData("{\"chunks\":[{\"start\":2,\"end\":2}]}")]
    [InlineData("{\"chunks\":[{\"start\":1,\"end\":1},{\"start\":1,\"end\":2}]}")]
    [InlineData("{\"chunks\":[{\"start\":1,\"end\":99}]}")]
    [InlineData("{\"chunks\":[]}")]
    public void ChunkRangesCannotLoseDuplicateOrInventParagraphs(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        Assert.Throws<PipelineValidationException>(() => TextAiPipeline.ValidateChunks(parsed.RootElement, [new(1, "first"), new(2, "second")], 100));
    }

    [Fact]
    public void ChunkTextComesFromSourceIncludingNonConsecutiveIdsAfterFormatting()
    {
        using var parsed = JsonDocument.Parse("{\"chunks\":[{\"start\":1,\"end\":3}]}");
        var chunks = TextAiPipeline.ValidateChunks(parsed.RootElement, [new(1, "a"), new(3, "b")], 100);
        Assert.Equal("a\n\nb", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task ScannerSkipsOwnDataAndUnreadableRootDoesNotBecomeComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "state"));
        await File.WriteAllTextAsync(Path.Combine(root, "hello.md"), "日本語");
        await File.WriteAllTextAsync(Path.Combine(root, "state", "private.txt"), "internal");
        await File.WriteAllTextAsync(Path.Combine(root, "~$word.txt"), "temporary");
        var scan = TextFiles.Scan([new(root), new(Path.Combine(root, "missing"))], Path.Combine(root, "state"), default);
        Assert.Single(scan.Files);
        Assert.Single(scan.CompletedRoots);
        Assert.Equal(1, scan.FailedRoots);
        var read = await TextFiles.ReadAsync(scan.Files[0], 100, default);
        Assert.Equal("日本語", read.Text);
        await Assert.ThrowsAsync<PipelineValidationException>(() => TextFiles.ReadAsync(scan.Files[0], 2, default));
    }

    [ServerFact]
    public async Task RealRepositoryIndexesFiltersChangesAndReturnsPathWithoutWithheldContent()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        Directory.CreateDirectory(input);
        var publicFile = Path.Combine(input, "moon.txt");
        var secretFile = Path.Combine(input, "secret.md");
        await File.WriteAllTextAsync(publicFile, "moon mission guide\n\nlaunch procedures are public");
        await File.WriteAllTextAsync(secretFile, "社外秘\n\nmoon confidential engineering design details");
        await File.WriteAllTextAsync(Path.Combine(input, "excluded.txt"), "検索対象外の文書");
        var settings = Settings(input);
        settings.TextPipeline.Categories.Add(new() { Id = "excluded", Description = "検索対象外", Excluded = true });
        var ai = new FakeTextAi();
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, ai, settings, fixture.Root);
        var cycle = await service.ScanAsync();
        Assert.Equal(2, cycle.Indexed);
        Assert.Equal(1, cycle.Excluded);
        Assert.Equal(0, cycle.Failed);
        var documents = await repository.DocumentsAsync(default);
        var publicDocument = documents.Single(x => x.Path == publicFile);
        var secret = documents.Single(x => x.Path == secretFile);
        Assert.DoesNotContain("社外秘", secret.Chunks!.Select(x => x.Text));
        var hits = await service.SearchAsync("moon", 10);
        Assert.Contains(hits, h => h.DocumentId == publicDocument.DocumentId && h.Content is not null);
        var blocked = Assert.Single(hits, h => h.DocumentId == secret.DocumentId);
        Assert.Equal(DisclosureDecision.Withheld, blocked.Decision);
        Assert.Null(blocked.Content);
        Assert.Equal(secretFile, blocked.SourcePath);
        Assert.Null(blocked.StartParagraph);
        var secretFetch = (await service.FetchAsync(secret.DocumentId))!;
        Assert.Null(secretFetch.Content);
        Assert.Equal(secretFile, secretFetch.SourcePath);
        Assert.DoesNotContain(hits, h => documents.Single(d => d.DocumentId == h.DocumentId).Status == "excluded");

        // Changed prompts share the same embedding table, but old configuration rows cannot occupy result slots.
        var changedPrompt = settings with { TextPipeline = settings.TextPipeline with { FormatPrompt = "new formatting policy" } };
        using (var otherConfiguration = new TextRagService(repository, ai, changedPrompt, fixture.Root))
            Assert.Empty(await otherConfiguration.SearchAsync("moon", 10));

        Assert.Equal(3, (await service.ScanAsync()).Unchanged);
        await File.WriteAllTextAsync(publicFile, "updated recipe guide");
        Assert.Null(await service.FetchAsync(publicDocument.DocumentId)); // Before the next scheduled scan.
        Assert.DoesNotContain(await service.SearchAsync("moon", 10), h => h.DocumentId == publicDocument.DocumentId);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Contains("updated", (await service.FetchAsync(publicDocument.DocumentId))!.Content);
        var changed = (await repository.GetAsync(publicDocument.Key, default))!;
        Assert.NotEqual(publicDocument.RevisionId, changed.RevisionId);

        File.Delete(publicFile);
        Assert.Null(await service.FetchAsync(publicDocument.DocumentId));
        Assert.Equal(1, (await service.ScanAsync()).Deleted);

        var different = settings with { TextPipeline = settings.TextPipeline with { EmbeddingRevision = "new-model-version" } };
        using var newGeneration = new TextRagService(repository, ai, different, fixture.Root);
        Assert.Empty(await newGeneration.SearchAsync("moon"));
    }

    [ServerFact]
    public async Task PersistedStagesResumeAfterEmbeddingFailureAndModelFailureIsClosed()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "guide.txt"), "moon first section\n\nmoon second section");
        var settings = Settings(input);
        var ai = new FakeTextAi { FailEmbeddingCall = 2 };
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        using (var service = new TextRagService(repository, ai, settings, fixture.Root))
        {
            Assert.Equal(1, (await service.ScanAsync()).Failed);
            var failed = Assert.Single(await repository.DocumentsAsync(default));
            Assert.Equal(1, failed.EmbeddedChunks);
            Assert.Equal("failed", failed.Status);
            Assert.Equal("embedding", failed.ErrorStage);
            Assert.Equal("connection", failed.ErrorDetailCode);
            Assert.DoesNotContain("simulated interruption", JsonSerializer.Serialize(failed));
            Assert.Empty(await service.SearchAsync("moon"));
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using var reopened = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var resumedAi = new FakeTextAi();
        using var resumed = new TextRagService(reopened, resumedAi, settings, fixture.Root);
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        Assert.Equal(0, resumedAi.TransformCalls);
        Assert.Equal(1, resumedAi.EmbeddingCalls);
        resumedAi.InspectionFails = true;
        var hit = Assert.Single(await resumed.SearchAsync("moon", 1));
        Assert.Equal(DisclosureDecision.Pending, hit.Decision);
        Assert.Null(hit.Content);
        Assert.Equal(Path.Combine(input, "guide.txt"), hit.SourcePath);
        var pendingFetch = (await resumed.FetchAsync(hit.DocumentId))!;
        Assert.Equal(DisclosureDecision.Pending, pendingFetch.Decision);
        Assert.Null(pendingFetch.Content);
        Assert.Equal(hit.SourcePath, pendingFetch.SourcePath);
    }

    [ServerFact]
    public async Task CancellationLeavesOnlyCompletedChunksAndResumesWithoutReprocessing()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "cancel.txt"), "first section\n\nsecond section");
        var settings = Settings(input);
        using var cancelled = new CancellationTokenSource();
        var ai = new FakeTextAi { BeforeEmbedding = call => { if (call == 2) cancelled.Cancel(); } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var interrupted = new TextRagService(repository, ai, settings, fixture.Root))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.ScanAsync(token: cancelled.Token));
        var state = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Equal("processing", state.Status);
        Assert.Equal(1, state.EmbeddedChunks);
        var resumedAi = new FakeTextAi();
        using var resumed = new TextRagService(repository, resumedAi, settings, fixture.Root);
        Assert.Null(await resumed.FetchAsync(state.DocumentId));
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        Assert.Equal(0, resumedAi.TransformCalls);
        Assert.Equal(1, resumedAi.EmbeddingCalls);
    }

    internal static AppSettings Settings(string input) => new()
    {
        UpdateExclusionMinutes = 0, // Synthetic fixtures are intentionally processed immediately.
        Confidentiality = new() { LlmEnabled = true }, // Exercise the opt-in inspection path in integration tests.
        Ai = new() { ChatModel = "fake-chat", EmbeddingModel = "fake-embedding" },
        Folders = [new(input)]
    };
}

internal sealed class FakeTextAi : ITextRagAi
{
    public bool InvalidChunkEnd { get; init; }
    public int TransformCalls { get; private set; }
    public int EmbeddingCalls { get; private set; }
    public int FailEmbeddingCall { get; init; }
    public bool InspectionFails { get; set; }
    public Action<int>? BeforeEmbedding { get; init; }
    public Task<string> CompleteAsync(string system, string input, CancellationToken token)
    {
        using var parsed = JsonDocument.Parse(input);
        var root = parsed.RootElement;
        if (root.TryGetProperty("language", out _)) return Task.FromResult("{\"description\":\"文書を検索して取得するインターフェースです。\"}");
        if (root.TryGetProperty("fragment", out _))
        {
            if (InspectionFails) throw new HttpRequestException("provider private details");
            return Task.FromResult("{\"decision\":\"allow\"}");
        }
        TransformCalls++;
        if (root.TryGetProperty("document", out var document))
            return Task.FromResult(document.GetString()!.Contains("検索対象外") ? "{\"category\":\"excluded\"}" : "{\"category\":\"general\"}");
        var paragraphs = root.GetProperty("paragraphs").EnumerateArray().ToArray();
        if (InvalidChunkEnd && root.TryGetProperty("maxCharacters", out _))
            return Task.FromResult("{\"chunks\":[{\"start\":1,\"end\":99}]}");
        return Task.FromResult(root.TryGetProperty("maxCharacters", out _)
            ? JsonSerializer.Serialize(new { chunks = paragraphs.Select(p => new { start = p.GetProperty("Id").GetInt32(), end = p.GetProperty("Id").GetInt32() }) })
            : JsonSerializer.Serialize(new { remove = paragraphs.Where(p => p.GetProperty("Text").GetString() == "社外秘").Select(p => p.GetProperty("Id").GetInt32()) }));
    }
    public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
    {
        EmbeddingCalls++;
        BeforeEmbedding?.Invoke(EmbeddingCalls);
        token.ThrowIfCancellationRequested();
        if (EmbeddingCalls == FailEmbeddingCall) throw new HttpRequestException("simulated interruption");
        return Task.FromResult(new VectorBatch(3, inputs.Select(s => s.Contains("recipe") ? new float[] { 0, 1, 0 } : new float[] { 1, 0.1f, 0 }).ToArray()));
    }
}
