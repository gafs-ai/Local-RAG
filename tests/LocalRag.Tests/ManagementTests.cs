using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class ManagementTests
{
    private static ManagementSettings Defaults() => ManagementSettings.FromLegacy(new(), new());

    [Fact]
    public async Task OldSearchSettingsAreRemovedWithoutChangingCustomPromptsOrDisclosurePolicy()
    {
        var managed = Defaults() with
        {
            UserPrompts = [new("custom", "独自分類", PromptPurpose.Classification, "Keep my prompt"),
                new("query", "旧検索指示", PromptPurpose.Search, "Old instruction")],
            Retrieval = new() { LlmEnabled = true, Patterns = ["private"] }
        };
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new AppSettings { Management = managed }))!;
        json["TextPipeline"]!["QueryInstruction"] = "Old instruction";
        json["Management"]!["Retrieval"]!["SearchPromptId"] = "query";
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var original = json.ToJsonString();
        await File.WriteAllTextAsync(store.FilePath, original);

        var loaded = await store.LoadAsync();
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
        var settings = Assert.IsType<ManagementSettings>(loaded.Management);
        Assert.Empty(settings.Validate());
        Assert.Equal("Keep my prompt", Assert.Single(settings.UserPrompts).Text);
        Assert.DoesNotContain(settings.Prompts, p => p.Purpose == PromptPurpose.Search);
        Assert.True(settings.Retrieval.LlmEnabled);
        Assert.Equal(["private"], settings.Retrieval.Patterns);
        Assert.Equal(new TextRagSettings().ClassifyPrompt, loaded.PipelineFor("a.txt").ClassifyPrompt);

        await store.SaveAsync(loaded);
        var saved = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("QueryInstruction", saved);
        Assert.DoesNotContain("SearchPromptId", saved);
        Assert.DoesNotContain("Old instruction", saved);
        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize((await store.LoadAsync()).Management));
    }

    [Fact]
    public async Task LegacyMigrationPreservesCustomAndInactivePromptsWithoutRewritingFile()
    {
        var legacy = new TextRagSettings
        {
            ClassifyPrompt = "custom classification", FormatEnabled = false,
            Categories = [new() { Description = new string('名', 500), FormatEnabled = false, FormatPrompt = "inactive custom" },
                new() { Id = "code", Description = "", FormatEnabled = true, FormatPrompt = "active custom", ChunkEnabled = false }]
        };
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var policy = new ConfidentialityPolicy { Enabled = false, Patterns = ["private"] };
        await store.SaveAsync(new AppSettings { SchemaVersion = 1, TextPipeline = legacy, Confidentiality = policy });
        var before = await File.ReadAllTextAsync(store.FilePath);
        var loaded = await store.LoadAsync();
        Assert.Equal(before, await File.ReadAllTextAsync(store.FilePath));
        Assert.Equal(2, loaded.SchemaVersion);
        var managed = Assert.IsType<ManagementSettings>(loaded.Management);
        Assert.Empty(managed.Validate());
        Assert.Contains(managed.UserPrompts, p => p.Text == "inactive custom");
        Assert.Equal(500, managed.Categories[0].Description.Length);
        var effective = loaded.PipelineFor("document.txt");
        Assert.Equal("custom classification", effective.ClassifyPrompt);
        Assert.False(effective.Categories[0].FormatEnabled);
        Assert.True(effective.Categories[1].FormatEnabled);
        Assert.Equal("active custom", effective.Categories[1].FormatPrompt);
        Assert.False(effective.Categories[1].ChunkEnabled);
        Assert.False(loaded.DisclosurePolicy().Enabled);
        await store.SaveAsync(loaded);
        var reopened = await store.LoadAsync();
        Assert.Equal(JsonSerializer.Serialize(managed), JsonSerializer.Serialize(reopened.Management));
    }

    [Fact]
    public void BuiltInPromptsCannotBeOverriddenAndReferencesMustMatchPurpose()
    {
        var settings = Defaults();
        Assert.Empty(settings.Validate());
        Assert.NotEmpty((settings with { UserPrompts = [new("system.format", "changed", PromptPurpose.Format, "override")] }).Validate());
        Assert.NotEmpty((settings with { DefaultTextRoute = settings.DefaultTextRoute with { ClassifyPromptId = "system.chunk" } }).Validate());
        Assert.NotEmpty((settings with { Retrieval = settings.Retrieval with { ConfidentialityPromptId = "missing" } }).Validate());
        Assert.NotEmpty((settings with { Retrieval = settings.Retrieval with { Patterns = ["["] } }).Validate());
        Assert.NotEqual("override", SystemPrompts.All.Single(p => p.Id == "system.format").Text);
    }

    [Fact]
    public void RoutesSupportFallbackSubsetOptionalDescriptionAndPerCategoryOverrides()
    {
        var settings = Defaults();
        settings = settings with
        {
            Categories = [new("general", "一般"), new("code", "コード")], TextExtensions = [".md", ".cs"],
            DefaultTextRoute = new() { EnabledCategoryIds = ["general"], ClassifyEnabled = false },
            ExtensionRoutes = [new() { Extension = ".cs", EnabledCategoryIds = ["code"], DefaultCategoryId = "code",
                CategoryOverrides = [new("code", "", "system.chunk")] }]
        };
        Assert.Empty(settings.Validate());
        var md = settings.Resolve(new(), "guide.md");
        Assert.False(md.ClassifyEnabled);
        Assert.Equal("general", Assert.Single(md.Categories).Id);
        var cs = settings.Resolve(new(), "Source.CS");
        var code = Assert.Single(cs.Categories);
        Assert.Equal("code", code.Id);
        Assert.Equal("コード", code.Description);
        Assert.False(code.FormatEnabled);
        Assert.True(code.ChunkEnabled);
        Assert.NotEmpty((settings with { TextExtensions = [".md", ".md"] }).Validate());
        Assert.NotEmpty((settings with { DefaultTextRoute = settings.DefaultTextRoute with { DefaultCategoryId = "code" } }).Validate());
    }

    [Fact]
    public void UnusedPromptAndRetrievalPolicyChangesDoNotInvalidateIngestion()
    {
        var settings = Defaults();
        var limits = new TextRagSettings();
        var ai = new AiConnectionSettings { EmbeddingModel = "nomic-embed-text-v2-moe-GGUF" };
        var changed = settings with
        {
            UserPrompts = [new("private_prompt", "秘密検査", PromptPurpose.Confidentiality, "custom inspection"),
                new("unused", "未使用", PromptPurpose.Chunk, "unused")],
            Retrieval = settings.Retrieval with { ConfidentialityEnabled = false, ConfidentialityPromptId = "private_prompt" }
        };
        Assert.Equal(settings.ConfigurationId(limits, ai), changed.ConfigurationId(limits, ai));
        Assert.Equal(settings.ApplyRetrieval(limits).SpaceId(ai, 768), changed.ApplyRetrieval(limits).SpaceId(ai, 768));
        Assert.NotEqual(settings.ConfigurationId(limits, ai), (settings with { DefaultTextRoute = settings.DefaultTextRoute with { FormatPromptId = "" } }).ConfigurationId(limits, ai));
        Assert.NotEqual(settings.ConfigurationId(limits, ai), (settings with { TextExtensions = [".md"] }).ConfigurationId(limits, ai));
    }

    [Fact]
    public async Task EffectivePromptAndStageSelectionReachAi()
    {
        var managed = Defaults() with
        {
            UserPrompts = [new("custom", "コード分類", PromptPurpose.Classification, "custom classifier")],
            TextExtensions = [".cs"],
            ExtensionRoutes = [new() { Extension = ".cs", ClassifyPromptId = "custom", EnabledCategoryIds = ["general"], FormatPromptId = "", ChunkPromptId = "" }]
        };
        var ai = new RecordingAi();
        var settings = managed.Resolve(new(), "a.cs");
        var pipeline = new TextAiPipeline(ai, settings);
        Assert.Equal("general", await pipeline.ClassifyAsync("code", default));
        var paragraphs = await pipeline.FormatAsync([new(1, "one"), new(2, "two")], settings.Categories[0], default);
        Assert.Equal("one\n\ntwo", Assert.Single(await pipeline.ChunkAsync(paragraphs, settings.Categories[0], default)).Text);
        Assert.Equal("custom classifier", Assert.Single(ai.Systems));
    }

    [ServerFact]
    public async Task ProjectSourceAndDocumentUseRoutesAndSharedRetrievalPolicy()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "LocalRAG.slnx"))) repoRoot = repoRoot.Parent;
        Assert.NotNull(repoRoot);
        var input = Path.Combine(fixture.Root, "inputs");
        Directory.CreateDirectory(input);
        var files = new[] { "src/LocalRag.Core/ITextRetrieval.cs", "Documents/Release-0.0.0.md" };
        foreach (var relative in files) File.Copy(Path.Combine(repoRoot.FullName, relative), Path.Combine(input, Path.GetFileName(relative)));
        var management = Defaults() with
        {
            TextExtensions = [".md", ".cs"], DefaultTextRoute = new() { ClassifyEnabled = false, FormatPromptId = "", EnabledCategoryIds = ["general"] },
            ExtensionRoutes = [new() { Extension = ".cs", ClassifyEnabled = false, FormatPromptId = "", ChunkPromptId = "", EnabledCategoryIds = ["general"] }],
            Retrieval = new() { LlmEnabled = false, Patterns = ["never-matches-test-marker"] }
        };
        var settings = TextRagTests.Settings(input) with { Management = management, TextPipeline = new() { MaxDocumentCharacters = 24000, MaxChunkCharacters = 24000 } };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        using var service = new TextRagService(repository, new FakeTextAi(), settings, Path.Combine(fixture.Root, "state"));
        var result = await service.ScanAsync();
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Indexed);
        var documents = await repository.DocumentsAsync(default);
        foreach (var document in documents)
        {
            Assert.Equal(await File.ReadAllTextAsync(document.Path), (await service.FetchAsync(document.DocumentId))!.Content);
        }
        Assert.NotEmpty(await service.SearchAsync("release"));
        // A retrieval-only policy edit applies immediately to old indexed documents, without re-embedding.
        var secretPolicy = management with { Retrieval = management.Retrieval with { Patterns = ["LocalRag|Local RAG"] } };
        using var blocked = new TextRagService(repository, new FakeTextAi(), settings with { Management = secretPolicy }, Path.Combine(fixture.Root, "state"));
        foreach (var document in documents)
        {
            var withheld = await blocked.FetchAsync(document.DocumentId);
            Assert.NotNull(withheld);
            Assert.Equal(DisclosureDecision.Withheld, withheld.Decision);
            Assert.Null(withheld.Content);
            Assert.Null(withheld.SourcePath);
        }
        var secretHits = await blocked.SearchAsync("release");
        Assert.NotEmpty(secretHits);
        Assert.All(secretHits, h => Assert.Null(h.Content));
        var removed = management with { TextExtensions = [".md"], ExtensionRoutes = [] };
        using var changed = new TextRagService(repository, new FakeTextAi(), settings with { Management = removed }, Path.Combine(fixture.Root, "state"));
        Assert.Null(await changed.FetchAsync(documents.Single(d => d.Path.EndsWith(".cs")).DocumentId));
    }

    private sealed class RecordingAi : ITextRagAi
    {
        public List<string> Systems { get; } = [];
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        { Systems.Add(system); return Task.FromResult("{\"category\":\"general\"}"); }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }
}
