using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class OrderedRouteTests
{
    private static ManagementSettings Defaults() => ManagementSettings.CreateDefault();

    [Fact]
    public void FirstMatchingRuleWinsAndUnmatchedExtensionsAreNotIngested()
    {
        var settings = Defaults() with { Routes = [
            new() { Name = "Python", Extensions = [".py"], SourceMode = SourceCodeMode.Python },
            new() { Name = "共通", Extensions = [".txt", ".py"], SourceMode = SourceCodeMode.Text }] };
        Assert.Empty(settings.Validate());
        Assert.Equal(SourceCodeMode.Python, settings.Resolve(new(), "CODE.PY").SourceMode);
        Assert.Equal(SourceCodeMode.Text, settings.Resolve(new(), "a.txt").SourceMode);
        Assert.Equal(new[] { ".py", ".txt" }, settings.AllowedExtensions().Order().ToArray());
        Assert.Throws<InvalidOperationException>(() => settings.RouteFor("a.exe"));
        var reversed = settings with { Routes = settings.Routes!.AsEnumerable().Reverse().ToList() };
        Assert.Equal(SourceCodeMode.Text, reversed.Resolve(new(), "a.py").SourceMode);
        Assert.NotEqual(settings.ConfigurationId(new(), new()), reversed.ConfigurationId(new(), new()));
        var renamed = settings with { Routes = settings.Routes.Select(r => r with { Name = "名前変更" }).ToList() };
        Assert.Equal(settings.ConfigurationId(new(), new()), renamed.ConfigurationId(new(), new()));
    }

    [Fact]
    public async Task LegacyMigrationPreservesScopeOverridesAndPromptsAndSavesOnlyOrderedRoutes()
    {
        var legacy = ManagementSettings.FromLegacy(new(), new()) with
        {
            TextExtensions = [".cs", ".py", ".txt", ".md"],
            UserPrompts = [new("custom", "独自分類", PromptPurpose.Classification, "custom classification")],
            ExtensionRoutes = [new() { Extension = ".cs", SourceMode = SourceCodeMode.Text, ClassifyPromptId = "custom", FormatPromptId = "" }]
        };
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var original = JsonSerializer.Serialize(new AppSettings { Management = legacy });
        await File.WriteAllTextAsync(store.FilePath, original);
        var loaded = await store.LoadAsync();
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
        var settings = loaded.Management!;
        Assert.Equal(2, settings.Version);
        Assert.Equal(legacy.TextExtensions.Order(), settings.AllowedExtensions().Order());
        Assert.Equal(SourceCodeMode.Text, loaded.PipelineFor("a.cs").SourceMode);
        Assert.Equal("custom classification", loaded.PipelineFor("a.cs").ClassifyPrompt);
        Assert.False(loaded.PipelineFor("a.cs").FormatEnabled);
        Assert.Equal(SourceCodeMode.Python, loaded.PipelineFor("a.py").SourceMode);
        Assert.Equal("共通設定", settings.Routes![^1].Name);
        Assert.Equal(new[] { ".txt", ".md" }, settings.Routes[^1].Extensions);
        await store.SaveAsync(loaded);
        var saved = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("TextExtensions", saved);
        Assert.DoesNotContain("DefaultTextRoute", saved);
        Assert.DoesNotContain("ExtensionRoutes", saved);
        Assert.DoesNotContain("\"Extension\":", saved);
        var reopened = await store.LoadAsync();
        Assert.Equal(loaded.PipelineConfigurationId(), reopened.PipelineConfigurationId());
        Assert.Equal(JsonSerializer.Serialize(settings.Routes), JsonSerializer.Serialize(reopened.Management!.Routes));
    }

    [Fact]
    public async Task NewInstallDefinesSeparateLanguagesAndLowestPriorityTextRule()
    {
        var settings = (await Store().LoadAsync()).Management!;
        Assert.Empty(settings.Validate());
        Assert.Equal(SourceCodeMode.CSharp, settings.RouteFor("a.cs").SourceMode);
        Assert.Equal(SourceCodeMode.Python, settings.RouteFor("a.py").SourceMode);
        Assert.Equal("共通設定", settings.Routes![^1].Name);
        Assert.Equal(new[] { ".txt" }, settings.Routes[^1].Extensions);
    }

    [Fact]
    public void RuleValidationRejectsWildcardMissingExtensionsAndUnspecifiedLanguage()
    {
        var defaults = Defaults();
        foreach (var bad in new TextRoute[] {
            new() { Extensions = ["*"], SourceMode = SourceCodeMode.Text },
            new() { Extensions = [], SourceMode = SourceCodeMode.Text },
            new() { Extensions = [".cs"] },
            new() { Extensions = [".txt", ".txt"], SourceMode = SourceCodeMode.Text },
            new() { Name = "", Extensions = [".txt"], SourceMode = SourceCodeMode.Text } })
            Assert.NotEmpty((defaults with { Routes = [bad] }).Validate());
        Assert.NotEmpty((defaults with { Routes = [] }).Validate());
    }

    [Fact]
    public async Task SelectedCSharpParserDoesNotGuessFromFileExtension()
    {
        var units = await SourceCodeParser.ParseAsync("public class Example { void Run() {} }", "example.py", "unused", default, SourceCodeMode.CSharp);
        Assert.Contains(units, u => u.Name == "Run");
        Assert.False(SourceCodeParser.Applies(new() { SourceMode = SourceCodeMode.Text }, "example.cs"));
    }

    [ServerFact]
    public async Task PriorityChangeReindexesWithSelectedLanguageAndRemovingRuleWithdrawsFile()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-input";
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "example.txt"), "public class Example { void Run() {} }");
        var text = new TextRoute { Name = "Text", Extensions = [".txt"], SourceMode = SourceCodeMode.Text, ClassifyEnabled = false, FormatPromptId = "" };
        var code = text with { Name = "C#", SourceMode = SourceCodeMode.CSharp };
        var management = Defaults() with { Routes = [text, code], Retrieval = new() { ConfidentialityEnabled = false } };
        var settings = TextRagTests.Settings(input) with { Management = management };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using (var service = new TextRagService(repository, new FakeTextAi(), settings, fixture.Root))
        {
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            Assert.Null(Assert.Single(await repository.DocumentsAsync(default)).CodeUnits);
        }
        var id = Assert.Single(await repository.DocumentsAsync(default)).DocumentId;
        var reordered = settings with { Management = management with { Routes = [code, text] } };
        using (var service = new TextRagService(repository, new FakeTextAi(), reordered, fixture.Root))
        {
            Assert.Null(await service.FetchAsync(id));
            Assert.Equal(1, (await service.ScanAsync()).Indexed);
            Assert.Equal(3, Assert.Single(await repository.DocumentsAsync(default)).CodeUnits!.Count);
            Assert.Equal(3, (await service.SearchAsync("検索", 10)).Count);
        }
        var removed = settings with { Management = management with { Routes = [text with { Extensions = [".md"] }] } };
        using var unavailable = new TextRagService(repository, new FakeTextAi(), removed, fixture.Root);
        Assert.Null(await unavailable.FetchAsync(id));
        Assert.Empty(await unavailable.SearchAsync("検索"));
        await unavailable.ScanAsync();
        Assert.Equal("deleted", Assert.Single(await repository.DocumentsAsync(default)).Status);
    }

    private static SettingsStore Store() => new(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
}
