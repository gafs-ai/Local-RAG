using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class CodeHierarchyTests
{
    private static SourceCodeUnit Unit(string id, string? parent, string kind, int length = 10) =>
        new(id, parent, kind, id, 1, 10, new string('x', length)) { Descriptions = ["説明 " + id] };

    [Fact]
    public void RetrievalPromotesFunctionButDirectHitsKeepTheirScopeAndRespectBound()
    {
        var file = Unit("file", null, "file", 9000);
        var type = Unit("type", "file", "class", 200);
        var method = Unit("method", "type", "function", 80);
        var free = Unit("free", "file", "function", 80);
        var units = new[] { file, type, method, free };
        Assert.Equal("type", CodeHierarchy.Select(method, units, 1000).Unit.Id);
        Assert.Equal("free", CodeHierarchy.Select(free, units, 1000).Unit.Id);
        Assert.Equal("type", CodeHierarchy.Select(type, units, 1000).Unit.Id);
        var fileHit = CodeHierarchy.Select(file, units, 1000);
        Assert.Equal("file", fileHit.Unit.Id);
        Assert.Equal("outline", fileHit.Mode);
        Assert.Contains("説明 type", fileHit.Content);
        Assert.DoesNotContain("説明 method", fileHit.Content); // Only direct children.
        var largeType = type with { Code = new string('x', 9000) };
        units = [file, largeType, method, free];
        Assert.Equal("method", CodeHierarchy.Select(method, units, 1000).Unit.Id);
        Assert.Equal("outline", CodeHierarchy.Select(largeType, units, 1000).Mode);
        var verbose = method with { Descriptions = [new string('長', 2000)] };
        units = [file, largeType, verbose, free];
        Assert.Equal("summary", CodeHierarchy.Select(largeType, units, 500).Mode);
        var enormous = largeType with { Descriptions = [string.Concat(Enumerable.Repeat("😀", 1000))] };
        var reduced = CodeHierarchy.Select(enormous, [file, enormous, verbose], 500);
        Assert.Equal("summary_truncated", reduced.Mode);
        Assert.InRange(reduced.Content.Length, 1, 500);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(reduced.Content)));
    }

    [Fact]
    public void CSharpDocumentationAndVariablesExcludeMethodBodyAndInitializers()
    {
        const string source = """
            // File overview
            using System;
            /// <summary>Type overview</summary>
            class C {
                private int counter = 1234;
                public string Name { get; set; }
                int Run() { int local = 3; return local; }
            }
            """;
        var units = SourceCodeParser.ParseCSharp(source, "a.cs", default);
        Assert.Contains("File overview", units[0].Documentation);
        var type = units.Single(u => u.Kind == "class");
        Assert.Contains("Type overview", type.Documentation);
        Assert.Equal(new[] { "int counter", "string Name" }, type.Variables);
        Assert.DoesNotContain(type.Variables, v => v.Contains("1234") || v.Contains("local"));
    }

    [ServerFact]
    public async Task LanguageParsersRetainClassDocumentationAndVariableNames()
    {
        var tools = Environment.GetEnvironmentVariable("LOCALRAG_TEST_TOOLS")!;
        var python = await SourceCodeParser.ParseAsync("\"\"\"FILE DOC\"\"\"\nclass C:\n    \"\"\"CLASS DOC\"\"\"\n    count = 0\n    def f(self):\n        self.value = 1\n", "a.py", tools, default);
        Assert.Equal("FILE DOC", python[0].Documentation);
        var pyType = python.Single(u => u.Kind == "class");
        Assert.Equal("CLASS DOC", pyType.Documentation);
        Assert.Equal(new[] { "count", "self.value" }, pyType.Variables);
        foreach (var sample in new[] {
            (SourceCodeMode.TypeScript, "/** CLASS DOC */\nclass C { count: number = 0; f() { return this.count; } }"),
            (SourceCodeMode.JavaScript, "/** CLASS DOC */\nclass C { count = 0; f() { return this.count; } }"),
            (SourceCodeMode.Cpp, "/** CLASS DOC */\nclass C { int count, other; int f() { return count; } };"),
            (SourceCodeMode.C, "/** CLASS DOC */\nstruct C { int count, other; };"),
            (SourceCodeMode.Rust, "/** CLASS DOC */\nstruct C { count: i32 }"),
            (SourceCodeMode.Java, "/** CLASS DOC */\nclass C { int count, other; int f() { return count; } }"),
            (SourceCodeMode.Kotlin, "/** CLASS DOC */\nclass C { var count: Int = 0; fun f(): Int { return count } }") })
        {
            var units = await SourceCodeParser.ParseAsync(sample.Item2, "a.data", tools, default, sample.Item1);
            var type = Assert.Single(units, u => u.Kind == "class");
            Assert.Contains("CLASS DOC", type.Documentation);
            Assert.True(type.Variables.Contains("count"), $"{sample.Item1}: {string.Join(",", type.Variables)}");
            Assert.DoesNotContain(type.Variables, v => v.Contains("return") || v.Contains("= 0"));
            if (sample.Item2.Contains("other")) Assert.Contains("other", type.Variables);
        }
    }

    [ServerFact]
    public async Task BottomUpDescriptionsUseMembersAndRetrievalReportsOriginalMatchedScope()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        var source = "class Calculator { int value; string padding = \"" + new string('x', 6000) + "\";\nint Add() => value + 1; }";
        var path = Path.Combine(input, "calculator.cs"); await File.WriteAllTextAsync(path, source);
        var managed = ManagementSettings.CreateDefault() with { Retrieval = new() { CodeResultMaxCharacters = 1000, LlmEnabled = false } };
        var settings = TextRagTests.Settings(input) with { Management = managed };
        var ai = new HierarchyAi();
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Assert.Equal(new[] { "function", "class", "file" }, ai.Calls.Select(c => c.Kind));
        var typeInput = ai.Calls.Single(c => c.Kind == "class");
        Assert.Equal("members", typeInput.Evidence);
        Assert.Contains("int value", typeInput.Fragment);
        Assert.Contains("description function Add", typeInput.Fragment);
        Assert.DoesNotContain("xxxx", typeInput.Fragment);
        var fileInput = ai.Calls.Single(c => c.Kind == "file");
        Assert.Equal("members", fileInput.Evidence);
        Assert.Contains("description class Calculator", fileInput.Fragment);
        Assert.DoesNotContain("description function Add", fileInput.Fragment);
        Assert.All(ai.Embeddings, text => Assert.DoesNotContain("=>", text));
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.All(document.CodeUnits!, unit => Assert.True(unit.DescriptionCompleted && !unit.DescriptionSkipped));
        using var calls = new LocalRetrievalCalls(service);
        var results = McpTests.Body(await calls.CallAsync("search", "function", 1, null, default)).GetProperty("results");
        var result = results[0];
        Assert.Equal("function", result.GetProperty("source_kind").GetString()); // class is too large
        Assert.Equal("full", result.GetProperty("content_mode").GetString());
        Assert.Equal("function", result.GetProperty("matched_source_kind").GetString());
        var classHit = Assert.Single(await service.SearchAsync("class", 1));
        Assert.Equal("class", classHit.SourceKind);
        Assert.Equal("outline", classHit.ContentMode);
        Assert.InRange(classHit.Content!.Length, 1, 1000);
        var fileHit = Assert.Single(await service.SearchAsync("file", 1));
        Assert.Equal("file", fileHit.SourceKind);
        Assert.Equal("outline", fileHit.ContentMode);
        Assert.Equal(source, (await service.FetchAsync(document.DocumentId))!.Content);
        var secret = settings with { Management = managed with { Retrieval = managed.Retrieval with { Patterns = ["xxxxx"] } } };
        using var blocked = new TextRagService(repo, ai, secret, Path.Combine(fixture.Root, "data"));
        using var blockedCalls = new LocalRetrievalCalls(blocked);
        var closed = McpTests.Body(await blockedCalls.CallAsync("search", "class", 1, null, default)).GetProperty("results")[0];
        Assert.Equal(new[] { "decision", "id", "source" }, closed.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("withheld", closed.GetProperty("decision").GetString());
    }

    [ServerFact]
    public async Task ExplicitDocumentationIsUsedAndFailedContainersDoNotDiscardFunctions()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.cs"), "// FILE DOC\nusing System;\n/// CLASS DOC\nclass C { int F() => 1; }");
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() };
        var ai = new HierarchyAi { RejectContainers = true };
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.All(document.CodeUnits!.Where(u => u.Kind != "function"), u => Assert.True(u.DescriptionSkipped));
        Assert.All(document.Chunks!, c => Assert.Equal(document.CodeUnits!.Single(u => u.Kind == "function").Id, c.ParentId));
        Assert.All(ai.Calls.Where(c => c.Kind != "function"), c => Assert.Equal("documentation", c.Evidence));
        Assert.Contains(ai.Calls, c => c.Fragment.Contains("FILE DOC"));
        Assert.Contains(ai.Calls, c => c.Fragment.Contains("CLASS DOC"));
        var before = ai.Calls.Count;
        Assert.Equal(1, (await service.ScanAsync()).Unchanged);
        Assert.Equal(before, ai.Calls.Count);
    }

    [ServerFact]
    public async Task OversizedDocumentationWithNoFunctionsSkipsWithoutPublishingEmptyVectors()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.cs"), "// " + new string('x', 5000) + "\nusing System;");
        var settings = TextRagTests.Settings(input) with { Management = ManagementSettings.CreateDefault() };
        var ai = new HierarchyAi();
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        var cycle = await service.ScanAsync();
        Assert.Equal(0, cycle.Failed);
        Assert.Equal(1, cycle.Excluded);
        var document = Assert.Single(await repo.DocumentsAsync(default));
        Assert.Equal("skipped", document.Status);
        Assert.Empty(ai.Calls);
        Assert.Empty(ai.Embeddings);
        Assert.Equal(1, (await service.ScanAsync()).Unchanged);
    }

    private sealed class HierarchyAi : ITextRagAi
    {
        public bool RejectContainers { get; init; }
        public List<(string Kind, string Evidence, string Fragment)> Calls { get; } = [];
        public List<string> Embeddings { get; } = [];
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) => Task.FromResult(2400);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            Assert.True(TextAiPipeline.InputBytes(JsonSerializer.Deserialize<JsonElement>(input)) <= 2400);
            using var parsed = JsonDocument.Parse(input);
            var root = parsed.RootElement;
            var kind = root.GetProperty("Kind").GetString()!;
            Calls.Add((kind, root.GetProperty("evidence").GetString()!, root.GetProperty("fragment").GetString()!));
            return Task.FromResult(RejectContainers && kind != "function" ? "{\"description\":42}" :
                JsonSerializer.Serialize(new { description = "description " + kind + " " + root.GetProperty("name").GetString() }));
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            Embeddings.AddRange(inputs);
            return Task.FromResult(new VectorBatch(3, inputs.Select(s => s.Contains("function") ? new float[] { 1, 0, 0 } :
                s.Contains("class") ? new float[] { 0, 1, 0 } : new float[] { 0, 0, 1 }).ToArray()));
        }
    }
}
