using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class MarkdownParentBoundsTests
{
    private static string Section(string title, int length) => ("## " + title + "\n\n").PadRight(length - 2, 'x') + "\n\n";

    [Fact]
    public async Task InitialOrderAndBoundsAreSavedWithoutOverwritingExistingSettings()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var settings = await store.LoadAsync();
        var routes = settings.Management!.Routes!;
        Assert.Equal(SourceLanguages.All.Select(l => l.Mode), routes.Take(SourceLanguages.All.Count).Select(r => r.SourceMode));
        Assert.Equal(new[] { ".md", ".markdown" }, routes[^2].Extensions);
        Assert.Equal(new[] { ".txt" }, routes[^1].Extensions);
        Assert.Equal(ChildChunkMode.Llm, settings.PipelineFor("a.txt").EffectiveChildMode);
        Assert.Equal(routes.SelectMany(r => r.Extensions!).Count(), routes.SelectMany(r => r.Extensions!).Distinct().Count());
        Assert.Equal(500, settings.PipelineFor("a.md").MarkdownParentMinCharacters);
        Assert.Equal(4000, settings.PipelineFor("a.md").MarkdownParentMaxCharacters);
        Assert.Equal(ParentChunkMode.Structure, settings.PipelineFor("a.md").EffectiveParentMode);
        routes[^2] = routes[^2] with { MarkdownParentMinCharacters = 200, MarkdownParentMaxCharacters = 800 };
        await store.SaveAsync(settings);
        var read = await store.LoadAsync();
        Assert.Equal(200, read.PipelineFor("a.md").MarkdownParentMinCharacters);
        Assert.Equal(800, read.PipelineFor("a.md").MarkdownParentMaxCharacters);
        Assert.Empty(read.Validate());
        var legacy = ManagementSettings.FromLegacy(new(), new()).ToOrderedSettings();
        Assert.Equal(0, legacy.Resolve(new(), "a.md").MarkdownParentMinCharacters);
        Assert.Equal(262144, legacy.Resolve(new(), "a.md").MarkdownParentMaxCharacters);
    }

    [Theory]
    [InlineData(500, 1000, new[] { 200, 200, 200, 100 }, new[] { 600, 100 })]
    [InlineData(500, 1000, new[] { 300, 800 }, new[] { 300, 800 })]
    [InlineData(500, 1000, new[] { 200, 300, 500 }, new[] { 500, 500 })]
    [InlineData(500, 1000, new[] { 200, 800 }, new[] { 1000 })]
    [InlineData(500, 1000, new[] { 100 }, new[] { 100 })]
    [InlineData(0, 1000, new[] { 100, 100 }, new[] { 100, 100 })]
    public void ConsecutiveSectionsFollowMinimumUntilMaximumWouldBeExceeded(int minimum, int maximum, int[] lengths, int[] expected)
    {
        var original = string.Concat(lengths.Select((size, i) => Section(i.ToString(), size)));
        var source = TextStructure.Build(original, "test.md", 2, minimum, maximum);
        Assert.Equal(expected, source.Parents.Select(p => p.Text.Length));
        Assert.Equal(original, string.Concat(source.Parents.Select(p => p.Text)));
        Assert.Equal(Enumerable.Range(1, source.Paragraphs.Count), source.Parents.SelectMany(p => Enumerable.Range(p.Start, p.End - p.Start + 1)));
    }

    [Theory]
    [InlineData("## Long\r\n\r\n", "paragraph 🌸 text\r\n\r\n")]
    [InlineData("## Long\n", "without-break-🌸")]
    [InlineData("## Long\n\n```text\n", "# this is not a heading\n")]
    public void OversizeSectionsSplitWithoutDroppingTextOrBreakingUnicode(string prefix, string body)
    {
        var original = prefix + string.Concat(Enumerable.Repeat(body, 80));
        var source = TextStructure.Build(original, "test.md", 2, 90, 120);
        Assert.True(source.Parents.Count > 1);
        Assert.Equal(original, string.Concat(source.Parents.Select(p => p.Text)));
        Assert.All(source.Parents, p =>
        {
            Assert.InRange(p.Text.Length, 1, 120);
            Assert.False(char.IsLowSurrogate(p.Text[0]));
            Assert.False(char.IsHighSurrogate(p.Text[^1]));
            Assert.False(p.Text.EndsWith('\r'));
        });
    }

    [Fact]
    public void ParagraphBreaksArePreferredAndOtherModesRemainUnbounded()
    {
        var original = "## A\n\n" + new string('a', 60) + "\n\n" + new string('b', 60);
        var settings = new TextRagSettings { MarkdownParentMinCharacters = 50, MarkdownParentMaxCharacters = 100 };
        var source = TextStructure.Build(original, "test.md", settings);
        Assert.Equal("## A\n\n" + new string('a', 60) + "\n\n", source.Parents[0].Text);
        Assert.Equal(original, Assert.Single(TextStructure.Build(original, "test.txt", settings).Parents).Text);
        Assert.Equal(original, Assert.Single(TextStructure.Build(original, "test.md", settings with { ParentMode = ParentChunkMode.None }).Parents).Text);
        Assert.Equal(original, Assert.Single(TextStructure.Build(original, "test.md", settings with { ParentMode = ParentChunkMode.Llm }).Parents).Text);
        Assert.NotEqual(settings.ConfigurationId(new()), (settings with { MarkdownParentMinCharacters = 51 }).ConfigurationId(new()));
        Assert.NotEqual(settings.ConfigurationId(new()), (settings with { MarkdownParentMaxCharacters = 101 }).ConfigurationId(new()));
        foreach (var bounds in new[] { (-1, 100), (50, 49), (101, 100), (0, 262145) })
        {
            Assert.NotEmpty((settings with { MarkdownParentMinCharacters = bounds.Item1, MarkdownParentMaxCharacters = bounds.Item2 }).Validate());
            var management = ManagementSettings.CreateDefault();
            Assert.NotEmpty((management with { Routes = [management.Routes![^2] with {
                MarkdownParentMinCharacters = bounds.Item1, MarkdownParentMaxCharacters = bounds.Item2 }] }).Validate());
        }
    }

    [Fact]
    public async Task ProjectGuideHasBoundedParentsAndEveryChildReferencesItsOriginalParent()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LocalRAG.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var original = await File.ReadAllTextAsync(Path.Combine(root.FullName, "Documents", "SettingsGuide.md"));
        var settings = ManagementSettings.CreateDefault().Resolve(new(), "guide.md");
        var source = TextStructure.Build(original, "guide.md", settings);
        Assert.Equal(original, string.Concat(source.Parents.Select(p => p.Text)));
        Assert.All(source.Parents, p => Assert.InRange(p.Text.Length, 1, 4000));
        var ai = new FakeTextAi();
        var hierarchy = await TextChunking.BuildAsync(ai, new(ai, settings), settings, settings.Categories[0], "guide.md", source, source.Paragraphs, default);
        Assert.All(hierarchy.Children, child =>
        {
            var parent = Assert.Single(hierarchy.Parents!, p => p.Id == child.ParentId);
            Assert.InRange(child.Start, parent.Start, parent.End);
            Assert.InRange(child.End, parent.Start, parent.End);
        });
    }

    [ServerFact]
    public async Task SearchReturnsBoundedParentsWhileFetchKeepsWholeOriginalAndChangesInvalidateIndex()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-input";
        Directory.CreateDirectory(input);
        var original = Section("A", 100) + Section("B", 100) + Section("C", 1500);
        await File.WriteAllTextAsync(Path.Combine(input, "guide.md"), original);
        var route = ManagementSettings.CreateDefault().Routes![^2] with {
            ClassifyEnabled = false, FormatPromptId = "", MarkdownParentMinCharacters = 200, MarkdownParentMaxCharacters = 500 };
        var management = ManagementSettings.CreateDefault() with { Routes = [route], Retrieval = new() { ConfidentialityEnabled = false } };
        var settings = TextRagTests.Settings(input) with { Management = management };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, fixture.Root);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal(original, string.Concat(document.Parents!.Select(p => p.Text)));
        Assert.All(document.Parents!, p => Assert.InRange(p.Text.Length, 1, 500));
        var hits = await service.SearchAsync("検索", 20);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.InRange(h.Content!.Length, 1, 500));
        Assert.Equal(original, (await service.FetchAsync(document.DocumentId))!.Content);
        var changed = settings with { Management = management with { Routes = [route with { MarkdownParentMaxCharacters = 600 }] } };
        using var next = new TextRagService(repo, new FakeTextAi(), changed, fixture.Root);
        Assert.Null(await next.FetchAsync(document.DocumentId));
        Assert.Empty(await next.SearchAsync("検索"));
        Assert.Equal(1, (await next.ScanAsync()).Indexed);
        Assert.Equal(original, (await next.FetchAsync(document.DocumentId))!.Content);
    }
}
