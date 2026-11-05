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
        // Prose descriptions require more output than category IDs or paragraph ranges.
        // Reserve the same output budget during subdivision and both completion attempts.
        var outputTokens = Math.Min(options.MaxOutputTokens, 512);
        var budget = await pipeline.InputBudgetAsync(options.CodePrompt, token, outputTokens).ConfigureAwait(false);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.DescribedCharacters < 0 || unit.DescribedCharacters > unit.Code.Length) throw new InvalidDataException("コード説明の再開位置が不正です。");
            object Input(string part) => new { language = SourceLanguages.Find(options.SourceMode)?.Name ?? Path.GetExtension(state.Path), unit.Kind,
                name = unit.Name[..Math.Min(200, unit.Name.Length)], fragment = part };
            foreach (var part in TextWindows.Split(unit.Code[unit.DescribedCharacters..], s => TextAiPipeline.InputBytes(Input(s)) <= budget))
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"コード説明を作成中：{i + 1}/{units.Count}（{unit.Kind}）");
                string description;
                try
                {
                    description = await pipeline.JsonAsync(options.CodePrompt, Input(part), root =>
                    {
                        var value = TextAiPipeline.StringProperty(root, "description");
                        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000)
                            throw new PipelineValidationException("response_schema", "コード説明の本文が不正です。");
                        return value;
                    }, token, outputTokens).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException ||
                    ex is PipelineValidationException { Code: "response_schema" or "output_incomplete" })
                {
                    token.ThrowIfCancellationRequested();
                    // Invalid model output is discarded after the correction attempt. Index only
                    // known structural facts, never a guessed behavior or the raw source as prose.
                    description = $"構造情報：{unit.Kind} {unit.Name}（{unit.StartLine}–{unit.EndLine}行）。" +
                        "この断片の動作説明はAI応答が不正のため未作成です。";
                    unit = unit with { DescriptionFallbackUsed = true };
                    state = state with { ProcessingNotices = state.ProcessingNotices.Append("code_description_structural").Distinct().ToList() };
                }
                unit = unit with { DescribedCharacters = unit.DescribedCharacters + part.Length, Descriptions = [.. unit.Descriptions, description] };
                units[i] = unit;
                state = state with { CodeUnits = units.ToList() };
                await repository.SaveAsync(state, token).ConfigureAwait(false);
            }
        }
        var chunks = new List<TextChunk>();
        var parents = new List<TextParentChunk>();
        var embeddingBudget = await ai.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var description = string.Join("\n\n", unit.Descriptions);
            if (description.Length == 0) throw new InvalidDataException("コード説明がありません。");
            var title = $"{unit.Kind}: {unit.Name}（{unit.StartLine}–{unit.EndLine}行）";
            var label = unit.DescriptionFallbackUsed ? "説明（AI生成・一部は構造情報のみ。動作説明が未作成の断片あり）" : "説明（AI生成・長文は断片ごとの説明）";
            var text = $"{label}：\n{description}\n\nソースコード：\n{unit.Code}";
            parents.Add(new(unit.Id, i + 1, i + 1, text, title, unit.Kind, unit.StartLine, unit.EndLine, unit.ContainerId));
            foreach (var part in TextWindows.Split(description, options.MaxChunkCharacters, embeddingBudget))
                chunks.Add(new(i + 1, i + 1, part, unit.Id));
        }
        state = state with { Chunks = chunks, Parents = parents };
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }
}
