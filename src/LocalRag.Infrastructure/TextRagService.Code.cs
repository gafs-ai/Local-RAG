using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private async Task<TextDocumentState> PrepareCodeAsync(TextDocumentState state, TextAiPipeline pipeline,
        TextRagSettings options, IProgress<string>? progress, CancellationToken token)
    {
        if (state.Chunks is not null) return state;
        if (state.CodeUnits is null)
        {
            progress?.Report("ソースコードの構造を解析しています（必要な解析環境は初回に自動取得します）");
            state = state with { CodeUnits = await SourceCodeParser.ParseAsync(state.Original, state.Path,
                Path.Combine(dataDirectory, "tools"), token, options.SourceMode).ConfigureAwait(false) };
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        var units = state.CodeUnits.ToList();
        var outputTokens = Math.Min(options.MaxOutputTokens, 512);
        var budget = await pipeline.InputBudgetAsync(options.CodePrompt, token, outputTokens).ConfigureAwait(false);
        int Depth(SourceCodeUnit unit) => unit.ContainerId is null ? 0 : 1 + Depth(units.Single(u => u.Id == unit.ContainerId));
        // Complete descendants first; checkpoint each fragment and each skip.
        var order = Enumerable.Range(0, units.Count).OrderBy(i => units[i].Kind == "function" ? 0 : units[i].Kind == "class" ? 1 : 2)
            .ThenByDescending(i => Depth(units[i])).ToArray();
        foreach (var i in order)
        {
            var unit = units[i];
            if (unit.DescriptionCompleted) continue;
            if (unit.DescribedCharacters < 0 || unit.DescribedCharacters > unit.Code.Length) throw new InvalidDataException("コード説明の再開位置が不正です。");
            object Input(string part, string evidence) => new { language = SourceLanguages.Find(options.SourceMode)?.Name ?? Path.GetExtension(state.Path),
                unit.Kind, name = unit.Name[..Math.Min(200, unit.Name.Length)], evidence, fragment = part };
            async Task Save()
            {
                units[i] = unit;
                state = state with { CodeUnits = units.ToList() };
                if (state.Processing?.Stages.GetValueOrDefault("code-description") is { } receipt)
                    state = Stamp(state, "code-description", receipt.Step, Syntax(state.CodeUnits), state.CodeUnits);
                await repository.SaveAsync(state, token).ConfigureAwait(false);
            }
            async Task<string> Describe(object input)
            {
                progress?.Report($"コード説明を作成中：{unit.Kind} {unit.Name}");
                return await pipeline.JsonAsync(options.CodePrompt, input, root =>
                {
                    var value = TextAiPipeline.StringProperty(root, "description");
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 4000)
                        throw new PipelineValidationException("response_schema", "コード説明の本文が不正です。");
                    return value;
                }, token, outputTokens).ConfigureAwait(false);
            }
            if (unit.Kind == "function")
            {
                foreach (var part in TextWindows.Split(unit.Code[unit.DescribedCharacters..], s => TextAiPipeline.InputBytes(Input(s, "source_fragment")) <= budget))
                {
                    token.ThrowIfCancellationRequested();
                    string description;
                    try { description = await Describe(Input(part, "source_fragment")); }
                    catch (Exception ex) when (InvalidDescription(ex))
                    {
                        token.ThrowIfCancellationRequested();
                        description = $"構造情報：{unit.Kind} {unit.Name}（{unit.StartLine}–{unit.EndLine}行）。動作説明はAI応答が不正のため未作成です。";
                        unit = unit with { DescriptionFallbackUsed = true };
                        state = state with { ProcessingNotices = state.ProcessingNotices.Append("code_description_structural").Distinct().ToList() };
                    }
                    unit = unit with { DescribedCharacters = unit.DescribedCharacters + part.Length, Descriptions = [.. unit.Descriptions, description] };
                    await Save();
                }
            }
            else
            {
                // Files use explicit documentation or direct-child descriptions, never all source.
                var evidence = unit.Documentation.Length > 0 ? "documentation" : "members";
                var material = unit.Documentation.Length > 0 ? unit.Documentation : CodeHierarchy.Outline(unit, units);
                if (unit.Kind == "class" && unit.Documentation.Length == 0 && TextAiPipeline.InputBytes(Input(unit.Code, "source")) <= budget)
                { material = unit.Code; evidence = "source"; }
                if (string.IsNullOrWhiteSpace(material) || TextAiPipeline.InputBytes(Input(material, evidence)) > budget)
                    unit = unit with { DescriptionSkipped = true };
                else
                {
                    try { unit = unit with { Descriptions = [await Describe(Input(material, evidence))] }; }
                    catch (Exception ex) when (InvalidDescription(ex))
                    { token.ThrowIfCancellationRequested(); unit = unit with { DescriptionSkipped = true }; }
                }
                if (unit.DescriptionSkipped)
                    state = state with { ProcessingNotices = state.ProcessingNotices.Append("code_" + unit.Kind + "_description_skipped").Distinct().ToList() };
            }
            unit = unit with { DescriptionCompleted = true };
            await Save();
        }
        var chunks = new List<TextChunk>();
        var parents = new List<TextParentChunk>();
        var embeddingBudget = await ai.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            // Retain containers for function hits even if the container embedding was skipped.
            parents.Add(new(unit.Id, i + 1, i + 1, CodeHierarchy.Full(unit), CodeHierarchy.Title(unit), unit.Kind, unit.StartLine, unit.EndLine, unit.ContainerId));
            if (unit.DescriptionSkipped) continue;
            foreach (var part in TextWindows.Split(CodeHierarchy.Description(unit), options.MaxChunkCharacters, embeddingBudget))
                chunks.Add(new(i + 1, i + 1, part, unit.Id, "code_description"));
        }
        state = state with { Chunks = chunks, Parents = parents };
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }

    private static bool InvalidDescription(Exception ex) => ex is JsonException ||
        ex is PipelineValidationException { Code: "response_schema" or "output_incomplete" };
}
