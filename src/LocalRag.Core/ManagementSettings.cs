using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PromptPurpose>))]
public enum PromptPurpose { Classification, Format, Chunk, Search, Confidentiality }

public sealed record ManagedPrompt(string Id, string Name, PromptPurpose Purpose, string Text);
public sealed record ManagedCategory(string Id, string Name, string Description = "", bool Excluded = false);
// null inherits the route default; empty skips the stage; otherwise a prompt ID.
public sealed record CategorySteps(string CategoryId, string? FormatPromptId = null, string? ChunkPromptId = null);
public sealed record TextRoute
{
    public string Extension { get; init; } = "*";
    public bool ClassifyEnabled { get; init; } = true;
    public string ClassifyPromptId { get; init; } = "system.classification";
    public List<string> EnabledCategoryIds { get; init; } = ["general", "specification", "procedure", "meeting", "excluded"];
    public string DefaultCategoryId { get; init; } = "general";
    public string FormatPromptId { get; init; } = "system.format";
    public string ChunkPromptId { get; init; } = "system.chunk";
    public List<CategorySteps> CategoryOverrides { get; init; } = [];
}

// Retrieval has no extension/category branches and is shared by future media pipelines.
public sealed record RetrievalPipelineSettings
{
    public string SearchPromptId { get; init; } = "system.search";
    public string ConfidentialityPromptId { get; init; } = "system.confidentiality";
    public bool ConfidentialityEnabled { get; init; } = true;
    public bool PatternsEnabled { get; init; } = true;
    public bool LlmEnabled { get; init; } = true;
    public List<string> Patterns { get; init; } = ["社外秘", "極秘", "CONFIDENTIAL"];
    public ConfidentialityPolicy Policy() => new()
    {
        Enabled = ConfidentialityEnabled, PatternsEnabled = PatternsEnabled, LlmEnabled = LlmEnabled, Patterns = [.. Patterns]
    };
}

public static class SystemPrompts
{
    // Never deserialize system definitions from user settings. Return immutable records.
    public static IReadOnlyList<ManagedPrompt> All { get; } = Array.AsReadOnly(new[]
    {
        new ManagedPrompt("system.classification", "標準：文書の分類", PromptPurpose.Classification, new TextRagSettings().ClassifyPrompt),
        new ManagedPrompt("system.format", "標準：不要段落の除去", PromptPurpose.Format, new TextRagSettings().FormatPrompt),
        new ManagedPrompt("system.chunk", "標準：段落をまとめて分割", PromptPurpose.Chunk, TextRagSettings.DefaultChunkPrompt),
        new ManagedPrompt("system.search", "標準：検索指示", PromptPurpose.Search, new TextRagSettings().QueryInstruction),
        new ManagedPrompt("system.confidentiality", "標準：秘匿判定", PromptPurpose.Confidentiality, new TextRagSettings().ConfidentialityPrompt)
    });
}

public sealed record ManagementSettings
{
    public int Version { get; init; } = 1;
    public List<ManagedPrompt> UserPrompts { get; init; } = [];
    public List<ManagedCategory> Categories { get; init; } = [];
    public List<string> TextExtensions { get; init; } = [".txt", ".md"];
    public TextRoute DefaultTextRoute { get; init; } = new();
    public List<TextRoute> ExtensionRoutes { get; init; } = [];
    public RetrievalPipelineSettings Retrieval { get; init; } = new();

    [JsonIgnore] public IEnumerable<ManagedPrompt> Prompts => SystemPrompts.All.Concat(UserPrompts);
    public string Prompt(string id) => Prompts.Single(p => p.Id == id).Text;
    public TextRoute RouteFor(string path) => ExtensionRoutes.SingleOrDefault(r =>
        string.Equals(r.Extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)) ?? DefaultTextRoute;

    public TextRagSettings Resolve(TextRagSettings limits, string path)
    {
        var route = RouteFor(path);
        TextCategory ResolveCategory(ManagedCategory c)
        {
            var steps = route.CategoryOverrides.SingleOrDefault(s => s.CategoryId == c.Id);
            var format = steps?.FormatPromptId ?? route.FormatPromptId;
            var chunk = steps?.ChunkPromptId ?? route.ChunkPromptId;
            return new TextCategory { Id = c.Id, Description = string.IsNullOrEmpty(c.Description) || c.Description == c.Name ? c.Name : c.Name + ": " + c.Description,
                Excluded = c.Excluded, FormatEnabled = format.Length != 0, ChunkEnabled = chunk.Length != 0,
                FormatPrompt = format.Length == 0 ? null : Prompt(format), ChunkPrompt = chunk.Length == 0 ? null : Prompt(chunk) };
        }
        return ApplyRetrieval(limits) with
        {
            ClassifyEnabled = route.ClassifyEnabled, ClassifyPrompt = Prompt(route.ClassifyPromptId),
            DefaultCategory = route.DefaultCategoryId, FormatEnabled = route.FormatPromptId.Length != 0,
            ChunkEnabled = route.ChunkPromptId.Length != 0,
            FormatPrompt = route.FormatPromptId.Length == 0 ? limits.FormatPrompt : Prompt(route.FormatPromptId),
            ChunkPrompt = route.ChunkPromptId.Length == 0 ? limits.ChunkPrompt : Prompt(route.ChunkPromptId),
            Categories = Categories.Where(c => route.EnabledCategoryIds.Contains(c.Id)).Select(ResolveCategory).ToList()
        };
    }

    public TextRagSettings ApplyRetrieval(TextRagSettings limits) => limits with
    { QueryInstruction = Prompt(Retrieval.SearchPromptId), ConfidentialityPrompt = Prompt(Retrieval.ConfidentialityPromptId) };

    public string ConfigurationId(TextRagSettings limits, AiConnectionSettings ai) => TextRagSettings.Hash(JsonSerializer.Serialize(new
    {
        version = "managed-text-v1",
        routes = TextExtensions.Order(StringComparer.Ordinal).Select(ext => new { extension = ext, configuration = Resolve(limits, "file" + ext).ConfigurationId(ai) })
    }));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Version != 1) errors.Add("管理設定の版には対応していません。");
        if (UserPrompts is null || Categories is null || TextExtensions is null || DefaultTextRoute is null || ExtensionRoutes is null || Retrieval is null)
            return ["管理設定の必須項目がありません。"];
        if (UserPrompts.Count > 200 || Categories.Count is < 1 or > 32 || ExtensionRoutes.Count > 64 || TextExtensions.Count is < 1 or > 64)
            errors.Add("プロンプトは200件以内、カテゴリは1～32件、拡張子は1～64件で指定してください。");
        var prompts = SystemPrompts.All.ToDictionary(p => p.Id);
        foreach (var p in UserPrompts)
        {
            if (p is null || !ValidId(p.Id) || p.Id.StartsWith("system", StringComparison.OrdinalIgnoreCase) || !prompts.TryAdd(p.Id, p))
            { errors.Add("ユーザープロンプトのIDが不正または重複しています。"); continue; }
            if (!Enum.IsDefined(p.Purpose) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100 || p.Text is null ||
                (p.Purpose != PromptPurpose.Search && string.IsNullOrWhiteSpace(p.Text)) || p.Text.Length > (p.Purpose == PromptPurpose.Search ? 1000 : 8000))
                errors.Add("プロンプトの名前・用途・本文を確認してください（本文は最大8000文字、検索用は1000文字）。");
        }
        var ids = new HashSet<string>();
        foreach (var c in Categories)
        {
            if (c is null || !ValidId(c.Id) || !ids.Add(c.Id)) { errors.Add("カテゴリIDが不正または重複しています。"); continue; }
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100 || c.Description is null || c.Description.Length > 500)
                errors.Add("カテゴリ名は1～100文字、説明は空欄または500文字以内です。");
        }
        bool HasPrompt(string? id, PromptPurpose purpose, bool skip = false) => id is not null &&
            ((skip && id.Length == 0) || (prompts.TryGetValue(id, out var p) && p.Purpose == purpose));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in TextExtensions)
            if (!ValidExtension(ext) || !extensions.Add(ext)) errors.Add("対象拡張子は重複しない .txt のような形式で指定してください。");
        if (DefaultTextRoute.Extension != "*") errors.Add("共通設定の拡張子指定が不正です。");
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in ExtensionRoutes)
            if (route is null || !extensions.Contains(route.Extension) || !routes.Add(route.Extension)) errors.Add("拡張子別設定は対象拡張子ごとに1つだけ指定してください。");
        foreach (var route in new[] { DefaultTextRoute }.Concat(ExtensionRoutes))
        {
            if (route is null || route.EnabledCategoryIds is null || route.CategoryOverrides is null) { errors.Add("パイプライン設定が不正です。"); continue; }
            if (route.EnabledCategoryIds.Count == 0 || route.EnabledCategoryIds.Distinct().Count() != route.EnabledCategoryIds.Count ||
                route.EnabledCategoryIds.Any(id => !ids.Contains(id)) || !route.EnabledCategoryIds.Contains(route.DefaultCategoryId))
                errors.Add($"{route.Extension}: 有効カテゴリを選び、その中から分類省略時のカテゴリを選んでください。");
            if (!HasPrompt(route.ClassifyPromptId, PromptPurpose.Classification) || !HasPrompt(route.FormatPromptId, PromptPurpose.Format, true) || !HasPrompt(route.ChunkPromptId, PromptPurpose.Chunk, true))
                errors.Add($"{route.Extension}: プロンプトが見つからないか用途が違います。");
            var seen = new HashSet<string>();
            foreach (var step in route.CategoryOverrides)
                if (step is null || !ids.Contains(step.CategoryId) || !seen.Add(step.CategoryId) ||
                    (step.FormatPromptId is not null && !HasPrompt(step.FormatPromptId, PromptPurpose.Format, true)) ||
                    (step.ChunkPromptId is not null && !HasPrompt(step.ChunkPromptId, PromptPurpose.Chunk, true))) errors.Add($"{route.Extension}: カテゴリ別設定を確認してください。");
        }
        if (!HasPrompt(Retrieval.SearchPromptId, PromptPurpose.Search) || !HasPrompt(Retrieval.ConfidentialityPromptId, PromptPurpose.Confidentiality))
            errors.Add("取得パイプラインのプロンプトが見つからないか用途が違います。");
        if (Retrieval.Patterns is null || Retrieval.Patterns.Count > 100 || Retrieval.Patterns.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 2000))
            errors.Add("秘匿パターンは空行を除き100件以内、各2000文字以内で指定してください。");
        else foreach (var pattern in Retrieval.Patterns)
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { errors.Add("秘匿パターンに正しくない正規表現があります。"); break; }
        return errors;
    }
    public static bool ValidExtension(string? value) => value is not null && Regex.IsMatch(value, "^\\.[a-z0-9][a-z0-9_+-]{0,19}$");
    private static bool ValidId(string? value) => value is not null && Regex.IsMatch(value, "^[a-z][a-z0-9_]{0,39}$");

    public static ManagementSettings FromLegacy(TextRagSettings legacy, ConfidentialityPolicy policy)
    {
        var prompts = new List<ManagedPrompt>();
        string Import(string text, PromptPurpose purpose, string name)
        {
            var existing = SystemPrompts.All.Concat(prompts).FirstOrDefault(p => p.Purpose == purpose && p.Text == text);
            if (existing is not null) return existing.Id;
            var id = "user_" + Guid.NewGuid().ToString("N");
            prompts.Add(new(id, "移行：" + name, purpose, text)); return id;
        }
        var format = Import(legacy.FormatPrompt, PromptPurpose.Format, "整形");
        var chunk = Import(legacy.ChunkPrompt, PromptPurpose.Chunk, "分割");
        // Keep even inactive custom prompts available in the manager after migration.
        foreach (var category in legacy.Categories)
        {
            if (category.FormatPrompt is not null) Import(category.FormatPrompt, PromptPurpose.Format, category.Id + " 整形");
            if (category.ChunkPrompt is not null) Import(category.ChunkPrompt, PromptPurpose.Chunk, category.Id + " 分割");
        }
        var route = new TextRoute
        {
            ClassifyEnabled = legacy.ClassifyEnabled, ClassifyPromptId = Import(legacy.ClassifyPrompt, PromptPurpose.Classification, "分類"),
            DefaultCategoryId = legacy.DefaultCategory, EnabledCategoryIds = legacy.Categories.Select(c => c.Id).ToList(),
            FormatPromptId = legacy.FormatEnabled ? format : "", ChunkPromptId = legacy.ChunkEnabled ? chunk : "",
            CategoryOverrides = legacy.Categories.Select(c => new CategorySteps(c.Id,
                c.FormatEnabled == false ? "" : c.FormatEnabled == true ? Import(c.FormatPrompt ?? legacy.FormatPrompt, PromptPurpose.Format, c.Id + " 整形") :
                    c.FormatPrompt is null ? null : legacy.FormatEnabled ? Import(c.FormatPrompt, PromptPurpose.Format, c.Id + " 整形") : "",
                c.ChunkEnabled == false ? "" : c.ChunkEnabled == true ? Import(c.ChunkPrompt ?? legacy.ChunkPrompt, PromptPurpose.Chunk, c.Id + " 分割") :
                    c.ChunkPrompt is null ? null : legacy.ChunkEnabled ? Import(c.ChunkPrompt, PromptPurpose.Chunk, c.Id + " 分割") : "")).ToList()
        };
        var retrieval = new RetrievalPipelineSettings
        {
            SearchPromptId = Import(legacy.QueryInstruction, PromptPurpose.Search, "検索指示"),
            ConfidentialityPromptId = Import(legacy.ConfidentialityPrompt, PromptPurpose.Confidentiality, "秘匿判定"),
            ConfidentialityEnabled = policy.Enabled, PatternsEnabled = policy.PatternsEnabled, LlmEnabled = policy.LlmEnabled, Patterns = [.. policy.Patterns]
        };
        return new() { UserPrompts = prompts, Categories = legacy.Categories.Select(c => new ManagedCategory(c.Id,
            string.IsNullOrWhiteSpace(c.Description) ? c.Id : c.Description[..Math.Min(100, c.Description.Length)], c.Description, c.Excluded)).ToList(), DefaultTextRoute = route, Retrieval = retrieval };
    }
}
