using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class HiddenEntryTests
{
    [Fact]
    public async Task DefaultExclusionAndExplicitOptOutSurviveSettingsRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root);
        var defaults = await store.LoadAsync();
        Assert.True(defaults.ExcludeDotHiddenEntries);
        var inclusive = defaults with { ExcludeDotHiddenEntries = false };
        await store.SaveAsync(inclusive);
        Assert.False((await store.LoadAsync()).ExcludeDotHiddenEntries);
        Assert.NotEqual(MonitoringCheckpoint.ScopeFor(defaults), MonitoringCheckpoint.ScopeFor(inclusive));
        Assert.Equal(defaults.PipelineConfigurationId(), inclusive.PipelineConfigurationId());
        await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":1}");
        Assert.True((await store.LoadAsync()).ExcludeDotHiddenEntries);
    }

    [Fact]
    public async Task ScannerExcludesDotEntriesButHonorsExplicitRootsAndOptOut()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var hidden = Path.Combine(root, ".git");
        var descendant = Path.Combine(hidden, "nested");
        Directory.CreateDirectory(descendant);
        var normal = Path.Combine(root, "version.1.txt");
        var dotFile = Path.Combine(root, ".notes.txt");
        var nestedFile = Path.Combine(descendant, "guide.md");
        var ownData = Path.Combine(root, ".state");
        Directory.CreateDirectory(ownData);
        foreach (var path in new[] { normal, dotFile, nestedFile, Path.Combine(ownData, "internal.txt") })
            await File.WriteAllTextAsync(path, "public text");
        // The requested rule concerns the dot prefix, not the Windows Hidden attribute.
        File.SetAttributes(normal, File.GetAttributes(normal) | FileAttributes.Hidden);
        try
        {
            WatchedFolder[] folders = [new(root), new(hidden, Priority: 99), new(descendant)];
            var excluded = TextFiles.Scan(folders, ownData, default);
            Assert.Equal(2, excluded.Files.Count);
            Assert.Contains(normal, excluded.Files);
            Assert.Contains(nestedFile, excluded.Files);
            Assert.Equal(3, excluded.CompletedRoots.Count);
            Assert.Equal(0, excluded.FailedRoots);
            var included = TextFiles.Scan(folders, ownData, default, excludeDotHiddenEntries: false);
            Assert.Equal(3, included.Files.Count);
            Assert.Contains(dotFile, included.Files);
            Assert.Contains(nestedFile, included.Files);
            Assert.DoesNotContain(included.Files, f => TextFiles.Within(f, ownData));
            Assert.True(TextFiles.IsDotHidden(dotFile));
            Assert.True(TextFiles.IsDotHidden(descendant));
            Assert.False(TextFiles.IsDotHidden(Path.Combine(root, ".", "version.1.txt")));
            Assert.False(TextFiles.IsDotHidden(Path.Combine(root, ".git", "..", "version.1.txt")));
            Assert.Equal(nestedFile, Assert.Single(TextFiles.Scan([new(descendant)], ownData, default).Files));
            var shallow = TextFiles.Scan([new(root, false)], ownData, default, excludeDotHiddenEntries: false);
            Assert.Contains(dotFile, shallow.Files);
            Assert.DoesNotContain(nestedFile, shallow.Files);
        }
        finally { File.SetAttributes(normal, FileAttributes.Normal); }
    }

    [Fact]
    public void ExplicitRootDoesNotOptInHiddenChildrenOrBypassIgnoreRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var hidden = Path.Combine(root, ".local", "input");
        var deeper = Path.Combine(hidden, ".drafts");
        Directory.CreateDirectory(deeper);
        var visible = Path.Combine(hidden, "guide.txt");
        var dotFile = Path.Combine(hidden, ".private.txt");
        var nested = Path.Combine(deeper, "draft.txt");
        foreach (var path in new[] { visible, dotFile, nested }) File.WriteAllText(path, "public test text");
        WatchedFolder[] folders = [new(root), new(Path.Combine(hidden, ".", "..", "input"))];
        var scan = TextFiles.Scan(folders, root + "-data", default);
        Assert.Equal(visible, Assert.Single(scan.Files));
        var policy = new IgnorePolicy(folders, []);
        Assert.False(policy.IsIgnored(hidden, true, excludeDotHiddenEntries: true));
        Assert.True(policy.IsIgnored(dotFile, false, excludeDotHiddenEntries: true));
        Assert.True(policy.IsIgnored(nested, false, excludeDotHiddenEntries: true));
        Assert.Empty(TextFiles.Scan(folders, root + "-data", default, globalIgnorePatterns: ["*.txt"]).Files);
        Assert.Equal(visible, Assert.Single(TextFiles.Scan(folders, root + "-data", default,
            globalIgnorePatterns: ["*.txt", "!guide.txt"]).Files));
        File.WriteAllText(Path.Combine(root, ".ragignore"), ".local/");
        Assert.Empty(TextFiles.Scan(folders, root + "-data", default).Files);
        File.WriteAllText(Path.Combine(hidden, ".ragignore"), "!guide.txt");
        Assert.Equal(visible, Assert.Single(TextFiles.Scan(folders, root + "-data", default).Files));
        Assert.Empty(TextFiles.Scan([new(hidden)], hidden, default).Files); // Own data stays protected.
    }

    [Fact]
    public void NonRecursiveExplicitHiddenRootDoesNotExposeItsDeeperDescendants()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        var hidden = Path.Combine(root, ".local");
        var deeper = Path.Combine(hidden, "nested");
        Directory.CreateDirectory(deeper);
        var direct = Path.Combine(hidden, "direct.txt");
        var nested = Path.Combine(deeper, "nested.txt");
        File.WriteAllText(direct, "public test text");
        File.WriteAllText(nested, "public test text");
        WatchedFolder[] folders = [new(root), new(hidden, Recursive: false)];
        Assert.Equal(direct, Assert.Single(TextFiles.Scan(folders, root + "-data", default).Files));
        Assert.True(new IgnorePolicy(folders, []).IsIgnored(nested, false, excludeDotHiddenEntries: true));
        Assert.Equal(2, TextFiles.Scan([new(root), new(hidden)], root + "-data", default).Files.Count);
    }

    [ServerFact]
    public async Task ExplicitRootUnderDotAncestorCanBeIndexedSearchedAndFetchedWithConfidentialityChecks()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root + "-inputs", ".local", "input");
        Directory.CreateDirectory(input);
        var visible = Path.Combine(input, "guide.txt");
        var secret = Path.Combine(input, "secret.txt");
        await File.WriteAllTextAsync(visible, "moon public test text");
        await File.WriteAllTextAsync(secret, "社外秘\n\nmoon synthetic secret");
        await File.WriteAllTextAsync(Path.Combine(input, ".hidden.txt"), "moon hidden test text");
        var settings = TextRagTests.Settings(input) with { ExcludeDotHiddenEntries = true };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, fixture.Root);
        Assert.Equal(2, (await service.ScanAsync()).Indexed);
        var hits = await service.SearchAsync("moon", 10);
        Assert.Equal(2, hits.Count);
        var allowed = Assert.Single(hits, h => h.SourcePath == visible);
        Assert.Equal("moon public test text", (await service.FetchAsync(allowed.DocumentId))!.Content);
        var closed = Assert.Single(hits, h => h.Decision == DisclosureDecision.Withheld);
        Assert.Null(closed.Content);
        Assert.Null((await service.FetchAsync(closed.DocumentId))!.Content);
        using var calls = new LocalRetrievalCalls(service);
        Assert.False((await calls.CallAsync("fetch", null, 0, allowed.DocumentId.ToString("D"), default)).IsError == true);
        Assert.Equal(2, (await service.ScanAsync()).Unchanged);
    }

    [ServerFact]
    public async Task ReenablingExclusionImmediatelyBlocksSearchAndFetchThenDeactivatesOnlyHiddenDocuments()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        var hidden = Path.Combine(input, ".drafts"); Directory.CreateDirectory(hidden);
        var visible = Path.Combine(input, "public.txt");
        var dotFile = Path.Combine(input, ".notes.txt");
        var nested = Path.Combine(hidden, "guide.txt");
        foreach (var file in new[] { visible, dotFile, nested }) await File.WriteAllTextAsync(file, "moon public text");
        var inclusive = TextRagTests.Settings(input) with { ExcludeDotHiddenEntries = false };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var include = new TextRagService(repo, new FakeTextAi(), inclusive, fixture.Root);
        Assert.Equal(3, (await include.ScanAsync()).Indexed);
        var documents = await repo.DocumentsAsync(default);
        var hiddenIds = documents.Where(d => d.Path != visible).Select(d => d.DocumentId).ToArray();
        using (var exclude = new TextRagService(repo, new FakeTextAi(), inclusive with { ExcludeDotHiddenEntries = true }, fixture.Root))
        {
            // Do not wait for the next scan before enforcing the retrieval restriction.
            Assert.Equal(visible, Assert.Single(await exclude.SearchAsync("moon", 10)).SourcePath);
            using var calls = new LocalRetrievalCalls(exclude);
            foreach (var id in hiddenIds)
            {
                Assert.Null(await exclude.FetchAsync(id));
                Assert.True((await calls.CallAsync("fetch", null, 0, id.ToString("D"), default)).IsError);
            }
            var result = await exclude.ScanAsync();
            Assert.Equal(2, result.Deleted);
            Assert.Equal(1, result.Unchanged);
            Assert.Equal(0, result.Failed);
            Assert.All((await repo.DocumentsAsync(default)).Where(d => hiddenIds.Contains(d.DocumentId)), d => Assert.Equal("deleted", d.Status));
            Assert.True(File.Exists(dotFile)); Assert.True(File.Exists(nested));
        }
        var restored = await include.ScanAsync();
        Assert.Equal(2, restored.Indexed);
        Assert.Equal(1, restored.Unchanged);
        Assert.Equal(3, (await include.SearchAsync("moon", 10)).Count);
        foreach (var id in hiddenIds) Assert.NotNull((await include.FetchAsync(id))!.Content);
    }
}
