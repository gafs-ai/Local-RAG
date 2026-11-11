using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class PdfIntegrationTests
{
    private static AppSettings Settings(string input) => TextRagTests.Settings(input) with {
        Management = ManagementSettings.CreateDefault() with { Routes = [ManagementSettings.CreatePdfRoute() with {
            ClassifyEnabled = false, WordRepairWithLlm = false, ParentEmbedding = ParentEmbeddingMode.None, MarkdownParentMinCharacters = 0 }],
            Retrieval = new() { ConfidentialityEnabled = false } } };

    [ServerFact]
    public async Task PdfIngestSearchFetchCacheAndPolicyChanges()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "garden.pdf"); await File.WriteAllBytesAsync(path, PdfTests.Document());
        var settings = Settings(input);
        settings = settings with { Management = settings.Management! with { Routes = [settings.Management!.Routes![0] with { WordRepairWithLlm = true }] } };
        var ai = new WordAi { Response = "invalid json" };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
        {
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            var doc = Assert.Single(await repo.DocumentsAsync(default));
            Assert.Equal("ready", doc.Status); Assert.NotNull(doc.PdfAssessment);
            Assert.Contains("pdf_repair_preserved_invalid", doc.ProcessingNotices);
            Assert.Contains("PDFページ 4", (await service.FetchAsync(doc.DocumentId))!.Content);
            var hits = await service.SearchAsync("flowers garden water"); Assert.NotEmpty(hits);
            Assert.Contains(hits, h => h.Content!.Contains("Garden"));
            var embeddings = ai.Embeddings; Assert.Equal(1, (await service.ScanAsync()).Unchanged); Assert.Equal(embeddings, ai.Embeddings);
        }
        var changedModel = settings with { Ai = settings.Ai with { EmbeddingModel = "replacement-embedding" } };
        var after = new WordAi();
        using (var service = new TextRagService(repo, after, changedModel, Path.Combine(f.Root, "data")))
        {
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            Assert.Equal(0, after.Calls); Assert.True(after.Embeddings > 0);
            Assert.Contains("pdf_repair_preserved_invalid", Assert.Single(await repo.DocumentsAsync(default)).ProcessingNotices);
        }
        var skip = settings with { Management = settings.Management! with { Routes = [settings.Management!.Routes![0] with { Pdf = new() { Policy = PdfInputPolicy.Skip } }] } };
        using (var service = new TextRagService(repo, ai, skip, Path.Combine(f.Root, "data")))
        {
            Assert.Equal(1, (await service.ScanAsync()).Excluded);
            var doc = Assert.Single(await repo.DocumentsAsync(default)); Assert.Equal("skipped", doc.Status);
            Assert.Empty(doc.Original); Assert.Null(await service.FetchAsync(doc.DocumentId)); Assert.Empty(await service.SearchAsync("garden"));
            Assert.Equal(1, (await service.ScanAsync()).Unchanged);
        }
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
    }

    [ServerFact]
    public async Task SamplingSkipIsStableAndOverrideCanReprocess()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "short.pdf"), PdfTests.Document(1, _ => "short"));
        await File.WriteAllBytesAsync(Path.Combine(input, "broken.pdf"), [1, 2, 3]);
        var settings = Settings(input); var ai = new WordAi();
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
        {
            var scan = await service.ScanAsync(); Assert.Equal(1, scan.Excluded); Assert.Equal(1, scan.Failed);
            Assert.Equal(0, ai.Calls); Assert.Equal(0, ai.Embeddings);
            var held = (await repo.DocumentsAsync(default)).Single(d => d.Status == "failed"); Assert.Null(held.Failure!.NextAttemptAtUtc);
            Assert.Equal(0, (await service.ScanAsync()).Failed);
        }
        var changed = settings with { Management = settings.Management! with { Routes = [settings.Management!.Routes![0] with { Pdf = new() { Policy = PdfInputPolicy.ExtractText } }] } };
        using (var service = new TextRagService(repo, ai, changed, Path.Combine(f.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
    }

    [ServerFact]
    public async Task CustomRemovedHeaderStillProtectsSearchAndFetch()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "protected.pdf"), PdfTests.Document(header: "PROJECT-RESTRICTED"));
        var settings = Settings(input); settings = settings with { Management = settings.Management! with { Retrieval = new() {
            ConfidentialityEnabled = true, BoundaryPatternsEnabled = true, PatternsEnabled = false, BoundaryPatterns = ["PROJECT-RESTRICTED"], LlmEnabled = false } } };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using var service = new TextRagService(repo, new WordAi(), settings, Path.Combine(f.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var doc = Assert.Single(await repo.DocumentsAsync(default)); Assert.DoesNotContain("PROJECT-RESTRICTED", doc.Original);
        Assert.Equal(DisclosureDecision.Withheld, (await service.FetchAsync(doc.DocumentId))!.Decision);
        var hits = await service.SearchAsync("garden"); Assert.NotEmpty(hits);
        Assert.All(hits, h => { Assert.Equal(DisclosureDecision.Withheld, h.Decision); Assert.Null(h.Content); });
    }
}
