using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class WordIntegrationTests
{
    private static AppSettings Settings(string input) => TextRagTests.Settings(input) with
    {
        Management = ManagementSettings.CreateDefault() with { Routes = [ManagementSettings.CreateWordRoute() with {
            ClassifyEnabled = false, ParentEmbedding = ParentEmbeddingMode.None, MarkdownParentMinCharacters = 0 }],
            Retrieval = new() { ConfidentialityEnabled = false } }
    };

    [ServerFact]
    public async Task WordIngestSearchFetchAndModelChangeReuseStructure()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "guide.docx");
        await File.WriteAllBytesAsync(path, WordTests.Package(WordTests.Paragraph("庭の水やり", "<w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr>") +
            WordTests.Paragraph("花壇の水やりは朝に行います。") + WordTests.Paragraph("肥料", "<w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr>") + WordTests.Paragraph("肥料は月に一度与えます。")));
        var settings = Settings(input); var ai = new WordAi();
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using (var service = new TextRagService(repo, ai, settings, Path.Combine(f.Root, "data")))
        {
            var cycle = await service.ScanAsync(); Assert.Equal(0, cycle.Failed); Assert.Equal(1, cycle.Indexed);
            var document = Assert.Single(await repo.DocumentsAsync(default));
            Assert.Equal("ready", document.Status); Assert.Equal(2, document.Parents!.Count);
            Assert.Equal(4, document.WordRepair!.NextBlock); Assert.NotNull(document.WordDisclosureText);
            Assert.Contains("水やり", (await service.FetchAsync(document.DocumentId))!.Content);
            Assert.Contains(await service.SearchAsync("花壇の水やり"), hit => hit.Content!.Contains("水やり"));
            var calls = ai.Calls; var embeddings = ai.Embeddings;
            Assert.Equal(1, (await service.ScanAsync()).Unchanged); Assert.Equal(calls, ai.Calls); Assert.Equal(embeddings, ai.Embeddings);
        }
        var changed = settings with { Ai = settings.Ai with { EmbeddingModel = "new-embedding" } }; var after = new WordAi();
        using (var service = new TextRagService(repo, after, changed, Path.Combine(f.Root, "data")))
        {
            Assert.Equal(1, (await service.ScanAsync()).Indexed); Assert.Equal(0, after.Calls); Assert.True(after.Embeddings > 0);
            var id = Assert.Single(await repo.DocumentsAsync(default)).DocumentId;
            await File.WriteAllBytesAsync(path, WordTests.Package(WordTests.Paragraph("更新後")));
            Assert.Null(await service.FetchAsync(id));
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
        }
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedHeaderLabelsBlockBothSearchAndFetch(bool custom)
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var label = custom ? "PROJECT-RESTRICTED" : "極秘";
        await File.WriteAllBytesAsync(Path.Combine(input, "private.docx"), WordTests.Package(WordTests.Paragraph("本文の情報"), header: WordTests.Paragraph(label)));
        var settings = Settings(input);
        settings = settings with { Management = settings.Management! with { Retrieval = new() {
            ConfidentialityEnabled = true, PatternsEnabled = false, BoundaryPatternsEnabled = true, LeadingLines = 5,
            BoundaryPatterns = [label], LlmEnabled = false } } };
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using var service = new TextRagService(repo, new WordAi(), settings, Path.Combine(f.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var doc = Assert.Single(await repo.DocumentsAsync(default));
        var fetched = await service.FetchAsync(doc.DocumentId);
        Assert.Equal(DisclosureDecision.Withheld, fetched!.Decision); Assert.Null(fetched.Content); Assert.EndsWith("private.docx", fetched.SourcePath);
        var hits = await service.SearchAsync("情報");
        Assert.NotEmpty(hits); Assert.All(hits, h => { Assert.Equal(DisclosureDecision.Withheld, h.Decision); Assert.Null(h.Content); });
    }

    [ServerFact]
    public async Task InvalidWordIsHeldAndDoesNotBlockFollowingFiles()
    {
        await using var f = await ServerFixture.StartAsync();
        var input = Path.Combine(f.Root, "input"); Directory.CreateDirectory(input);
        var bad = Path.Combine(input, "bad.docx");
        await File.WriteAllBytesAsync(bad, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(input, "good.docx"), WordTests.Package(WordTests.Paragraph("普通の本文")));
        await using var repo = await TextRagRepository.OpenAsync(f.Manager.Connection!);
        using var service = new TextRagService(repo, new WordAi(), Settings(input), Path.Combine(f.Root, "data"));
        var first = await service.ScanAsync(); Assert.Equal(1, first.Failed); Assert.Equal(1, first.Indexed);
        var doc = (await repo.DocumentsAsync(default)).Single(d => d.Path == bad);
        Assert.Equal("word_invalid", doc.ErrorDetailCode); Assert.Null(doc.Failure!.NextAttemptAtUtc);
        Assert.Equal(0, (await service.ScanAsync()).Failed);
        await File.WriteAllBytesAsync(bad, WordTests.Package(WordTests.Paragraph("修復された本文")));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
    }
}
