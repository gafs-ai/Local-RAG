using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private static TextDocumentState WithoutNotices(TextDocumentState state, string prefix) => state with
        { ProcessingNotices = state.ProcessingNotices.Where(n => !n.StartsWith(prefix, StringComparison.Ordinal)).ToList() };
    private static bool Reusable(TextDocumentState state, string stage, ProcessingStep step, object input, object? output) =>
        output is not null && state.Processing?.Stages.TryGetValue(stage, out var receipt) == true &&
        receipt.Step.Id == step.Id && receipt.InputHash == ProcessingPlan.Hash(input) && receipt.OutputHash == ProcessingPlan.Hash(output);

    private static TextDocumentState Stamp(TextDocumentState state, string stage, ProcessingStep step, object input, object? output)
    {
        var provenance = state.Processing ?? new();
        var stages = new Dictionary<string, ProcessingReceipt>(provenance.Stages)
        { [stage] = new(step, ProcessingPlan.Hash(input), ProcessingPlan.Hash(output)) };
        return state with { Processing = provenance with { Stages = stages } };
    }

    private static object Syntax(List<SourceCodeUnit>? units) => (units ?? []).Select(u => new
        { u.Id, u.ContainerId, u.Kind, u.Name, u.StartLine, u.EndLine, u.Code, u.Documentation, u.Variables }).ToArray();
    private static object ChildInput(StructuredText structure, List<TextParagraph>? paragraphs) => new { structure.Parents, paragraphs };
    private static object ParentInput(StructuredText structure, List<TextChunk>? children) => new { structure.Parents, children };
    private static object ParentOutput(TextDocumentState state) => new { state.Parents, state.HierarchyChildren };

    // Only import legacy artifacts when their old full-configuration signature is known to match.
    private TextDocumentState AdoptLegacy(TextDocumentState state, TextRagSettings options, ProcessingPlan plan)
    {
        state = state with { Processing = new() { ProfileId = plan.Id } };
        if (state.Chunks is not null) state = Stamp(state, "embedding", plan.Embedding, state.Chunks, state.Chunks);
        if (state.CategoryId is not null) state = Stamp(state, "classify", plan.Classify, state.Original, state.CategoryId);
        if (string.IsNullOrWhiteSpace(state.Original)) return state;
        if (plan.IsCode)
        {
            if (state.CodeUnits is not null)
            {
                state = Stamp(state, "parse", plan.Structure, state.Original, Syntax(state.CodeUnits));
                if (state.CodeUnits!.All(u => u.DescriptionCompleted))
                    state = Stamp(state, "code-description", plan.CodeDescription, Syntax(state.CodeUnits), state.CodeUnits);
            }
            return state;
        }
        var source = TextStructure.Build(state.Original, state.Path, options);
        if (state.Paragraphs is not null) state = Stamp(state, "format", plan.Format, source.Paragraphs, state.Paragraphs);
        if (state.Chunks is not null)
        {
            var children = state.Chunks.Where(c => c.EmbeddingKind == "child").ToList();
            var prepared = children.Select(c => c with { ParentId = source.Parents.Single(p => p.Start <= c.Start && p.End >= c.End).Id }).ToList();
            state = state with { PreparedChildren = prepared, HierarchyChildren = children };
            state = Stamp(state, "children", plan.Children, ChildInput(source, state.Paragraphs), prepared);
            state = Stamp(state, "parents", plan.Parents, ParentInput(source, prepared), ParentOutput(state));
            state = state with { ParentEmbeddings = state.ParentEmbeddings.Select(p => p with
                { InputHash = state.Parents?.SingleOrDefault(parent => parent.Id == p.ParentId) is { } parent ? ProcessingPlan.Hash(parent.Text) : null,
                    RecipeId = plan.ParentDescription.Id }).ToList() };
        }
        return state;
    }

    private async Task<TextDocumentState> PrepareTextStagesAsync(TextDocumentState state, TextAiPipeline pipeline,
        TextRagSettings options, TextCategory category, ProcessingPlan plan, IProgress<string>? progress, Action<string> stage, CancellationToken token)
    {
        var source = TextStructure.Build(DocumentInputs.IsConverted(options.SourceMode) ? state.WordPreparedText ?? state.Original : state.Original, state.Path, options);
        if (!Reusable(state, "format", plan.Format, source.Paragraphs, state.Paragraphs))
        {
            stage("format");
            state = WithoutNotices(state, "format_");
            progress?.Report("整形を更新しています");
            state = state with { Paragraphs = await pipeline.FormatAsync(source.Paragraphs, category, token).ConfigureAwait(false) };
            state = Stamp(state, "format", plan.Format, source.Paragraphs, state.Paragraphs);
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        var input = ChildInput(source, state.Paragraphs);
        if (!Reusable(state, "children", plan.Children, input, state.PreparedChildren))
        {
            stage("chunk");
            state = WithoutNotices(state, "chunk_");
            progress?.Report("子チャンクを更新しています");
            state = state with { PreparedChildren = await TextChunking.BuildChildrenAsync(IngestionAi, pipeline, options, category, state.Path,
                source, state.Paragraphs!, token).ConfigureAwait(false) };
            state = Stamp(state, "children", plan.Children, input, state.PreparedChildren);
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        var parentInput = ParentInput(source, state.PreparedChildren);
        if (state.HierarchyChildren is null || !Reusable(state, "parents", plan.Parents, parentInput, ParentOutput(state)))
        {
            stage("chunk");
            progress?.Report("親チャンクを更新しています");
            var hierarchy = await TextChunking.BuildParentsAsync(pipeline, options, category, source, state.PreparedChildren!, token).ConfigureAwait(false);
            state = state with { Parents = hierarchy.Parents, HierarchyChildren = hierarchy.Children };
            state = Stamp(state, "parents", plan.Parents, parentInput, ParentOutput(state));
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        stage("parent_embedding");
        var budget = await IngestionAi.DocumentEmbeddingBudgetAsync(token).ConfigureAwait(false);
        var packed = state.HierarchyChildren!.SelectMany(c => TextWindows.Split(c.Text, options.MaxChunkCharacters, budget)
            .Select(part => c with { Text = part })).ToList();
        // Both complete and partial descriptions depend on parent text, not embedding-model-specific slices.
        var checkpoints = new List<ParentEmbeddingProgress>();
        foreach (var parent in state.Parents ?? [])
        {
            var hash = ProcessingPlan.Hash(parent.Text);
            var previous = state.ParentEmbeddings.FirstOrDefault(p => p.InputHash == hash && p.RecipeId == plan.ParentDescription.Id &&
                (p.Completed || p.ParentId == parent.Id));
            checkpoints.Add(previous is null ? new(parent.Id) { InputHash = hash, RecipeId = plan.ParentDescription.Id }
                : previous with { ParentId = parent.Id });
        }
        state = state with { Chunks = packed, ParentEmbeddings = options.ParentEmbedding == ParentEmbeddingMode.None ? state.ParentEmbeddings : checkpoints,
            ParentEmbeddingsCompleted = false };
        state = await PrepareParentEmbeddingsAsync(state, pipeline, options, progress, token).ConfigureAwait(false);
        state = WithoutNotices(state, "parent_embedding_");
        if (options.ParentEmbedding != ParentEmbeddingMode.None && (state.Parents ?? []).Any(parent => state.ParentEmbeddings.Any(p => p.ParentId == parent.Id && p.Skipped)))
            state = state with { ProcessingNotices = state.ProcessingNotices.Append("parent_embedding_skipped").ToList() };
        return Stamp(state, "parent-description", plan.ParentDescription, new { state.Parents, budget }, state.ParentEmbeddings);
    }

    private async Task<TextDocumentState> PrepareWordAsync(TextDocumentState state, TextAiPipeline pipeline,
        TextRagSettings options, ProcessingPlan plan, WordDocument word, CancellationToken token)
    {
        var input = word.Blocks;
        if (state.WordPreparedText is not null && Reusable(state, "word-repair", plan.WordRepair, input, state.WordRepair)) return state;
        if (!Reusable(state, "word-repair", plan.WordRepair, input, state.WordRepair)) state = state with { WordRepair = null };
        state = WithoutNotices(state, options.SourceMode == SourceCodeMode.Pdf ? "pdf_repair_" : "word_repair_");
        var result = await WordStructureRepair.RunAsync(pipeline, options, input, state.WordRepair, async next =>
        {
            state = Stamp(state with { WordRepair = next, WordPreparedText = null }, "word-repair", plan.WordRepair, input, next);
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        var prepared = string.Join("\n\n", result.Blocks);
        if (options.SourceMode == SourceCodeMode.Pdf) result = result with { Notices = result.Notices.Select(n => n.Replace("word_", "pdf_")).Distinct().ToList() };
        if (prepared.Length > options.MaxDocumentCharacters) throw new PipelineValidationException("document_size", "補正後の文書上限を超えています。");
        state = Stamp(state with { WordPreparedText = prepared, WordRepair = result,
            ProcessingNotices = state.ProcessingNotices.Concat(result.Notices).Distinct().ToList() }, "word-repair", plan.WordRepair, input, result);
        await repository.SaveAsync(state, token).ConfigureAwait(false);
        return state;
    }

    private async Task<TextDocumentState> PrepareCodeStagesAsync(TextDocumentState state, TextAiPipeline pipeline,
        TextRagSettings options, ProcessingPlan plan, IProgress<string>? progress, CancellationToken token)
    {
        if (state.CodeUnits is null || !Reusable(state, "parse", plan.Structure, state.Original, Syntax(state.CodeUnits)))
        {
            state = state with { CodeUnits = await SourceCodeParser.ParseAsync(state.Original, state.Path,
                Path.Combine(dataDirectory, "tools"), token, options.SourceMode).ConfigureAwait(false) };
            state = Stamp(state, "parse", plan.Structure, state.Original, Syntax(state.CodeUnits));
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        var syntax = Syntax(state.CodeUnits);
        // Keep in-progress descriptions when their generation recipe is unchanged.
        if (!Reusable(state, "code-description", plan.CodeDescription, syntax, state.CodeUnits))
        {
            state = WithoutNotices(state, "code_");
            state = state with { CodeUnits = state.CodeUnits!.Select(u => u with { Descriptions = [], DescribedCharacters = 0,
                DescriptionCompleted = false, DescriptionSkipped = false, DescriptionFallbackUsed = false }).ToList() };
            state = Stamp(state, "code-description", plan.CodeDescription, syntax, state.CodeUnits);
            await repository.SaveAsync(state, token).ConfigureAwait(false);
        }
        state = await PrepareCodeAsync(state with { Chunks = null }, pipeline, options, progress, token).ConfigureAwait(false);
        return Stamp(state, "code-description", plan.CodeDescription, syntax, state.CodeUnits);
    }
}
