using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record StructuralNodeScore(string Id, string? ParentId, double? Similarity);

public static class StructuralRanking
{
    public static double? Cosine(float[] query, float[] vector)
    {
        if (query.Length != vector.Length || query.Length == 0) return null;
        double dot = 0, left = 0, right = 0;
        for (var i = 0; i < query.Length; i++)
        {
            if (!float.IsFinite(query[i]) || !float.IsFinite(vector[i])) return null;
            dot += (double)query[i] * vector[i]; left += (double)query[i] * query[i]; right += (double)vector[i] * vector[i];
        }
        return left == 0 || right == 0 ? null : Math.Clamp(dot / Math.Sqrt(left * right), -1, 1);
    }

    public static IReadOnlyDictionary<string, StructuralSearchScore> Calculate(IReadOnlyList<StructuralNodeScore> nodes,
        RetrievalPipelineSettings options, bool limited)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var childScores = nodes.Where(n => n.ParentId is not null && n.Similarity.HasValue)
            .GroupBy(n => n.ParentId!, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.Select(n => n.Similarity!.Value).OrderDescending().Take(3).Average(), StringComparer.Ordinal);
        var result = new Dictionary<string, StructuralSearchScore>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var parent = node.ParentId is not null && byId.TryGetValue(node.ParentId, out var p) ? p.Similarity : null;
            double? children = childScores.TryGetValue(node.Id, out var childrenScore) ? childrenScore : null;
            double total = 0, weights = 0;
            void Add(double? score, double weight) { if (score.HasValue && weight > 0) { total += score.Value * weight; weights += weight; } }
            Add(node.Similarity, options.HierarchySelfWeight); Add(parent, options.HierarchyParentWeight); Add(children, options.HierarchyChildWeight);
            // A node with no own or child evidence must not become a result just because its parent matched.
            if (weights > 0 && (node.Similarity.HasValue && options.HierarchySelfWeight > 0 || children.HasValue && options.HierarchyChildWeight > 0))
                result[node.Id] = new(total / weights, node.Similarity, parent, children, limited);
        }
        return result;
    }
}
