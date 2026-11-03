using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class IgnorePolicyTests
{
    [Theory]
    [InlineData("*.tmp", "a/b.tmp", false, true)]
    [InlineData("/a.tmp", "sub/a.tmp", false, false)]
    [InlineData("/a.tmp", "a.tmp", false, true)]
    [InlineData("cache/", "a/cache", true, true)]
    [InlineData("cache/", "a/cache", false, false)]
    [InlineData("a/b", "x/a/b", false, false)]
    [InlineData("**/bin/", "bin", true, true)]
    [InlineData("**/bin/", "a/b/bin", true, true)]
    [InlineData("a/**/b.txt", "a/b.txt", false, true)]
    [InlineData("a/**/b.txt", "a/x/y/b.txt", false, true)]
    [InlineData("a/**", "a/x/y", false, true)]
    [InlineData("a?c", "abc", false, true)]
    [InlineData("a?c", "a/c", false, false)]
    [InlineData("[a-c].txt", "b.txt", false, true)]
    [InlineData("[!a-c].txt", "d.txt", false, true)]
    [InlineData("a[!x]b", "a/b", false, false)]
    [InlineData(@"\#note", "#note", false, true)]
    [InlineData(@"\!note", "!note", false, true)]
    [InlineData(@"a\*b", "a*b", false, true)]
    [InlineData(@"name\ ", "name ", false, true)]
    [InlineData("*.TXT ", "a.txt", false, true)]
    public void GlobSyntaxIsPathAware(string pattern, string path, bool directory, bool expected) =>
        Assert.Equal(expected, Assert.Single(IgnorePattern.Parse([pattern])).Matches(path, directory));

    [Theory]
    [InlineData("foo", "child/foo", false)]
    [InlineData(" /foo/ ", "foo", true)]
    [InlineData("**/foo", "child/foo", true)]
    [InlineData("a/../b", "b", true)]
    public void DockerPatternsAreRootRelativeAndCleaned(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Assert.Single(IgnorePattern.Parse([pattern], docker: true)).Matches(path, false));

    [Fact]
    public async Task GlobalSettingsValidateAndRoundTripWithoutChangingEmbeddings()
    {
        var store = new SettingsStore(NewRoot());
        var original = await store.LoadAsync();
        Assert.Empty(original.GlobalIgnorePatterns);
        var settings = original with { GlobalIgnorePatterns = ["# comment", "*.tmp", "!keep.tmp", @"name\ "] };
        await store.SaveAsync(settings);
        Assert.Equal(settings.GlobalIgnorePatterns, (await store.LoadAsync()).GlobalIgnorePatterns);
        Assert.NotEqual(MonitoringCheckpoint.ScopeFor(original), MonitoringCheckpoint.ScopeFor(settings));
        Assert.Equal(original.PipelineConfigurationId(), settings.PipelineConfigurationId());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with { GlobalIgnorePatterns = ["[bad"] }));
        Assert.Equal(settings.GlobalIgnorePatterns, (await store.LoadAsync()).GlobalIgnorePatterns);
        Assert.Throws<InvalidDataException>(() => IgnorePattern.Parse([new string('a', 1025)]));
        Assert.Throws<InvalidDataException>(() => IgnorePattern.Parse(Enumerable.Repeat("#", 2049).ToArray()));
        Assert.Empty(IgnorePattern.Parse(["", "# comment", "  "]));
        Assert.Empty(IgnorePattern.Parse(["."], docker: true));
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".dockerignore")]
    [InlineData(".npmignore")]
    [InlineData(".ragignore")]
    public void EveryControlFileIsReadEvenWhenItIsHidden(string name)
    {
        var root = NewRoot();
        Write(root, "skip.txt"); var keep = Write(root, "keep.txt");
        File.WriteAllText(Path.Combine(root, name), "skip.txt\n", new UTF8Encoding(true));
        var scan = TextFiles.Scan([new(root)], root + "-data", default,
            extensions: [".txt", Path.GetExtension(name)]);
        Assert.Equal(keep, Assert.Single(scan.Files));
        Assert.Equal(0, scan.FailedRoots);
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".dockerignore")]
    [InlineData(".npmignore")]
    [InlineData(".ragignore")]
    [InlineData("global")]
    public void ExceptionInAnySourceWinsAgainstAllOthersRegardlessOfLineOrder(string exceptionSource)
    {
        var root = NewRoot();
        var keep = Write(root, "keep.txt"); Write(root, "skip.txt");
        string[] names = [".gitignore", ".dockerignore", ".npmignore", ".ragignore"];
        foreach (var name in names)
            File.WriteAllText(Path.Combine(root, name), name == exceptionSource ? "!keep.txt\n*.txt" : "*.txt");
        string[] global = exceptionSource == "global" ? ["!keep.txt", "*.txt"] : ["*.txt"];
        Assert.Equal(keep, Assert.Single(TextFiles.Scan([new(root)], root + "-data", default, globalIgnorePatterns: global).Files));
    }

    [Fact]
    public void NestedExceptionsRestoreChildrenOfExcludedDirectoriesAndDotHiddenEntries()
    {
        var root = NewRoot();
        var keep = Write(root, "blocked/deep/keep.txt");
        Write(root, "blocked/deep/skip.txt");
        var dotKeep = Write(root, ".hidden/.keep.txt"); Write(root, ".hidden/skip.txt");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "blocked/\n.hidden/");
        File.WriteAllText(Path.Combine(root, "blocked", "deep", ".ragignore"), "!keep.txt\n*.txt");
        File.WriteAllText(Path.Combine(root, ".hidden", ".npmignore"), "!.keep.txt");
        WatchedFolder[] folders = [new(root), new(Path.Combine(root, "blocked", "deep"))];
        var scan = TextFiles.Scan(folders, root + "-data", default, globalIgnorePatterns: ["**/*.txt"]);
        Assert.Equal(0, scan.FailedRoots);
        Assert.Equal(2, scan.Files.Count); Assert.Contains(keep, scan.Files); Assert.Contains(dotKeep, scan.Files);
        Assert.False(new IgnorePolicy(folders, ["**/*.txt"]).IsIgnored(dotKeep, false, excludeDotHiddenEntries: true));
    }

    [Fact]
    public void LocalRulesStayScopedAndDirectoryExceptionsIncludeTheirDescendants()
    {
        var root = NewRoot();
        var restored = Write(root, "child/restore/deep/keep.txt");
        var sibling = Write(root, "sibling/skip.txt");
        Write(root, "child/skip.txt");
        var anchored = Write(root, "child/deep/root.txt");
        Write(root, "child/root.txt");
        File.WriteAllText(Path.Combine(root, "child", ".ragignore"), "skip.txt\n/root.txt\n!restore/\nrestore/**");
        var files = TextFiles.Scan([new(root)], root + "-data", default).Files;
        Assert.Equal(3, files.Count);
        Assert.Contains(restored, files); Assert.Contains(sibling, files); Assert.Contains(anchored, files);
        Assert.Empty(TextFiles.Scan([new(root, false)], root + "-data", default).Files);
    }

    [Fact]
    public void ExceptionsDoNotBypassDataDirectoryOrExtensionRestrictions()
    {
        var root = NewRoot();
        var data = Path.Combine(root, ".state");
        Write(root, ".state/internal.txt"); Write(root, "binary.exe");
        var keep = Write(root, ".keep.txt");
        Assert.Equal(keep, Assert.Single(TextFiles.Scan([new(root)], data, default, globalIgnorePatterns: ["!**"]).Files));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidRulesFailClosedEvenWhenAnotherSourceHasAnException(int mode)
    {
        var root = NewRoot(); var file = Write(root, "keep.txt");
        var rule = Path.Combine(root, ".ragignore");
        if (mode == 0) File.WriteAllText(rule, "[bad");
        if (mode == 1) File.WriteAllText(rule, new string('x', 65537));
        if (mode == 2) File.WriteAllBytes(rule, [0xff, 0xfe, 0xff]);
        var scan = TextFiles.Scan([new(root)], root + "-data", default, globalIgnorePatterns: ["!keep.txt"]);
        Assert.Empty(scan.Files); Assert.Equal(1, scan.FailedRoots); Assert.Empty(scan.CompletedRoots);
        Assert.Throws<InvalidDataException>(() => new IgnorePolicy([new(root)], ["!keep.txt"]).IsIgnored(file, false));
    }

    [ServerFact]
    public async Task RuleEditsAffectMcpImmediatelyAndScanWithdrawsOnlyExcludedDocuments()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        var guide = Write(input, "guide.txt"); Write(input, "public.txt");
        var settings = TextRagTests.Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, new FakeTextAi(), settings, fixture.Root);
        Assert.Equal(2, (await service.ScanAsync()).Indexed);
        var id = (await repo.DocumentsAsync(default)).Single(d => d.Path == guide).DocumentId;
        var rule = Path.Combine(input, ".gitignore");
        File.WriteAllText(rule, "guide.txt");
        Assert.Single(await service.SearchAsync("moon", 10));
        using var calls = new LocalRetrievalCalls(service);
        Assert.True((await calls.CallAsync("fetch", null, 0, id.ToString("D"), default)).IsError);
        // A new exception restores an already indexed document immediately, before scanning.
        var exception = Path.Combine(input, ".ragignore");
        File.WriteAllText(exception, "!guide.txt\nguide.txt");
        Assert.Equal(2, (await service.SearchAsync("moon", 10)).Count);
        Assert.NotNull((await service.FetchAsync(id))!.Content);
        File.Delete(exception);
        Assert.Equal(1, (await service.ScanAsync()).Deleted);
        Assert.True(File.Exists(guide));
        File.WriteAllText(exception, "!guide.txt");
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.NotNull((await service.FetchAsync(id))!.Content);
        File.WriteAllText(rule, "[bad");
        Assert.Empty(await service.SearchAsync("moon", 10)); Assert.Null(await service.FetchAsync(id));
        var failed = await service.ScanAsync();
        Assert.Equal(1, failed.InaccessibleRoots); Assert.Equal(0, failed.Deleted);
        Assert.All(await repo.DocumentsAsync(default), d => Assert.Equal("ready", d.Status));
        File.Delete(rule);
        using var global = new TextRagService(repo, new FakeTextAi(), settings with { GlobalIgnorePatterns = ["*.txt"] }, fixture.Root);
        Assert.Equal(guide, Assert.Single(await global.SearchAsync("moon", 10)).SourcePath);
        Assert.Equal(1, (await global.ScanAsync()).Deleted);
    }

    [ServerFact]
    public async Task RuleAddedDuringEmbeddingPreventsPublication()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs"; Write(input, "guide.txt");
        var ai = new FakeTextAi { BeforeEmbedding = _ => File.WriteAllText(Path.Combine(input, ".ragignore"), "guide.txt") };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, TextRagTests.Settings(input), fixture.Root);
        var result = await service.ScanAsync();
        Assert.Equal(0, result.Indexed); Assert.Equal(1, result.Deleted);
        Assert.Equal("deleted", Assert.Single(await repo.DocumentsAsync(default)).Status);
        Assert.Empty(await service.SearchAsync("moon", 10));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static string Write(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "moon public text"); return path;
    }
}
