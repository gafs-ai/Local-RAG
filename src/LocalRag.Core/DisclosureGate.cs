using System.Text.RegularExpressions;

namespace LocalRag.Core;

public enum DisclosureDecision { Allowed, Withheld, Pending, Unchecked }

public sealed record ConfidentialityPolicy
{
    public bool Enabled { get; init; } = true;
    public bool PatternsEnabled { get; init; } = true;
    // Experimental semantic inspection is opt-in; pattern inspection remains enabled by default.
    public bool LlmEnabled { get; init; } = false;
    public IReadOnlyList<string> Patterns { get; init; } = ["社外秘", "極秘", "CONFIDENTIAL"];
    // Existing serialized policies retain whole-document matching. New installations use CreateDefault.
    public bool BoundaryPatternsEnabled { get; init; } = false;
    public int LeadingLines { get; init; } = 5;
    public int TrailingLines { get; init; } = 0;
    public IReadOnlyList<string> BoundaryPatterns { get; init; } = ["極秘", "丸秘", "マル秘", "TOP SECRET", "SECRET"];

    public static ConfidentialityPolicy CreateDefault() => new()
    { PatternsEnabled = false, Patterns = [], BoundaryPatternsEnabled = true };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (LeadingLines < 0 || TrailingLines < 0) errors.Add("秘匿検査の先頭・末尾行数は0以上の整数で指定してください。");
        foreach (var (label, patterns) in new[] { ("全文", Patterns), ("先頭・末尾", BoundaryPatterns) })
        {
            if (patterns is null || patterns.Count > 100 || patterns.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 2000))
            { errors.Add($"{label}の秘匿パターンは空行を除き100件以内、各2000文字以内で指定してください。"); continue; }
            foreach (var pattern in patterns)
                try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { errors.Add($"{label}の秘匿パターンに正しくない正規表現があります。"); break; }
        }
        return errors;
    }
}

public interface IConfidentialityClassifier
{
    Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken);
}

// Denied content contains no title, snippet, score, or classifier reasoning.
// Retrieval separately supplies the original-file path for the user to open manually.
public sealed record DisclosureResult(Guid DocumentId, bool Exists, DisclosureDecision Decision, string? Content);

public sealed class DisclosureGate(IConfidentialityClassifier classifier)
{
    public async Task<DisclosureResult> EvaluateAsync(
        Guid documentId, string original, string candidate, ConfidentialityPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!policy.Enabled) return new(documentId, true, DisclosureDecision.Unchecked, candidate);

        try
        {
            if (policy.Validate().Count != 0) return new(documentId, true, DisclosureDecision.Pending, null);
            var full = policy.PatternsEnabled && policy.Patterns.Count > 0;
            var boundary = policy.BoundaryPatternsEnabled && policy.BoundaryPatterns.Count > 0 && (policy.LeadingLines > 0 || policy.TrailingLines > 0);
            if (!full && !boundary && !policy.LlmEnabled) return new(documentId, true, DisclosureDecision.Pending, null);
            if (boundary)
            {
                var regions = BoundaryRegions(original, policy.LeadingLines, policy.TrailingLines);
                foreach (var pattern in policy.BoundaryPatterns)
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    if (regions.Any(regex.IsMatch)) return new(documentId, true, DisclosureDecision.Withheld, null);
                }
            }
            if (full)
            {
                if (policy.Patterns is null) return new(documentId, true, DisclosureDecision.Pending, null);
                foreach (var pattern in policy.Patterns)
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                    if (regex.IsMatch(original) || regex.IsMatch(candidate))
                        return new(documentId, true, DisclosureDecision.Withheld, null);
                }
            }

            if (policy.LlmEnabled)
            {
                var decision = await classifier.InspectAsync(original, candidate, cancellationToken).ConfigureAwait(false);
                if (decision != DisclosureDecision.Allowed)
                    return new(documentId, true,
                        decision == DisclosureDecision.Withheld ? decision : DisclosureDecision.Pending, null);
            }
            return new(documentId, true, DisclosureDecision.Allowed, candidate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Provider failures and invalid rules must never turn into permission to disclose.
            return new(documentId, true, DisclosureDecision.Pending, null);
        }
    }

    // Physical source lines, including blank lines; a final line terminator is not an extra line.
    // Keep original line endings and never join disjoint header/footer regions for regex matching.
    internal static IReadOnlyList<string> BoundaryRegions(string original, int leading, int trailing)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] is not ('\r' or '\n')) continue;
            if (original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n') i++;
            if (i + 1 < original.Length) starts.Add(i + 1);
        }
        var headEnd = leading == 0 ? 0 : leading >= starts.Count ? original.Length : starts[leading];
        var tailStart = trailing == 0 ? original.Length : trailing >= starts.Count ? 0 : starts[starts.Count - trailing];
        if (leading > 0 && trailing > 0 && headEnd >= tailStart) return [original];
        var regions = new List<string>();
        if (leading > 0) regions.Add(original[..headEnd]);
        if (trailing > 0) regions.Add(original[tailStart..]);
        return regions;
    }
}
