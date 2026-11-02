using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>A single writer/AI lane. Settings are immutable for this service's lifetime.</summary>
public sealed class TextRagService(TextRagRepository repository, ITextRagAi ai, AppSettings settings, string dataDirectory) : ITextRetrieval, IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly string _configuration = settings.PipelineConfigurationId();
    private readonly TextRagSettings _retrievalPrompts = settings.RetrievalPrompts();
    private readonly DisclosureGate _gate = new(new TextConfidentialityClassifier(ai, settings.RetrievalPrompts()));

    public async Task<TextCycleResult> ScanAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            var scan = await Task.Run(() => TextFiles.Scan(settings.Folders, dataDirectory, token, settings.AllowedTextExtensions()), token).ConfigureAwait(false);
            var documents = (await repository.DocumentsAsync(token).ConfigureAwait(false)).ToDictionary(x => x.Key);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int indexed = 0, excluded = 0, unchanged = 0, failed = 0, deleted = 0;
            foreach (var path in scan.Files)
            {
                var pipelineSettings = settings.PipelineFor(path);
                var pipeline = new TextAiPipeline(ai, pipelineSettings);
                token.ThrowIfCancellationRequested();
                seen.Add(path);
                var key = TextRagSettings.Hash(Path.GetFullPath(path).ToUpperInvariant());
                documents.TryGetValue(key, out var previous);
                TextDocumentState? state = previous;
                var stage = "read";
                try
                {
                    var snapshot = await TextFiles.ReadAsync(path, settings.TextPipeline.MaxDocumentCharacters, token).ConfigureAwait(false);
                    if (previous is not null && previous.SourceHash == snapshot.Hash && previous.ConfigurationId == _configuration && previous.Status is "ready" or "excluded")
                    { unchanged++; continue; }
                    stage = "prepare";
                    if (previous is not null) await repository.DeactivateAsync(previous, token).ConfigureAwait(false);
                    state = previous is not null && previous.SourceHash == snapshot.Hash && previous.ConfigurationId == _configuration && previous.Status != "deleted"
                        ? previous with { Status = "processing", ErrorCode = null, ErrorStage = null, ErrorDetailCode = null }
                        : new TextDocumentState
                        {
                            Key = key, DocumentId = previous?.DocumentId ?? Guid.NewGuid(), Path = path, SourceHash = snapshot.Hash,
                            ConfigurationId = _configuration, RevisionId = TextRagSettings.Hash(key + snapshot.Hash + _configuration), Original = snapshot.Text
                        };
                    await repository.SaveAsync(state, token).ConfigureAwait(false);
                    progress?.Report($"テキスト処理中（今回の対象 {indexed + excluded + unchanged + failed + 1}/{scan.Files.Count}）");
                    if (state.CategoryId is null)
                    {
                        stage = "classify";
                        state = state with { CategoryId = await pipeline.ClassifyAsync(state.Original, token).ConfigureAwait(false) };
                        await repository.SaveAsync(state, token).ConfigureAwait(false);
                    }
                    var category = pipelineSettings.Categories.Single(c => c.Id == state.CategoryId);
                    if (category.Excluded)
                    {
                        await repository.SaveAsync(state with { Status = "excluded" }, token).ConfigureAwait(false);
                        excluded++; continue;
                    }
                    if (state.Paragraphs is null)
                    {
                        stage = "format";
                        var paragraphs = TextFiles.Paragraphs(state.Original, settings.TextPipeline.MaxChunkCharacters);
                        state = state with { Paragraphs = await pipeline.FormatAsync(paragraphs, category, token).ConfigureAwait(false) };
                        await repository.SaveAsync(state, token).ConfigureAwait(false);
                    }
                    if (state.Chunks is null)
                    {
                        stage = "chunk";
                        state = state with { Chunks = await pipeline.ChunkAsync(state.Paragraphs, category, token).ConfigureAwait(false) };
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
                    var current = await TextFiles.ReadAsync(path, settings.TextPipeline.MaxDocumentCharacters, token).ConfigureAwait(false);
                    if (current.Hash != state.SourceHash) throw new IOException("処理中に元ファイルが変更されました。");
                    await repository.PublishAsync(state, token).ConfigureAwait(false);
                    indexed++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    if (state is not null)
                    {
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
            }
            foreach (var old in documents.Values.Where(d => d.Status != "deleted" && !seen.Contains(d.Path)))
            {
                var stillConfigured = settings.Folders.Any(f => TextFiles.InFolder(old.Path, f));
                if (stillConfigured && !scan.CompletedRoots.Any(f => TextFiles.InFolder(old.Path, f))) continue;
                await repository.DeactivateAsync(old, token).ConfigureAwait(false);
                await repository.SaveAsync(old with { Status = "deleted" }, token).ConfigureAwait(false);
                deleted++;
            }
            return new(indexed, excluded, unchanged, failed, deleted, scan.FailedRoots);
        }
        finally { _operations.Release(); }
    }

    public async Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000 || limit is < 1 or > 20) throw new ArgumentException("検索語は1～1000文字、件数は1～20で指定してください。");
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireModels();
            var input = EmbeddingInput.Query(settings.Ai.EmbeddingModel, _retrievalPrompts.QueryInstruction, query);
            var batch = await ai.EmbedAsync([input], token).ConfigureAwait(false);
            ValidateVectors(batch, 1);
            var space = _retrievalPrompts.SpaceId(settings.Ai, batch.Dimension);
            await repository.EnsureSpaceAsync(space, batch.Dimension, token).ConfigureAwait(false);
            var chunks = await repository.SearchAsync(space, _configuration, batch.Vectors[0], limit, token).ConfigureAwait(false);
            var results = new List<TextSearchResult>();
            foreach (var chunk in chunks)
            {
                var document = await repository.GetAsync(chunk.DocumentKey, token).ConfigureAwait(false);
                if (document is null || document.RevisionId != chunk.RevisionId || !await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var disclosure = await DiscloseAsync(document, chunk.Text, token).ConfigureAwait(false);
                if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
                var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
                results.Add(new(document.DocumentId, disclosure.Decision, allowed ? chunk.Text : null,
                    allowed ? document.Path : null, allowed ? chunk.Start : null, allowed ? chunk.End : null));
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
            return new(document.DocumentId, disclosure.Decision, allowed ? document.Original : null, allowed ? document.Path : null, null, null);
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
        try { return (await TextFiles.ReadAsync(document.Path, settings.TextPipeline.MaxDocumentCharacters, token).ConfigureAwait(false)).Hash == document.SourceHash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { return false; }
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

    public void Dispose() => _operations.Dispose();
}
