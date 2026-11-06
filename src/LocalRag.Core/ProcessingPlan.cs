using System.Text.Json;

namespace LocalRag.Core;

public sealed record ProcessingStep(string Method, string Parameters, string? Model = null)
{
    public string Id => TextRagSettings.Hash(Method + "\n" + Parameters);
    public static ProcessingStep Create(string method, object parameters, string? model = null) =>
        new(method, JsonSerializer.Serialize(parameters), model);
}

public sealed record ProcessingReceipt(ProcessingStep Step, string InputHash, string OutputHash);
public sealed record ProcessingProvenance
{
    public int Version { get; init; } = 1;
    public string ProfileId { get; init; } = "";
    public Dictionary<string, ProcessingReceipt> Stages { get; init; } = [];
}

/// <summary>Effective settings only. Display names, unused prompts and unrelated routes are not dependencies.</summary>
public sealed record ProcessingPlan(bool IsCode, bool Excluded, ProcessingStep Read, ProcessingStep Classify,
    ProcessingStep Structure, ProcessingStep Format, ProcessingStep Children, ProcessingStep Parents,
    ProcessingStep CodeDescription, ProcessingStep ParentDescription, ProcessingStep Embedding)
{
    public string Id => Hash(new { IsCode, Excluded, Read.Id, Classification = Classify.Id,
        Structure = Structure.Id, Format = Format.Id, Children = Children.Id, Parents = Parents.Id,
        CodeDescription = CodeDescription.Id, ParentDescription = ParentDescription.Id, Embedding = Embedding.Id });
    public static string Hash(object? value) => TextRagSettings.Hash(JsonSerializer.Serialize(value));
    public static bool Current(AppSettings settings, TextDocumentState document) =>
        settings.AllowedTextExtensions().Contains(Path.GetExtension(document.Path), StringComparer.OrdinalIgnoreCase) &&
        (document.Processing is { Version: 1 } p ? p.ProfileId == Create(settings, document.Path, document.CategoryId).Id
            : document.Processing is null && document.ConfigurationId == settings.PipelineConfigurationId());

    public static ProcessingPlan Create(AppSettings app, string path, string? categoryId)
    {
        var s = app.PipelineFor(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var code = s.SourceMode == SourceCodeMode.Code || SourceLanguages.IsCode(s.SourceMode) ||
            s.SourceMode == SourceCodeMode.Auto && extension is ".cs" or ".py";
        var markdown = !code && extension is ".md" or ".markdown";
        var category = s.Categories.SingleOrDefault(c => c.Id == (categoryId ?? s.DefaultCategory));
        var excluded = category is null || category.Excluded;
        var off = ProcessingStep.Create("unused/v1", new { });
        object Llm(string prompt, int output) => new { app.Ai.BaseUrl, app.Ai.ChatModel, prompt, output };
        ProcessingStep Chat(string method, string prompt, object options, int output = 128) =>
            ProcessingStep.Create(method, new { ai = Llm(prompt, Math.Min(s.MaxOutputTokens, output)), options }, app.Ai.ChatModel);
        var classify = s.ClassifyEnabled ? Chat("classify/v1", s.ClassifyPrompt, new { s.DefaultCategory,
            categories = s.Categories.Select(c => new { c.Id, c.Description, c.Excluded }) }) :
            ProcessingStep.Create("fixed-category/v1", new { s.DefaultCategory });
        var structure = code ? ProcessingStep.Create("code-parser/v2", new { s.SourceMode, extension }) :
            ProcessingStep.Create("paragraph-structure/v1", new { markdown,
                heading = markdown ? s.ParentHeadingLevel : 0,
                minimum = markdown && s.EffectiveParentMode == ParentChunkMode.Structure ? s.MarkdownParentMinCharacters : 0,
                maximum = markdown && s.EffectiveParentMode == ParentChunkMode.Structure ? s.MarkdownParentMaxCharacters : 262144 });
        var format = !code && (category?.FormatEnabled ?? s.FormatEnabled)
            ? Chat("remove-paragraphs/v1", category?.FormatPrompt ?? s.FormatPrompt, new { categoryId }) : off;
        var childMode = s.ChildMode == ChildChunkMode.Inherit && markdown && s.MechanicalMarkdown ? ChildChunkMode.Mechanical : s.EffectiveChildMode;
        var join = category?.ChunkEnabled ?? s.ChunkEnabled;
        var children = code ? off : childMode == ChildChunkMode.Llm && join
            ? Chat("child-llm/v1", category?.ChunkPrompt ?? s.ChunkPrompt, new { categoryId, s.MaxChunkCharacters })
            : ProcessingStep.Create("child-mechanical/v1", new { childMode, join = childMode == ChildChunkMode.Mechanical && join, s.MaxChunkCharacters });
        var parents = code ? off : s.EffectiveParentMode == ParentChunkMode.Llm
            ? Chat("parent-llm/v1", category?.ParentPrompt ?? s.ParentPrompt, new { })
            : ProcessingStep.Create("parent-structure/v1", new { mode = s.EffectiveParentMode });
        var parentEnabled = !code && s.EffectiveParentMode != ParentChunkMode.None;
        var parentDescription = !parentEnabled ? off : s.ParentEmbedding == ParentEmbeddingMode.TextOrSummary
            ? Chat("parent-description/v1", s.ParentDescriptionPrompt, new { s.ParentEmbedding }, 512)
            : ProcessingStep.Create("parent-embedding/v1", new { s.ParentEmbedding });
        return new(code, excluded, ProcessingStep.Create("text-reader/v1", new { s.EncodingMode }), classify, structure,
            format, children, parents, code ? Chat("code-description/v2", s.CodePrompt, new { s.SourceMode }, 512) : off,
            parentDescription, ProcessingStep.Create("document-embedding/v1", new { app.Ai.BaseUrl, app.Ai.EmbeddingModel,
                app.TextPipeline.EmbeddingRevision, s.MaxChunkCharacters, profile = EmbeddingInput.Profile(app.Ai.EmbeddingModel) }, app.Ai.EmbeddingModel));
    }
}
