using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class SourceCodePipelineTests
{
    [Fact]
    public void CSharpParserUnderstandsCommentsStringsAttributesAndNestedDefinitions()
    {
        const string code = """
            // class Fake { void NotAFunction() {} }
            namespace Test;
            [Serializable]
            public class Outer
            {
                public string Read() { return "void Fake() { }"; }
                public int Read(int n) => n + 1;
                class Inner { int Run() { int Local() => 1; return Local(); } }
            }
            """;
        var units = SourceCodeParser.ParseCSharp(code, "Example.cs", default);
        Assert.Equal(code, units[0].Code);
        Assert.DoesNotContain(units, u => u.Name == "Fake" || u.Name == "NotAFunction");
        Assert.Equal(2, units.Count(u => u.Name == "Read"));
        Assert.Contains("[Serializable]", units.Single(u => u.Name == "Outer").Code);
        Assert.Equal(units.Single(u => u.Name == "Run").Id, units.Single(u => u.Name == "Local").ContainerId);
        Assert.All(units, u => Assert.Contains(u.Code, code));
        Assert.Throws<PipelineValidationException>(() => SourceCodeParser.ParseCSharp("class Incomplete {", "a.cs", default));
    }

    [ServerFact]
    public async Task PythonParserIncludesDecoratorsAsyncAndNestedScopesWithoutExecutingSource()
    {
        const string code = """
            raise RuntimeError("must never execute")
            class 日本語:
                @decorator("def fake():")
                async def run(self):
                    '''class Fake: pass'''
                    def inner():
                        return 1
                    return inner()
            """;
        var units = await SourceCodeParser.ParseAsync(code, "sample.py", Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!, default);
        Assert.Equal(4, units.Count);
        Assert.Equal(code, units[0].Code);
        Assert.StartsWith("    @decorator", units.Single(u => u.Name == "run").Code);
        Assert.Equal(units.Single(u => u.Name == "run").Id, units.Single(u => u.Name == "inner").ContainerId);
        Assert.All(units, u => Assert.Contains(u.Code, code));
        await Assert.ThrowsAsync<PipelineValidationException>(() => SourceCodeParser.ParseAsync("def invalid(:", "a.py",
            Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!, default));
    }

    [ServerFact]
    public async Task DescriptionIndexResumesAndReturnsThreeScopesWithMcpAndDisclosureChecks()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input");
        Directory.CreateDirectory(input);
        var code = "public class Calculator { public int Add(int a, int b) {\n" + string.Concat(Enumerable.Repeat("//説明対象の長いコメント\n", 30)) + "return a + b; } }";
        var path = Path.Combine(input, "Calculator.cs");
        await File.WriteAllTextAsync(path, code);
        var management = ManagementSettings.FromLegacy(new(), new()) with { TextExtensions = [".cs"],
            DefaultTextRoute = new() { ClassifyEnabled = false }, Retrieval = new() { LlmEnabled = false, Patterns = ["CONFIDENTIAL"] } };
        var settings = TextRagTests.Settings(input) with { Management = management };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var first = new CodeAi { FailCall = 2 };
        using (var service = new TextRagService(repository, first, settings, Path.Combine(fixture.Root, "data")))
            Assert.Equal(1, (await service.ScanAsync()).Failed);
        var checkpoint = Assert.Single(await repository.DocumentsAsync(default));
        var interrupted = checkpoint.CodeUnits!.Single(u => u.Kind == "function");
        Assert.True(interrupted.DescribedCharacters > 0);
        Assert.Single(interrupted.Descriptions);
        var second = new CodeAi();
        using var resumed = new TextRagService(repository, second, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        var ready = Assert.Single(await repository.DocumentsAsync(default));
        var function = ready.CodeUnits!.Single(u => u.Kind == "function");
        Assert.Equal(first.Fragments[0] + string.Concat(second.Fragments.Take(function.Descriptions.Count - 1)), function.Code);
        Assert.All(ready.CodeUnits!, u => Assert.True(u.DescriptionCompleted));
        Assert.Equal(function.Code.Length, function.DescribedCharacters);
        Assert.All(second.EmbeddingInputs, s => { Assert.Contains("説明", s); Assert.DoesNotContain("public", s); });
        var hits = await resumed.SearchAsync("足し算", 10);
        Assert.Contains(hits, h => h.SourceKind == "class");
        Assert.DoesNotContain(hits, h => h.SourceKind == "function");
        Assert.Equal(hits.Count, hits.Select(h => h.ParentChunkId).Distinct().Count());
        Assert.All(hits, hit => { Assert.Contains("説明", hit.Content!); Assert.Contains("public", hit.Content!); Assert.Null(hit.StartParagraph); Assert.NotNull(hit.StartLine); });
        Assert.Equal(code, (await resumed.FetchAsync(ready.DocumentId))!.Content);
        using var calls = new LocalRetrievalCalls(resumed);
        var response = McpTests.Body(await calls.CallAsync("search", "足し算", 10, null, default));
        Assert.All(response.GetProperty("results").EnumerateArray(), h => Assert.True(h.TryGetProperty("source_kind", out _)));
        var blockedSettings = settings with { Management = management with { Retrieval = management.Retrieval with { Patterns = ["説明"] } } };
        using var blocked = new TextRagService(repository, second, blockedSettings, Path.Combine(fixture.Root, "data"));
        using var blockedCalls = new LocalRetrievalCalls(blocked);
        var hidden = McpTests.Body(await blockedCalls.CallAsync("search", "足し算", 10, null, default));
        Assert.All(hidden.GetProperty("results").EnumerateArray(), h =>
        {
            Assert.Equal(new[] { "decision", "id", "source" }, h.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(path, h.GetProperty("source").GetString());
        });
        await File.AppendAllTextAsync(path, "\n// changed");
        Assert.Null(await resumed.FetchAsync(ready.DocumentId));
        Assert.Empty(await resumed.SearchAsync("足し算"));
    }

    private sealed class CodeAi : ITextRagAi
    {
        public int FailCall { get; init; }
        private int _calls;
        public List<string> Fragments { get; } = [];
        public List<string> EmbeddingInputs { get; } = [];
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => Task.FromResult(500);
        public Task<int> DocumentEmbeddingBudgetAsync(CancellationToken token) => Task.FromResult(100);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            if (++_calls == FailCall) throw new HttpRequestException("test interruption");
            using var json = JsonDocument.Parse(input);
            Fragments.Add(json.RootElement.GetProperty("fragment").GetString()!);
            return Task.FromResult("{\"description\":\"説明：整数の足し算を行います。\"}");
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            EmbeddingInputs.AddRange(inputs);
            return Task.FromResult(new VectorBatch(3, inputs.Select(_ => new float[] { 1, 0.1f, 0 }).ToArray()));
        }
    }
}
