using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>A single writer/AI lane. Settings are immutable for this service's lifetime.</summary>
public sealed partial class TextRagService(TextRagRepository repository, ITextRagAi ai, AppSettings settings, string dataDirectory,
    TimeProvider? timeProvider = null) : ITextRetrieval, IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly string _configuration = settings.PipelineConfigurationId();
    private readonly TextRagSettings _retrievalPrompts = settings.RetrievalPrompts();
    private readonly DisclosureGate _gate = new(new TextConfidentialityClassifier(ai, settings.RetrievalPrompts()));

    private async Task<TextCycleResult> ProcessFileCoreAsync(FileWorkItem file, IProgress<string>? progress, CancellationToken token, bool laneHeld = false)
    {
        if (!laneHeld) await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            if (!FileDiscoveryPolicy.StillEligible(file, settings.UpdateExclusionMinutes)) return new(0, 0, 0, 0, 0, 0, 1);
            var path = file.Path;
            int indexed = 0, failed = 0;
            var pipelineSettings = settings.PipelineFor(path);
            var pipeline = new TextAiPipeline(ai, pipelineSettings);
            token.ThrowIfCancellationRequested();

            var key = TextRagSettings.Hash(Path.GetFullPath(path).ToUpperInvariant());
            var previous = await repository.GetAsync(key, token).ConfigureAwait(false);
            TextDocumentState? state = previous;
            var stage = "read";
            try
            {
                if (new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns).IsIgnored(path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries))
                    return await WithdrawAsync(previous, token).ConfigureAwait(false);
                var snapshot = await TextFiles.ReadAsync(path, settings.TextPipeline.MaxDocumentCharacters, token, pipelineSettings.EncodingMode, allowEmpty: true).ConfigureAwait(false);
                if (!FileDiscoveryPolicy.StillEligible(file, settings.UpdateExclusionMinutes)) return new(0, 0, 0, 0, 0, 0, 1);
                if (previous is not null && previous.SourceHash == snapshot.Hash && previous.ConfigurationId == _configuration && previous.Status is "ready" or "excluded" or "skipped")
                { return new(0, 0, 1, 0, 0, 0); }
                stage = "prepare";
                if (previous is not null) await repository.DeactivateAsync(previous, token).ConfigureAwait(false);
                state = previous is not null && previous.SourceHash == snapshot.Hash && previous.ConfigurationId == _configuration && previous.Status != "deleted"
                    ? previous with { Status = "processing", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null, WithdrawalReason = null }
                    : new TextDocumentState
                    {
                        Key = key, DocumentId = previous?.DocumentId ?? Guid.NewGuid(), Path = path, SourceHash = snapshot.Hash,
                        ConfigurationId = _configuration, RevisionId = TextRagSettings.Hash(key + snapshot.Hash + _configuration), Original = snapshot.Text,
                        ProcessingNotices = [$"encoding_{(snapshot.EncodingAutomatic ? "auto" : "manual")}_{snapshot.EncodingMode}"]
                    };
                await repository.SaveAsync(state, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshot.Text))
                {
                    await repository.SaveAsync(state with { Status = "skipped", ProcessingNotices =
                        state.ProcessingNotices.Append("empty_text").Distinct().ToList() }, token).ConfigureAwait(false);
                    return new(0, 1, 0, 0, 0, 0);
                }
                progress?.Report($"処理中：{System.IO.Path.GetFileName(path)}（実行中のファイルは優先度変更でも中断しません）");
                if (state.CategoryId is null)
                {
                    stage = "classify";
                    var categoryId = await pipeline.ClassifyAsync(state.Original, token).ConfigureAwait(false);
                    state = state with { CategoryId = categoryId,
                        ProcessingNotices = state.ProcessingNotices.Concat(pipeline.Notices).Distinct().ToList() };
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                }
                var category = pipelineSettings.Categories.Single(c => c.Id == state.CategoryId);
                if (category.Excluded)
                {
                    await repository.SaveAsync(state with { Status = "excluded" }, token).ConfigureAwait(false);
                    return new(0, 1, 0, 0, 0, 0);
                }
                var isCode = SourceCodeParser.Applies(pipelineSettings, path);
                if (isCode)
                {
                    stage = "code";
                    state = await PrepareCodeAsync(state, pipeline, pipelineSettings, progress, token).ConfigureAwait(false);
                }
                if (!isCode && state.Paragraphs is null)
                {
                    stage = "format";
                    var structure = TextStructure.Build(state.Original, state.Path, pipelineSettings);
                    var paragraphs = structure.Paragraphs;
                    var formatted = await pipeline.FormatAsync(paragraphs, category, token).ConfigureAwait(false);
                    state = state with { Paragraphs = formatted, ProcessingNotices = state.ProcessingNotices.Concat(pipeline.Notices).Distinct().ToList() };
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                }
                if (state.Chunks is null)
                {
                    stage = "chunk";
                    var hierarchy = await TextChunking.BuildAsync(ai, pipeline, pipelineSettings, category, state.Path,
                        TextStructure.Build(state.Original, state.Path, pipelineSettings),
                        state.Paragraphs ?? throw new InvalidDataException("段落がありません。"), token).ConfigureAwait(false);
                    state = state with { Chunks = hierarchy.Children, Parents = hierarchy.Parents,
                        ProcessingNotices = state.ProcessingNotices.Concat(pipeline.Notices).Distinct().ToList() };
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                }
                for (var ordinal = state.EmbeddedChunks; ordinal < state.Chunks.Count; ordinal++)
                {
                    stage = "embedding";
                    var batch = await ai.EmbedAsync([EmbeddingInput.Document(settings.Ai.EmbeddingModel, state.Chunks[ordinal].Text)], token).ConfigureAwait(false);
                    ValidateVectors(batch, 1);
                    if (state.SpaceId is null)
                    {
                        var space = _retrievalPrompts.SpaceId(settings.Ai, batch.Dimension);
                        await repository.EnsureSpaceAsync(space, batch.Dimension, token).ConfigureAwait(false);
                        state = state with { SpaceId = space, Dimension = batch.Dimension };
                    }
                    if (batch.Dimension != state.Dimension) throw new InvalidDataException("処理中にEmbedding次元数が変化しました。");
                    await repository.StageChunkAsync(state, ordinal, batch.Vectors[0], token).ConfigureAwait(false);
                    state = state with { EmbeddedChunks = ordinal + 1 };
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                }
                stage = "publish";
                var current = await TextFiles.ReadAsync(path, settings.TextPipeline.MaxDocumentCharacters, token, pipelineSettings.EncodingMode).ConfigureAwait(false);
                if (current.Hash != state.SourceHash) throw new IOException("処理中に元ファイルが変更されました。");
                if (new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns).IsIgnored(path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries))
                    return await WithdrawAsync(state, token).ConfigureAwait(false);
                await repository.PublishAsync(state, token).ConfigureAwait(false);
                indexed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                if (state is not null)
                {
                    // Nested stages checkpoint after each bounded fragment. Do not overwrite that progress
                    // with the caller's older snapshot when a later fragment fails.
                    var checkpoint = await repository.GetAsync(state.Key, token).ConfigureAwait(false);
                    if (checkpoint?.RevisionId == state.RevisionId) state = checkpoint;
                    await repository.DeactivateAsync(state, token).ConfigureAwait(false);
                    await repository.SaveAsync(state with { Status = "failed", ErrorCode = ex.GetType().Name,
                        ErrorStage = stage, ErrorDetailCode = PipelineErrors.DetailCode(ex) }, token).ConfigureAwait(false);
                }
                // Persist first-read failures too, without manufacturing content or a source hash.
                else await repository.SaveAsync(new TextDocumentState
                {
                    Key = key, DocumentId = Guid.NewGuid(), Path = path, RevisionId = TextRagSettings.Hash(key + _configuration + "unreadable"),
                    ConfigurationId = _configuration, Status = "failed", ErrorCode = ex.GetType().Name,
                    ErrorStage = stage, ErrorDetailCode = PipelineErrors.DetailCode(ex)
                }, token).ConfigureAwait(false);
            }

            return new(indexed, 0, 0, failed, 0, 0);
        }
        finally { if (!laneHeld) _operations.Release(); }
    }
    public async Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000 || limit is < 1 or > 20) throw new ArgumentException("検索語は1～1000文字、件数は1～20で指定してください。");
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            var input = EmbeddingInput.Query(settings.Ai.EmbeddingModel, query);
            var batch = await ai.EmbedAsync([input], token).ConfigureAwait(false);
            ValidateVectors(batch, 1);
            var space = _retrievalPrompts.SpaceId(settings.Ai, batch.Dimension);
            await repository.EnsureSpaceAsync(space, batch.Dimension, token).ConfigureAwait(false);
            // Over-fetch children so multiple matching paragraphs do not fill all parent result slots.
            // A bounded candidate pool avoids unbounded query and disclosure work on large corpora.
            var chunks = await repository.SearchAsync(space, _configuration, batch.Vectors[0], 256, token).ConfigureAwait(false);
            var results = new List<TextSearchResult>();
            var seenParents = new HashSet<string>(StringComparer.Ordinal);
            var closedDocuments = new HashSet<Guid>();
            var documents = new Dictionary<string, TextDocumentState?>();
            foreach (var chunk in chunks)
            {
                if (!documents.TryGetValue(chunk.DocumentKey, out var document))
                {
                    document = await repository.GetAsync(chunk.DocumentKey, token).ConfigureAwait(false);
                    documents[chunk.DocumentKey] = document;
                }
                if (document is null || closedDocuments.Contains(document.DocumentId) || document.RevisionId != chunk.RevisionId) continue;
                TextParentChunk? parent = null;
                if (chunk.ParentId is not null)
                {
                    parent = document.Parents?.SingleOrDefault(p => p.Id == chunk.ParentId && p.Start <= chunk.Start && p.End >= chunk.End);
                    if (parent is null || !seenParents.Add(document.RevisionId + ":" + parent.Id)) continue;
                }
                if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var content = parent?.Text ?? chunk.Text;
                var disclosure = await DiscloseAsync(document, parent?.Title is null ? content : $"Section: {parent.Title}\n\n{content}", token).ConfigureAwait(false);
                if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
                if (!allowed) closedDocuments.Add(document.DocumentId);
                results.Add(new(document.DocumentId, disclosure.Decision, allowed ? content : null,
                    document.Path, allowed && parent?.SourceKind is null ? parent?.Start ?? chunk.Start : null,
                    allowed && parent?.SourceKind is null ? parent?.End ?? chunk.End : null,
                    allowed && parent is not null ? document.RevisionId + ":" + parent.Id : null, allowed ? parent?.Title : null,
                    allowed ? parent?.SourceKind : null, allowed ? parent?.StartLine : null, allowed ? parent?.EndLine : null,
                    allowed && parent?.ContainerId is not null ? document.RevisionId + ":" + parent.ContainerId : null));
                if (results.Count == limit) break;
            }
            return results;
        }
        finally { _operations.Release(); }
    }

    public async Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var document = (await repository.DocumentsAsync(token).ConfigureAwait(false)).SingleOrDefault(d => d.DocumentId == documentId);
            if (document is null || !await CurrentAsync(document, token).ConfigureAwait(false)) return null;
            var disclosure = await DiscloseAsync(document, document.Original, token).ConfigureAwait(false);
            if (!await CurrentAsync(document, token).ConfigureAwait(false)) return null;
            var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
            return new(document.DocumentId, disclosure.Decision, allowed ? document.Original : null, document.Path, null, null);
        }
        finally { _operations.Release(); }
    }

    private Task<DisclosureResult> DiscloseAsync(TextDocumentState document, string candidate, CancellationToken token) =>
        _gate.EvaluateAsync(document.DocumentId, document.Original, $"Source: {document.Path}\n\n{candidate}", settings.DisclosurePolicy(), token);

    private async Task<bool> CurrentAsync(TextDocumentState document, CancellationToken token)
    {
        if (document.Status != "ready" || document.ConfigurationId != _configuration || TextFiles.Within(document.Path, dataDirectory) ||
            !settings.Folders.Any(f => TextFiles.InFolder(document.Path, f)) ||
            !settings.AllowedTextExtensions().Contains(Path.GetExtension(document.Path), StringComparer.OrdinalIgnoreCase) ||
            !settings.PipelineFor(document.Path).Categories.Any(c => c.Id == document.CategoryId && !c.Excluded)) return false;
        try
        {
            if (new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns).IsIgnored(document.Path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries)) return false;
            return (await TextFiles.ReadAsync(document.Path, settings.TextPipeline.MaxDocumentCharacters, token, settings.PipelineFor(document.Path).EncodingMode).ConfigureAwait(false)).Hash == document.SourceHash;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException or PipelineValidationException) { return false; }
    }

    private async Task<TextCycleResult> WithdrawAsync(TextDocumentState? state, CancellationToken token)
    {
        if (state is null) return new(0, 0, 0, 0, 0, 0);
        await repository.DeactivateAsync(state, token).ConfigureAwait(false);
        await repository.SaveAsync(state with { Status = "deleted", WithdrawalReason = "ignored", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null }, token).ConfigureAwait(false);
        return new(0, 0, 0, 0, 1, 0);
    }

    private void RequireModels()
    {
        if (string.IsNullOrWhiteSpace(settings.Ai.EmbeddingModel) || string.IsNullOrWhiteSpace(settings.Ai.ChatModel))
            throw new InvalidOperationException("チャット用とEmbedding用のモデルを設定してください。");
    }

    private static void ValidateVectors(VectorBatch batch, int count)
    {
        if (batch.Dimension is < 1 or > 8192 || batch.Vectors.Count != count ||
            batch.Vectors.Any(v => v.Length != batch.Dimension || v.Any(x => !float.IsFinite(x)) || v.All(x => x == 0)))
            throw new InvalidDataException("Embedding応答が不正です。");
    }

    public void Dispose() { _operations.Dispose(); _scans.Dispose(); _discoveries.Dispose(); }
}
