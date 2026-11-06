using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>A single writer/AI lane. Settings are immutable for this service's lifetime.</summary>
public sealed partial class TextRagService(TextRagRepository repository, ITextRagAi ai, AppSettings settings, string dataDirectory,
    TimeProvider? timeProvider = null) : ITextRetrieval, IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly string _configuration = settings.DataSourceName == repository.DataSourceName ? settings.PipelineConfigurationId()
        : throw new InvalidOperationException("設定とDB接続のデータソース名が一致しません。");
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

            var key = DocumentIdentity.Key(settings.DataSourceName, path);
            var previous = await repository.GetAsync(key, token).ConfigureAwait(false);
            TextDocumentState? state = previous;
            var stage = "read";
            try
            {
                if (new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns).IsIgnored(path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries))
                    return await WithdrawAsync(previous, token).ConfigureAwait(false);
                var snapshot = await TextFiles.ReadAsync(path, settings.TextPipeline.MaxDocumentCharacters, token, pipelineSettings.EncodingMode, allowEmpty: true).ConfigureAwait(false);
                if (!FileDiscoveryPolicy.StillEligible(file, settings.UpdateExclusionMinutes)) return new(0, 0, 0, 0, 0, 0, 1);
                var plan = ProcessingPlan.Create(settings, path, previous?.CategoryId);
                var sameSource = previous is not null && previous.SourceHash == snapshot.Hash && previous.Original == snapshot.Text;
                if (sameSource && previous!.Processing is null && previous.ConfigurationId == _configuration)
                {
                    previous = AdoptLegacy(previous, pipelineSettings, plan);
                    previous = Stamp(previous, "read", plan.Read, snapshot.Hash, new { snapshot.Text, snapshot.EncodingMode, snapshot.EncodingAutomatic });
                    await repository.BackfillVectorHashesAsync(previous, token).ConfigureAwait(false);
                    await repository.SaveAsync(previous, token).ConfigureAwait(false);
                }
                var compatible = sameSource && ProcessingPlan.Current(settings, previous!);
                if (compatible && previous!.Status is "ready" or "excluded" or "skipped") return new(0, 0, 1, 0, 0, 0);
                stage = "prepare";
                if (previous is not null) await repository.DeactivateAsync(previous, token).ConfigureAwait(false);
                var resume = compatible && previous!.Status != "deleted";
                state = sameSource && previous!.Processing is { Version: 1 }
                    ? previous with { Status = "processing", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null, WithdrawalReason = null }
                    : new TextDocumentState
                    {
                        Key = key, DocumentId = previous?.DocumentId ?? Guid.NewGuid(), Path = path, SourceHash = snapshot.Hash, DataSourceName = settings.DataSourceName,
                        ConfigurationId = _configuration, Original = snapshot.Text,
                        ProcessingNotices = [$"encoding_{(snapshot.EncodingAutomatic ? "auto" : "manual")}_{snapshot.EncodingMode}"]
                    };
                state = state with { ConfigurationId = _configuration,
                    RevisionId = resume ? state.RevisionId : Guid.NewGuid().ToString("N"), EmbeddedChunks = resume ? state.EmbeddedChunks : 0,
                    Processing = (state.Processing ?? new()) with { ProfileId = plan.Id } };
                // A vector space is reusable only under the identical embedding model/profile/revision.
                if (previous?.SpaceId is null || previous.SpaceId != _retrievalPrompts.SpaceId(settings.Ai, previous.Dimension))
                    state = state with { SpaceId = null, Dimension = 0, EmbeddedChunks = 0 };
                else state = state with { SpaceId = previous.SpaceId, Dimension = previous.Dimension };
                state = state with { ProcessingNotices = state.ProcessingNotices.Where(n => !n.StartsWith("encoding_", StringComparison.Ordinal))
                    .Append($"encoding_{(snapshot.EncodingAutomatic ? "auto" : "manual")}_{snapshot.EncodingMode}").ToList() };
                state = Stamp(state, "read", plan.Read, snapshot.Hash, new { snapshot.Text, snapshot.EncodingMode, snapshot.EncodingAutomatic });
                await repository.SaveAsync(state, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshot.Text))
                {
                    await repository.SaveAsync(state with { Status = "skipped", ProcessingNotices =
                        state.ProcessingNotices.Append("empty_text").Distinct().ToList() }, token).ConfigureAwait(false);
                    return new(0, 1, 0, 0, 0, 0);
                }
                progress?.Report($"処理中：{System.IO.Path.GetFileName(path)}（実行中のファイルは優先度変更でも中断しません）");
                if (!Reusable(state, "classify", plan.Classify, state.Original, state.CategoryId))
                {
                    stage = "classify";
                    state = WithoutNotices(state, "category_");
                    var categoryId = await pipeline.ClassifyAsync(state.Original, token).ConfigureAwait(false);
                    state = state with { CategoryId = categoryId,
                        ProcessingNotices = state.ProcessingNotices.Concat(pipeline.Notices).Distinct().ToList() };
                    state = Stamp(state, "classify", plan.Classify, state.Original, state.CategoryId);
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                }
                plan = ProcessingPlan.Create(settings, path, state.CategoryId);
                state = state with { Processing = state.Processing! with { ProfileId = plan.Id } };
                var category = pipelineSettings.Categories.Single(c => c.Id == state.CategoryId);
                if (category.Excluded)
                {
                    await repository.SaveAsync(state with { Status = "excluded" }, token).ConfigureAwait(false);
                    return new(0, 1, 0, 0, 0, 0);
                }
                var isCode = plan.IsCode;
                if (isCode)
                {
                    stage = "code";
                    state = await PrepareCodeStagesAsync(state, pipeline, pipelineSettings, plan, progress, token).ConfigureAwait(false);
                    if (state.Chunks is { Count: 0 })
                    {
                        await repository.SaveAsync(state with { Status = "skipped", ProcessingNotices =
                            state.ProcessingNotices.Append("code_no_descriptions").Distinct().ToList() }, token).ConfigureAwait(false);
                        return new(0, 1, 0, 0, 0, 0);
                    }
                }
                if (!isCode) state = await PrepareTextStagesAsync(state, pipeline, pipelineSettings, category, plan, progress, s => stage = s, token).ConfigureAwait(false);
                var preparedChunks = state.Chunks ?? throw new InvalidDataException("埋め込み用チャンクがありません。");
                if (!Reusable(state, "embedding", plan.Embedding, preparedChunks, preparedChunks)) state = state with { EmbeddedChunks = 0 };
                state = Stamp(state, "embedding", plan.Embedding, preparedChunks, preparedChunks);
                state = state with { ProcessingNotices = state.ProcessingNotices.Concat(pipeline.Notices).Distinct().ToList() };
                await repository.SaveAsync(state, token).ConfigureAwait(false);
                stage = "embedding";
                if (state.SpaceId is not null) await repository.EnsureSpaceAsync(state.SpaceId, state.Dimension, token).ConfigureAwait(false);
                for (var ordinal = state.EmbeddedChunks; ordinal < preparedChunks.Count; ordinal++)
                {
                    stage = "embedding";
                    var reused = state.SpaceId is null ? null : await repository.ReusableVectorAsync(state.SpaceId, state.Key, preparedChunks[ordinal].Text, token).ConfigureAwait(false);
                    var batch = reused is null ? await ai.EmbedAsync([EmbeddingInput.Document(settings.Ai.EmbeddingModel, preparedChunks[ordinal].Text)], token).ConfigureAwait(false)
                        : new VectorBatch(state.Dimension, [reused]);
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
                    Key = key, DocumentId = Guid.NewGuid(), Path = path, DataSourceName = settings.DataSourceName, RevisionId = TextRagSettings.Hash(key + _configuration + "unreadable"),
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
            if (settings.Management?.Retrieval.HierarchySearchEnabled ?? true)
                return await SearchStructureAsync(space, batch.Vectors[0], limit, token).ConfigureAwait(false);
            // Over-fetch children so multiple matching paragraphs do not fill all parent result slots.
            // A bounded candidate pool avoids unbounded query and disclosure work on large corpora.
            var chunks = await repository.SearchAsync(space, _configuration, batch.Vectors[0], 256, token,
                await CompatibleRevisionsAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
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
                    if (parent is null) continue;
                }
                var matchedParent = parent;
                CodeSelection? selection = null;
                if (parent?.SourceKind is not null && document.CodeUnits is { } units)
                {
                    var hit = units.Single(u => u.Id == parent.Id);
                    selection = CodeHierarchy.Select(hit, units, settings.Management?.Retrieval.CodeResultMaxCharacters ?? 8000);
                    parent = document.Parents!.Single(p => p.Id == selection.Unit.Id);
                }
                if (parent is not null && !seenParents.Add(document.RevisionId + ":" + parent.Id)) continue;
                if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var content = selection?.Content ?? parent?.Text ?? chunk.Text;
                var disclosure = await DiscloseAsync(document, parent?.Title is null ? content : $"Section: {parent.Title}\n\n{content}", token).ConfigureAwait(false);
                if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
                if (!allowed) closedDocuments.Add(document.DocumentId);
                results.Add(new(document.DocumentId, disclosure.Decision, allowed ? content : null,
                    document.Path, allowed && parent?.SourceKind is null ? parent?.Start ?? chunk.Start : null,
                    allowed && parent?.SourceKind is null ? parent?.End ?? chunk.End : null,
                    allowed && parent is not null ? document.RevisionId + ":" + parent.Id : null, allowed ? parent?.Title : null,
                    allowed ? parent?.SourceKind : null, allowed ? parent?.StartLine : null, allowed ? parent?.EndLine : null,
                    allowed && parent?.ContainerId is not null ? document.RevisionId + ":" + parent.ContainerId : null,
                    allowed ? selection?.Mode : null,
                    allowed && selection is not null ? document.RevisionId + ":" + matchedParent!.Id : null,
                    allowed && selection is not null ? matchedParent!.SourceKind : null,
                    allowed ? chunk.EmbeddingKind : null));
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
        if (document.Status != "ready" || TextFiles.Within(document.Path, dataDirectory) ||
            !settings.Folders.Any(f => TextFiles.InFolder(document.Path, f)) ||
            !settings.AllowedTextExtensions().Contains(Path.GetExtension(document.Path), StringComparer.OrdinalIgnoreCase) ||
            !settings.PipelineFor(document.Path).Categories.Any(c => c.Id == document.CategoryId && !c.Excluded) ||
            !ProcessingPlan.Current(settings, document)) return false;
        try
        {
            if (new IgnorePolicy(settings.Folders, settings.GlobalIgnorePatterns).IsIgnored(document.Path, directory: false, excludeDotHiddenEntries: settings.ExcludeDotHiddenEntries)) return false;
            return (await TextFiles.ReadAsync(document.Path, settings.TextPipeline.MaxDocumentCharacters, token, settings.PipelineFor(document.Path).EncodingMode).ConfigureAwait(false)).Hash == document.SourceHash;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException or PipelineValidationException) { return false; }
    }

    private async Task<string[]> CompatibleRevisionsAsync(CancellationToken token) =>
        (await repository.DocumentStatusesAsync(token).ConfigureAwait(false)).Where(d => d.Status == "ready" &&
            !TextFiles.Within(d.Path, dataDirectory) && settings.Folders.Any(f => TextFiles.InFolder(d.Path, f)) &&
            settings.AllowedTextExtensions().Contains(Path.GetExtension(d.Path), StringComparer.OrdinalIgnoreCase) &&
            ProcessingPlan.Current(settings, d)).Select(d => d.RevisionId).ToArray();

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
