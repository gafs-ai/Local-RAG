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
        var budget = await pipeline.InputBudgetAsync(options.CodePrompt, token).ConfigureAwait(false);
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
                var description = await pipeline.JsonAsync(options.CodePrompt, Input(part), root =>
                {
                    var value = TextAiPipeline.StringProperty(root, "description");
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 4000)
                        throw new PipelineValidationException("response_schema", "コード説明の本文が不正です。");
                    return value;
                }, token).ConfigureAwait(false);
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
            var text = $"説明（AI生成・長文は断片ごとの説明）：\n{description}\n\nソースコード：\n{unit.Code}";
            parents.Add(new(unit.Id, i + 1, i + 1, text, title, unit.Kind, unit.StartLine, unit.EndLine, unit.ContainerId));
            foreach (var part in TextWindows.Split(description, options.MaxChunkCharacters, embeddingBudget))
                chunks.Add(new(i + 1, i + 1, part, unit.Id));
        }
        state = state with { Chunks = chunks, Parents = parents };
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }
}
