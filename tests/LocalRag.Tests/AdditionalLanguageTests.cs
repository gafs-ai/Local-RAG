using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class AdditionalLanguageTests
{
    private static readonly (SourceCodeMode Mode, string Code, string Function)[] Examples = [
        (SourceCodeMode.TypeScript, "export class Calc { add(a: number, b: number): number { return a + b; } }", "add"),
        (SourceCodeMode.JavaScript, "throw new Error('must not run'); class 計算 { add(a,b) { return a+b; } } const twice = x => x * 2;", "add"),
        (SourceCodeMode.Cpp, "template<typename T> class Calc { public: T add(T a, T b) { return a+b; } };", "add"),
        (SourceCodeMode.C, "struct Calc { int value; }; int add(int a, int b) { return a+b; }", "add"),
        (SourceCodeMode.Rust, "struct Calc; impl Calc { fn add(a: i32, b: i32) -> i32 { a+b } }", "add"),
        (SourceCodeMode.Java, "class Calc { int add(int a, int b) { return a+b; } }", "add"),
        (SourceCodeMode.Kotlin, "class Calc { fun add(a: Int, b: Int): Int { return a + b } }", "add")
    ];

    [ServerFact]
    public async Task EveryAddedLanguageExtractsFunctionsAndTypesWithoutExecutingSource()
    {
        var tools = Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!;
        foreach (var example in Examples)
        {
            // An unrelated extension verifies that the configured language determines the parser.
            var units = await SourceCodeParser.ParseAsync(example.Code, "input.data", tools, default, example.Mode);
            Assert.Equal(example.Code, units[0].Code);
            Assert.Contains(units, u => u.Kind == "function" && u.Name == example.Function);
            Assert.Contains(units, u => u.Kind == "class");
            Assert.All(units, u => Assert.Contains(u.Code, example.Code));
            Assert.All(units.Skip(1), u => Assert.Contains(units, p => p.Id == u.ContainerId));
            var invalid = await Assert.ThrowsAsync<PipelineValidationException>(() =>
                SourceCodeParser.ParseAsync("class { (", "bad.data", tools, default, example.Mode));
            Assert.Equal("code_syntax", invalid.Code);
        }
        var tsx = "export const App = () => <div>こんにちは</div>;";
        var components = await SourceCodeParser.ParseAsync(tsx, "view.tsx", tools, default, SourceCodeMode.TypeScript);
        Assert.Contains(components, u => u.Kind == "function" && u.Name == "App" && u.Code.Contains("こんにちは"));
    }

    [Fact]
    public async Task AllLanguageChoicesAndCustomExtensionsSurviveSettingsRoundTrip()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var settings = await store.LoadAsync();
        Assert.Empty(settings.Validate());
        foreach (var language in SourceLanguages.All)
            foreach (var extension in language.Extensions)
                Assert.Equal(language.Mode, settings.PipelineFor("sample" + extension).SourceMode);
        await store.SaveAsync(settings);
        Assert.Equal(settings.PipelineConfigurationId(), (await store.LoadAsync()).PipelineConfigurationId());
    }

    [ServerFact]
    public async Task AddedLanguagesShareDescriptionIndexAndRetrievalPipeline()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-input";
        Directory.CreateDirectory(input);
        var rules = new List<TextRoute>();
        foreach (var example in Examples)
        {
            var extension = SourceLanguages.Find(example.Mode)!.Extensions[0];
            await File.WriteAllTextAsync(Path.Combine(input, "sample" + extension), example.Code);
            rules.Add(new() { Name = example.Mode.ToString(), Extensions = [extension], SourceMode = example.Mode, ClassifyEnabled = false });
        }
        // Reuse the verified offline parser cache for the integration fixture.
        var data = Path.Combine(fixture.Root, "state");
        var parserCache = Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!;
        await new PythonParserRuntime(parserCache).EnsureAsync(default);
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        foreach (var rule in rules)
        {
            // Prepare the fixture cache using the same verified archives, without downloading again.
            var grammar = SourceLanguages.Find(rule.SourceMode)!.Grammar!;
            await TreeSitterRuntime.EnsureAsync(parserCache, grammar, default);
        }
        CopyCache(parserCache, Path.Combine(data, "tools"));
        var management = ManagementSettings.CreateDefault() with { Routes = rules, Retrieval = new() { ConfidentialityEnabled = false } };
        var settings = TextRagTests.Settings(input) with { Management = management };
        using var service = new TextRagService(repository, new FakeTextAi(), settings, data);
        var cycle = await service.ScanAsync();
        Assert.Equal(0, cycle.Failed);
        Assert.Equal(Examples.Length, cycle.Indexed);
        foreach (var document in await repository.DocumentsAsync(default))
        {
            Assert.Contains(document.CodeUnits!, u => u.Kind == "function");
            Assert.All(document.Chunks!, c => Assert.Contains("インターフェース", c.Text));
            Assert.Equal(document.Original, (await service.FetchAsync(document.DocumentId))!.Content);
        }
        Assert.NotEmpty(await service.SearchAsync("検索"));
    }

    private static void CopyCache(string cache, string target)
    {
        foreach (var name in new[] { "python-parser", "tree-sitter" })
        {
            var root = Path.Combine(cache, name);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal)))
            {
                var destination = Path.Combine(target, name, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
    }
}
