using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagService
{
    private sealed class SearchNode(string id, TextDocumentState document, TextParentChunk? parent, TextChunk? child, SourceCodeUnit? code)
    {
        public string Id { get; } = id;
        public TextDocumentState Document { get; } = document;
        public TextParentChunk? Parent { get; } = parent;
        public TextChunk? Child { get; } = child;
        public SourceCodeUnit? Code { get; } = code;
        public string? ParentId { get; set; }
        public List<int> Ordinals { get; } = [];
        public StoredTextChunk? BestVector { get; set; }
        public double? Similarity { get; set; }
    }

    private async Task<IReadOnlyList<TextSearchResult>> SearchStructureAsync(string space, float[] query, int limit, CancellationToken token)
    {
        const int seedLimit = 64, nodeLimit = 1024, vectorLimit = 4096, depthLimit = 2;
        var options = settings.Management?.Retrieval ?? new RetrievalPipelineSettings();
        var revisions = await CompatibleRevisionsAsync(token).ConfigureAwait(false);
        var seeds = await repository.SearchVectorSeedsAsync(space, _configuration, query, seedLimit, token, revisions).ConfigureAwait(false);
        var nodes = new Dictionary<string, SearchNode>(StringComparer.Ordinal);
        var owners = new Dictionary<string, SearchNode>(StringComparer.Ordinal);
        static string ParentId(TextDocumentState document, string id) => document.RevisionId + ":parent:" + id;
        static string VectorKey(string revision, int ordinal) => revision + "_" + ordinal;
        foreach (var key in seeds.Select(s => s.Chunk.DocumentKey).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var document = await repository.GetAsync(key, token).ConfigureAwait(false);
            if (document?.Chunks is null || !await CurrentAsync(document, token).ConfigureAwait(false)) continue;
            var codeUnits = (document.CodeUnits ?? []).ToDictionary(u => u.Id, StringComparer.Ordinal);
            foreach (var parent in document.Parents ?? [])
            {
                var code = codeUnits.GetValueOrDefault(parent.Id);
                var node = new SearchNode(ParentId(document, parent.Id), document, parent, null, code)
                { ParentId = code?.ContainerId is null ? null : ParentId(document, code.ContainerId) };
                nodes.Add(node.Id, node);
            }
            for (var ordinal = 0; ordinal < document.Chunks.Count; ordinal++)
            {
                var chunk = document.Chunks[ordinal];
                var parentId = chunk.ParentId is null ? null : ParentId(document, chunk.ParentId);
                SearchNode? parent = parentId is not null ? nodes.GetValueOrDefault(parentId) : null;
                if (chunk.ParentId is not null && parent is null) continue;
                SearchNode node;
                if (parent?.Code is not null || chunk.EmbeddingKind is "parent_text" or "parent_summary")
                {
                    if (parent is null) continue;
                    node = parent;
                }
                else
                {
                    node = new SearchNode(document.RevisionId + ":child:" + ordinal, document, null, chunk, null) { ParentId = parentId };
                    nodes.Add(node.Id, node);
                }
                node.Ordinals.Add(ordinal);
                owners.Add(VectorKey(document.RevisionId, ordinal), node);
            }
        }
        var children = nodes.Values.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var selected = new Dictionary<string, SearchNode>(StringComparer.Ordinal);
        var queue = new Queue<(SearchNode Node, int Depth)>();
        var limited = false;
        void Include(SearchNode node, int depth)
        {
            if (selected.ContainsKey(node.Id)) return;
            if (selected.Count == nodeLimit) { limited = true; return; }
            selected.Add(node.Id, node); queue.Enqueue((node, depth));
        }
        foreach (var seed in seeds)
            if (owners.TryGetValue(VectorKey(seed.Chunk.RevisionId, seed.Chunk.Ordinal), out var node)) Include(node, 0);
        while (queue.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Depth == depthLimit) continue;
            if (entry.Node.ParentId is not null && nodes.TryGetValue(entry.Node.ParentId, out var parent)) Include(parent, entry.Depth + 1);
            foreach (var child in children.GetValueOrDefault(entry.Node.Id) ?? []) Include(child, entry.Depth + 1);
        }
        if (selected.Values.Any(n => n.ParentId is not null && !selected.ContainsKey(n.ParentId) ||
            (children.GetValueOrDefault(n.Id) ?? []).Any(c => !selected.ContainsKey(c.Id)))) limited = true;
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        void Accept(StoredSearchVector vector)
        {
            var chunk = vector.Chunk;
            var key = VectorKey(chunk.RevisionId, chunk.Ordinal);
            if (!owners.TryGetValue(key, out var node) || !selected.ContainsKey(node.Id) || chunk.DocumentKey != node.Document.Key) return;
            var expected = node.Document.Chunks![chunk.Ordinal];
            if (expected.ParentId != chunk.ParentId || expected.Start != chunk.Start || expected.End != chunk.End ||
                expected.EmbeddingKind != chunk.EmbeddingKind || expected.Text != chunk.Text) return;
            loaded.Add(key);
            var similarity = StructuralRanking.Cosine(query, vector.Embedding);
            if (!similarity.HasValue) { limited = true; return; }
            if (node.Similarity is null || similarity > node.Similarity || similarity == node.Similarity && chunk.Ordinal < node.BestVector!.Ordinal)
            { node.Similarity = similarity; node.BestVector = chunk; }
        }
        foreach (var seed in seeds) Accept(seed);
        var requests = new List<string>();
        // Round-robin allocation prevents a long multi-vector unit from exhausting the expansion budget.
        var activeNodes = selected.Values.Where(n => n.Ordinals.Count > 0).ToArray();
        var rounds = activeNodes.Length == 0 ? 0 : activeNodes.Max(n => n.Ordinals.Count);
        for (var i = 0; i < rounds; i++)
        {
            foreach (var node in activeNodes)
            {
                if (i >= node.Ordinals.Count) continue;
                var key = VectorKey(node.Document.RevisionId, node.Ordinals[i]);
                if (loaded.Contains(key)) continue;
                if (requests.Count + loaded.Count == vectorLimit) { limited = true; break; }
                requests.Add(key);
            }
            if (limited && requests.Count + loaded.Count == vectorLimit) break;
        }
        foreach (var vector in await repository.RelatedVectorsAsync(space, _configuration, requests, token, revisions).ConfigureAwait(false)) Accept(vector);
        if (requests.Any(key => !loaded.Contains(key))) limited = true;
        var scores = StructuralRanking.Calculate(selected.Values.Select(n => new StructuralNodeScore(n.Id, n.ParentId, n.Similarity)).ToArray(), options, limited);
        var candidates = new Dictionary<string, SearchNode>(StringComparer.Ordinal);
        foreach (var node in selected.Values.Where(n => scores.ContainsKey(n.Id)))
        {
            var target = node;
            SearchNode? preferred = null;
            if (node.Child is not null && node.ParentId is not null) preferred = selected.GetValueOrDefault(node.ParentId);
            else if (node.Code?.Kind == "function")
            {
                var id = node.ParentId;
                while (id is not null && selected.TryGetValue(id, out var ancestor))
                {
                    if (ancestor.Code?.Kind == "class") { preferred = ancestor; break; }
                    id = ancestor.ParentId;
                }
                if (preferred?.Code is not null && (preferred.Code.Code.Length > options.CodeResultMaxCharacters ||
                    CodeHierarchy.Full(preferred.Code).Length > options.CodeResultMaxCharacters)) preferred = null;
            }
            // Stored embeddings are F32; do not let rounding decide the exact preference boundary.
            if (preferred is not null && scores.TryGetValue(preferred.Id, out var parentScore) && parentScore.Combined + 1e-6 >= scores[node.Id].Combined - StructuralRanking.ParentPreferenceMargin)
                target = preferred;
            candidates[target.Id] = target;
        }
        var results = new List<TextSearchResult>();
        var closed = new HashSet<Guid>();
        foreach (var node in candidates.Values.OrderByDescending(n => scores[n.Id].Combined)
                     .ThenBy(n => n.Document.Path, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var document = node.Document;
            if (closed.Contains(document.DocumentId) || !await CurrentAsync(document, token).ConfigureAwait(false)) continue;
            var evidence = node.BestVector ?? (children.GetValueOrDefault(node.Id) ?? []).Where(n => n.BestVector is not null)
                .OrderByDescending(n => n.Similarity).ThenBy(n => n.Id, StringComparer.Ordinal).Select(n => n.BestVector).FirstOrDefault();
            if (evidence is null) continue;
            var codeSelection = node.Code is null ? null : CodeHierarchy.Select(node.Code, document.CodeUnits!, options.CodeResultMaxCharacters, preferClass: false);
            var content = codeSelection?.Content ?? node.Parent?.Text ?? node.Child!.Text;
            var disclosure = await DiscloseAsync(document, node.Parent?.Title is null ? content : $"Section: {node.Parent.Title}\n\n{content}", token).ConfigureAwait(false);
            if (!await CurrentAsync(document, token).ConfigureAwait(false)) continue;
            var allowed = disclosure.Decision is DisclosureDecision.Allowed or DisclosureDecision.Unchecked;
            if (!allowed)
            {
                closed.Add(document.DocumentId);
                results.Add(new(document.DocumentId, disclosure.Decision, null, document.Path, null, null));
            }
            else
            {
                var matchedCode = document.CodeUnits?.SingleOrDefault(u => u.Id == evidence.ParentId);
                var parent = node.Parent;
                results.Add(new(document.DocumentId, disclosure.Decision, content, document.Path,
                    node.Code is null ? parent?.Start ?? node.Child!.Start : null, node.Code is null ? parent?.End ?? node.Child!.End : null,
                    parent is null ? null : document.RevisionId + ":" + parent.Id, parent?.Title,
                    node.Code?.Kind, node.Code?.StartLine, node.Code?.EndLine,
                    node.Code?.ContainerId is null ? null : document.RevisionId + ":" + node.Code.ContainerId,
                    codeSelection?.Mode ?? (node.Child is null ? "full" : "child"),
                    matchedCode is null ? null : document.RevisionId + ":" + matchedCode.Id, matchedCode?.Kind, evidence.EmbeddingKind,
                    scores[node.Id], node.Id, node.Code?.Kind ?? (node.Child is null ? "parent" : "child")));
            }
            if (results.Count == limit) break;
        }
        return results;
    }
}
