using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class IngestionRecoveryTests
{
    [Fact]
    public void LiteralDotSegmentsInGitignoreDoNotInvalidateOtherRulesOrBecomeTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, ".gitignore"), "./data\n../outside\nsecret.txt\n");
        var publicFile = Path.Combine(root, "data", "guide.txt"); File.WriteAllText(publicFile, "public");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "hidden");
        var scan = TextFiles.Scan([new(root)], root + "-data", default);
        Assert.Equal(publicFile, Assert.Single(scan.Files));
        Assert.Equal(0, scan.FailedRoots);
    }

    [Fact]
    public void InvalidIgnoreInOneSubtreeDoesNotPreventScanningHealthySiblingsOrWithdrawUnseenDocuments()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bad"));
        Directory.CreateDirectory(Path.Combine(root, "good"));
        File.WriteAllText(Path.Combine(root, "bad", ".ragignore"), "[broken");
        File.WriteAllText(Path.Combine(root, "bad", "secret.txt"), "must not be discovered");
        var good = Path.Combine(root, "good", "guide.txt"); File.WriteAllText(good, "discover me");
        var scan = TextFiles.Scan([new(root)], root + "-data", default);
        Assert.Equal(good, Assert.Single(scan.Files));
        Assert.Equal(1, scan.FailedRoots);
        Assert.Empty(scan.CompletedRoots); // incomplete discovery must never imply source deletion
        Assert.Contains(scan.Failures, f => f.Code == "ignore_invalid" && f.Path.Contains("bad"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeMultibyteDocumentsAreReadWithoutTruncationAndConfiguredLimitStillApplies(bool utf32)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "large.md");
        var original = string.Concat(Enumerable.Repeat("日本語😀\n", 20000));
        await File.WriteAllTextAsync(path, original, utf32 ? Encoding.UTF32 : new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > 256 * 1024);
        var read = await TextFiles.ReadAsync(path, 262144, default);
        Assert.Equal(original, read.Text);
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() => TextFiles.ReadAsync(path, 64000, default));
        Assert.Contains(error.Code, new[] { "document_size", "file_size" });
        Assert.Equal(original, (await TextFiles.ReadAsync(path, 262144, default)).Text);
    }

    [Fact]
    public async Task EmptyAndOversizedDocumentsHaveActionableDistinctReasons()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, " \n\t");
        Assert.Equal("empty_text", (await Assert.ThrowsAsync<PipelineValidationException>(() => TextFiles.ReadAsync(path, 100, default))).Code);
        await File.WriteAllTextAsync(path, new string('a', 101));
        Assert.Equal("document_size", (await Assert.ThrowsAsync<PipelineValidationException>(() => TextFiles.ReadAsync(path, 100, default))).Code);
        await File.WriteAllTextAsync(path, new string('a', 405));
        Assert.Equal("file_size", (await Assert.ThrowsAsync<PipelineValidationException>(() => TextFiles.ReadAsync(path, 100, default))).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MixedTypesUseDefaultOnlyWhenNoExclusionDecisionIsOverridden(bool exclusionVote, bool excludedDefault)
    {
        var options = new TextRagSettings { DefaultCategory = excludedDefault ? "excluded" : "procedure" };
        options.Categories.Add(new() { Id = "excluded", Description = "検索対象外", Excluded = true });
        var ai = new MixedAi(exclusionVote, options.Categories, options.DefaultCategory);
        var pipeline = new TextAiPipeline(ai, options);
        // Two equal windows: no category has a majority.
        var source = new string('x', 1000);
        if (exclusionVote || excludedDefault)
            Assert.Equal("category_conflict", (await Assert.ThrowsAsync<PipelineValidationException>(() => pipeline.ClassifyAsync(source, default))).Code);
        else
        {
            Assert.Equal("procedure", await pipeline.ClassifyAsync(source, default));
            Assert.Contains("category_default_conflict", pipeline.Notices);
        }
        Assert.Equal(source, string.Concat(ai.Inputs));
        Assert.Equal(2, ai.Inputs.Count);
        Assert.All(ai.Inputs, text => Assert.Equal(500, text.Length));
    }

    [ServerFact]
    public async Task EmptyDocumentIsSkippedWithoutAiAndReprocessedWhenContentAppears()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "empty.txt"); await File.WriteAllTextAsync(path, " \n");
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new FakeTextAi();
        using var service = new TextRagService(repository, ai, TextRagTests.Settings(input), Path.Combine(fixture.Root, "data"));
        var cycle = await service.ScanAsync();
        Assert.Equal(0, cycle.Failed); Assert.Equal(1, cycle.Excluded);
        Assert.Equal("skipped", Assert.Single(await repository.DocumentsAsync(default)).Status);
        Assert.Equal(0, ai.TransformCalls);
        Assert.Equal(1, (await service.ScanAsync()).Unchanged);
        await File.WriteAllTextAsync(path, "public moon guide");
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Equal("ready", Assert.Single(await repository.DocumentsAsync(default)).Status);
        await File.WriteAllTextAsync(path, "");
        Assert.Equal(1, (await service.ScanAsync()).Excluded);
        Assert.Empty(await service.SearchAsync("moon"));
    }

    [ServerFact]
    public async Task InvalidCodeDescriptionsIndexStructuralFactsWithoutInventingBehaviorAndStillInspectFullSource()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "Calculator.cs");
        const string original = "public class Calculator { public int Add(int a, int b) => a + b; }";
        await File.WriteAllTextAsync(path, original);
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new InvalidDescriptionAi();
        using var service = new TextRagService(repository, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Equal(3, document.CodeUnits!.Count);
        var function = document.CodeUnits.Single(u => u.Kind == "function");
        Assert.True(function.DescriptionFallbackUsed);
        Assert.Equal(function.Code.Length, function.DescribedCharacters);
        Assert.All(document.CodeUnits.Where(u => u.Kind != "function"), unit => Assert.True(unit.DescriptionSkipped));
        Assert.Contains("code_description_structural", document.ProcessingNotices);
        Assert.All(ai.OutputReservations, n => Assert.Equal(512, n));
        Assert.Equal(6, ai.OutputReservations.Count); // one correction per unit
        Assert.All(ai.Embeddings, text => { Assert.Contains("構造情報", text); Assert.DoesNotContain("=> a + b", text); Assert.DoesNotContain("made-up", text); });
        Assert.All(await service.SearchAsync("Calculator", 10), hit => Assert.Contains("説明未作成", hit.Content!));
        Assert.Equal(original, (await service.FetchAsync(document.DocumentId))!.Content);
        var hidden = settings with { Management = settings.Management! with { Retrieval = new() { Patterns = [@"a \+ b"] } } };
        using var blocked = new TextRagService(repository, ai, hidden, Path.Combine(fixture.Root, "data"));
        var withheld = await blocked.FetchAsync(document.DocumentId);
        Assert.Equal(DisclosureDecision.Withheld, withheld!.Decision);
        Assert.Null(withheld.Content);
        Assert.Equal(path, withheld.SourcePath);
        Assert.Equal(1, (await service.ScanAsync()).Unchanged); // no endless model retries for an indexed fallback
        await File.WriteAllTextAsync(Path.Combine(input, ".ragignore"), "Calculator.cs\n");
        Assert.Equal(1, (await service.ScanAsync()).Deleted);
        document = Assert.Single(await repository.DocumentsAsync(default));
        Assert.Equal("ignored", document.WithdrawalReason);
        Assert.True(File.Exists(path));
        Assert.Null(await service.FetchAsync(document.DocumentId));
    }

    private sealed class MixedAi(bool exclusion, IReadOnlyList<TextCategory> configuredCategories, string defaultCategory) : ITextRagAi
    {
        public List<string> Inputs { get; } = [];
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token)
        {
            var categories = configuredCategories.Select(c => new { c.Id, c.Description });
            var overhead = TextAiPipeline.InputBytes(new { categories, defaultCategory, document = "" });
            return Task.FromResult(overhead + 500 + 160);
        }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            using var json = System.Text.Json.JsonDocument.Parse(input);
            Inputs.Add(json.RootElement.GetProperty("document").GetString()!);
            var id = Inputs.Count == 1 ? "general" : exclusion ? "excluded" : "specification";
            return Task.FromResult("{\"category\":\"" + id + "\"}");
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class InvalidDescriptionAi : ITextRagAi
    {
        public List<string> Embeddings { get; } = [];
        public List<int> OutputReservations { get; } = [];
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => Task.FromResult("{\"description\":{\"made-up\":true}}");
        public Task<string> CompleteBoundedAsync(string system, string input, int outputTokens, CancellationToken token)
        { OutputReservations.Add(outputTokens); return CompleteAsync(system, input, token); }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        { Embeddings.AddRange(inputs); return Task.FromResult(new VectorBatch(3, inputs.Select(_ => new float[] { 1, 0.1f, 0 }).ToArray())); }
    }
}
