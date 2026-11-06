using System.Text.Json;
using System.Text.Json.Nodes;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class BoundaryPatternTests
{
    private static Task<DisclosureResult> Inspect(string original, ConfidentialityPolicy? policy = null, string candidate = "candidate") =>
        new DisclosureGate(new NeverClassify()).EvaluateAsync(Guid.NewGuid(), original, candidate, policy ?? ConfidentialityPolicy.CreateDefault());

    [Theory]
    [InlineData("1\n2\n3\n4\nSECRET\n6", DisclosureDecision.Withheld)]
    [InlineData("1\n2\n3\n4\n5\nSECRET", DisclosureDecision.Allowed)]
    [InlineData("\n\n\n\n\n極秘", DisclosureDecision.Allowed)]
    [InlineData("丸秘\npublic body", DisclosureDecision.Withheld)]
    public async Task DefaultUsesOriginalFirstFivePhysicalLines(string original, DisclosureDecision expected)
    {
        // Returning a chunk starting with a marker must not make a body marker a header.
        var result = await Inspect(original, candidate: "SECRET");
        Assert.Equal(expected, result.Decision);
        Assert.Equal(expected == DisclosureDecision.Allowed ? "SECRET" : null, result.Content);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task FooterCountsPhysicalLinesWithoutInventingFinalEmptyLine(string newline)
    {
        var policy = ConfidentialityPolicy.CreateDefault() with { LeadingLines = 0, TrailingLines = 1 };
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("body" + newline + "極秘" + newline, policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("body" + newline + "極秘", policy)).Decision);
        var blankLastLine = "body" + newline + "極秘" + newline + newline;
        Assert.Equal(DisclosureDecision.Allowed, (await Inspect(blankLastLine, policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect(blankLastLine, policy with { TrailingLines = 2 })).Decision);
    }

    [Fact]
    public async Task DisjointRegionsAreNeverConcatenatedForMatching()
    {
        var policy = ConfidentialityPolicy.CreateDefault() with { LeadingLines = 1, TrailingLines = 1, BoundaryPatterns = ["HEAD\\s+TAIL"] };
        Assert.Equal(DisclosureDecision.Allowed, (await Inspect("HEAD\nmiddle\nTAIL", policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("HEAD\nTAIL", policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("HEAD\nTAIL", policy with { LeadingLines = int.MaxValue, TrailingLines = int.MaxValue })).Decision);
    }

    [Fact]
    public async Task FullPatternsIndependentlyInspectBodyAndCandidate()
    {
        var policy = ConfidentialityPolicy.CreateDefault() with { PatternsEnabled = true, Patterns = ["TOKEN"] };
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("1\n2\n3\n4\n5\ntoken", policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("public", policy, "TOKEN generated summary")).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("極秘", policy)).Decision);
        Assert.Equal(DisclosureDecision.Allowed, (await Inspect("極秘", policy with { BoundaryPatternsEnabled = false })).Decision);
        Assert.Equal(DisclosureDecision.Allowed, (await Inspect("public", policy with { PatternsEnabled = false }, "TOKEN")).Decision);
    }

    [Fact]
    public async Task InvalidOrEmptyEffectiveInspectionNeverReleasesData()
    {
        var defaults = ConfidentialityPolicy.CreateDefault();
        foreach (var policy in new[] {
            defaults with { LeadingLines = -1 }, defaults with { TrailingLines = -1 },
            defaults with { BoundaryPatterns = ["["] }, defaults with { BoundaryPatterns = null! },
            defaults with { LeadingLines = 0, TrailingLines = 0 }, defaults with { BoundaryPatterns = [] },
            defaults with { BoundaryPatternsEnabled = false } })
        {
            var result = await Inspect("public", policy);
            Assert.Equal(DisclosureDecision.Pending, result.Decision);
            Assert.Null(result.Content);
        }
    }

    [Fact]
    public async Task LimitedPatternsDoNotLimitOptInLlmInspection()
    {
        var classifier = new RecordingClassifier();
        const string original = "1\n2\n3\n4\n5\nprivate body";
        var result = await new DisclosureGate(classifier).EvaluateAsync(Guid.NewGuid(), original, "candidate",
            ConfidentialityPolicy.CreateDefault() with { LlmEnabled = true });
        Assert.Equal((original, "candidate"), classifier.Input);
        Assert.Equal(DisclosureDecision.Withheld, result.Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNewFieldsPreserveExistingWholeDocumentRules(bool legacy)
    {
        var store = NewStore();
        var settings = legacy ? new AppSettings { SchemaVersion = 1, Confidentiality = new() { Patterns = ["custom"] } }
            : new AppSettings { Management = ManagementSettings.CreateDefault() with { Retrieval = new() { Patterns = ["custom"] } } };
        var json = JsonSerializer.SerializeToNode(settings)!;
        var policy = (JsonObject)(legacy ? json["Confidentiality"]! : json["Management"]!["Retrieval"]!);
        foreach (var key in new[] { "BoundaryPatternsEnabled", "BoundaryPatterns", "LeadingLines", "TrailingLines" }) policy.Remove(key);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, json.ToJsonString());
        var loaded = await store.LoadAsync();
        await store.SaveAsync(loaded);
        var actual = (await store.LoadAsync()).DisclosurePolicy();
        Assert.True(actual.PatternsEnabled);
        Assert.False(actual.BoundaryPatternsEnabled);
        Assert.Equal(new[] { "custom" }, actual.Patterns);
        Assert.Equal(DisclosureDecision.Withheld, (await Inspect("1\n2\n3\n4\n5\ncustom", actual)).Decision);
    }

    [Fact]
    public async Task NewFieldsRoundTripAndInvalidSavePreservesFile()
    {
        var store = NewStore();
        var settings = await store.LoadAsync();
        var management = settings.Management!;
        var retrieval = management.Retrieval with { LeadingLines = 2, TrailingLines = 3, BoundaryPatterns = ["HEADER"], PatternsEnabled = true, Patterns = ["TOKEN"] };
        settings = settings with { Management = management with { Retrieval = retrieval } };
        await store.SaveAsync(settings);
        var actual = (await store.LoadAsync()).DisclosurePolicy();
        Assert.Equal(2, actual.LeadingLines);
        Assert.Equal(3, actual.TrailingLines);
        Assert.True(actual.BoundaryPatternsEnabled && actual.PatternsEnabled);
        Assert.Equal(new[] { "HEADER" }, actual.BoundaryPatterns);
        Assert.Equal(new[] { "TOKEN" }, actual.Patterns);
        var before = await File.ReadAllTextAsync(store.FilePath);
        foreach (var invalid in new[] { retrieval with { LeadingLines = -1 }, retrieval with { BoundaryPatterns = ["["] } })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with { Management = management with { Retrieval = invalid } }));
            Assert.Equal(before, await File.ReadAllTextAsync(store.FilePath));
        }
    }

    [ServerFact]
    public async Task SearchAndFetchUseSourceLinesAndApplyNewRulesWithoutReindexing()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "guide.txt");
        const string text = "first\n\nthird\n\nfifth\n\nSECRET body";
        await File.WriteAllTextAsync(path, text);
        var settings = TextRagTests.Settings(input) with { Confidentiality = ConfidentialityPolicy.CreateDefault() };
        var ai = new FakeTextAi();
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
        {
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            Assert.All(await service.SearchAsync("body", 10), hit => Assert.Equal(DisclosureDecision.Allowed, hit.Decision));
        }
        var doc = Assert.Single(await repo.DocumentsAsync(default));
        foreach (var policy in new[] { settings.Confidentiality,
            settings.Confidentiality with { LeadingLines = 0, TrailingLines = 1 },
            settings.Confidentiality with { PatternsEnabled = true, Patterns = ["SECRET"] } })
        {
            var expected = policy == settings.Confidentiality ? DisclosureDecision.Allowed : DisclosureDecision.Withheld;
            using var service = new TextRagService(repo, ai, settings with { Confidentiality = policy }, Path.Combine(f.Root, "data"));
            var hits = await service.SearchAsync("body", 10);
            Assert.NotEmpty(hits);
            Assert.All(hits, hit => Assert.Equal(expected, hit.Decision));
            var fetch = (await service.FetchAsync(doc.DocumentId))!;
            Assert.Equal(expected, fetch.Decision);
            Assert.Equal(expected == DisclosureDecision.Allowed ? text : null, fetch.Content);
            Assert.Equal(path, fetch.SourcePath);
            var before = (ai.TransformCalls, ai.EmbeddingCalls);
            Assert.Equal(1, (await service.ScanAsync()).Unchanged);
            Assert.Equal(before, (ai.TransformCalls, ai.EmbeddingCalls));
        }
    }

    private static SettingsStore NewStore() => new(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
    private sealed class NeverClassify : IConfidentialityClassifier
    {
        public Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken) => throw new InvalidOperationException("LLM must not run");
    }
    private sealed class RecordingClassifier : IConfidentialityClassifier
    {
        public (string, string)? Input { get; private set; }
        public Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken)
        { Input = (original, candidate); return Task.FromResult(DisclosureDecision.Withheld); }
    }
}
