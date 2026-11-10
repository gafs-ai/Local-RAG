using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private async Task<TextDocumentState> PrepareParentEmbeddingsAsync(TextDocumentState state, TextAiPipeline pipeline,
        TextRagSettings options, IProgress<string>? progress, CancellationToken token)
    {
        if (options.ParentEmbedding == ParentEmbeddingMode.None || state.Parents is not { Count: > 0 })
        {
            state = state with { ParentEmbeddingsCompleted = true };
            await repository.SaveAsync(state, token).ConfigureAwait(false);
            return state;
        }
        var embeddingBudget = await IngestionAi.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        var checkpoints = state.ParentEmbeddings.ToList();
        foreach (var parent in state.Parents)
        {
            var position = checkpoints.FindIndex(p => p.ParentId == parent.Id);
            if (position < 0) { position = checkpoints.Count; checkpoints.Add(new(parent.Id)); }
            var checkpoint = checkpoints[position];
            var fits = Encoding.UTF8.GetByteCount(parent.Text) <= embeddingBudget;
            if (checkpoint.Completed && !fits && options.ParentEmbedding == ParentEmbeddingMode.TextOrSummary && checkpoint.Kind == "parent_summary") continue;
            if (checkpoint.Completed && !fits && options.ParentEmbedding == ParentEmbeddingMode.TextOrSummary)
                checkpoint = new(parent.Id) { InputHash = checkpoint.InputHash, RecipeId = checkpoint.RecipeId };
            async Task Save()
            {
                checkpoints[position] = checkpoint;
                state = state with { ParentEmbeddings = checkpoints.ToList() };
                await repository.SaveAsync(state, token).ConfigureAwait(false);
            }
            if (fits)
                checkpoint = checkpoint with { EmbeddingText = parent.Text, Kind = "parent_text", Skipped = false };
            else if (options.ParentEmbedding == ParentEmbeddingMode.TextWhenFits)
                checkpoint = checkpoint with { Skipped = true, EmbeddingText = null };
            else
            {
                try
                {
                    var outputTokens = Math.Min(512, options.MaxOutputTokens);
                    var budget = await pipeline.InputBudgetAsync(options.ParentDescriptionPrompt, token, outputTokens).ConfigureAwait(false);
                    object Input(string text, string stage) => new { stage, fragment = text };
                    async Task<string> Summarize(string text, string stage)
                    {
                        progress?.Report("親チャンクの説明を作成中：" + (parent.Title ?? parent.Id));
                        return await pipeline.JsonAsync(options.ParentDescriptionPrompt, Input(text, stage), root =>
                        {
                            var value = TextAiPipeline.StringProperty(root, "description");
                            if (string.IsNullOrWhiteSpace(value) || value.Length > 4000)
                                throw new PipelineValidationException("response_schema", "親チャンク説明の形式が不正です。");
                            return value;
                        }, token, outputTokens).ConfigureAwait(false);
                    }
                    var children = state.Chunks!.Where(c => c.ParentId == parent.Id && c.EmbeddingKind == "child").ToArray();
                    if (checkpoint.ReductionInput is null)
                    {
                        for (var i = checkpoint.ChildIndex; i < children.Length; i++)
                        {
                            var child = children[i];
                            foreach (var part in TextWindows.Split(child.Text[checkpoint.Offset..], s => TextAiPipeline.InputBytes(Input(s, "child")) <= budget))
                            {
                                var description = await Summarize(part, "child");
                                checkpoint = checkpoint with { Offset = checkpoint.Offset + part.Length, Descriptions = [.. checkpoint.Descriptions, description] };
                                await Save();
                            }
                            checkpoint = checkpoint with { ChildIndex = i + 1, Offset = 0 };
                            await Save();
                        }
                        checkpoint = checkpoint with { ReductionInput = string.Join("\n\n", checkpoint.Descriptions), Offset = 0 };
                        await Save();
                    }
                    // Reduce all child descriptions in bounded groups; never drop a tail to fit.
                    while (checkpoint.EmbeddingText is null && !checkpoint.Skipped)
                    {
                        var input = checkpoint.ReductionInput!;
                        if (string.IsNullOrWhiteSpace(input)) { checkpoint = checkpoint with { Skipped = true }; break; }
                        if (TextAiPipeline.InputBytes(Input(input, "parent")) <= budget)
                        {
                            checkpoint = checkpoint with { EmbeddingText = await Summarize(input, "parent") };
                            break;
                        }
                        foreach (var part in TextWindows.Split(input[checkpoint.Offset..], s => TextAiPipeline.InputBytes(Input(s, "parent")) <= budget))
                        {
                            var description = await Summarize(part, "parent");
                            checkpoint = checkpoint with { Offset = checkpoint.Offset + part.Length, Reduced = [.. checkpoint.Reduced, description] };
                            await Save();
                        }
                        var reduced = string.Join("\n\n", checkpoint.Reduced);
                        if (Encoding.UTF8.GetByteCount(reduced) >= Encoding.UTF8.GetByteCount(input) || checkpoint.Round >= 7)
                        { checkpoint = checkpoint with { Skipped = true }; break; }
                        checkpoint = checkpoint with { ReductionInput = reduced, Offset = 0, Reduced = [], Round = checkpoint.Round + 1 };
                        await Save();
                    }
                }
                catch (Exception ex) when (InvalidDescription(ex) || ex is PipelineValidationException { Code: "context_capacity" or "context_configuration" })
                { token.ThrowIfCancellationRequested(); checkpoint = checkpoint with { Skipped = true }; }
            }
            if (checkpoint.Skipped)
                state = state with { ProcessingNotices = state.ProcessingNotices.Append("parent_embedding_skipped").Distinct().ToList() };
            checkpoint = checkpoint with { Completed = true };
            await Save();
        }
        var chunks = state.Chunks!.ToList();
        foreach (var checkpoint in checkpoints.Where(p => !p.Skipped && p.EmbeddingText is not null))
        {
            var parent = state.Parents.Single(p => p.Id == checkpoint.ParentId);
            if (checkpoint.Kind == "parent_text")
                chunks.Add(new(parent.Start, parent.End, checkpoint.EmbeddingText!, parent.Id, checkpoint.Kind));
            else
                foreach (var part in TextWindows.Split(checkpoint.EmbeddingText!, options.MaxChunkCharacters, embeddingBudget))
                    chunks.Add(new(parent.Start, parent.End, part, parent.Id, checkpoint.Kind));
        }
        state = state with { Chunks = chunks, ParentEmbeddingsCompleted = true };
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }
}
