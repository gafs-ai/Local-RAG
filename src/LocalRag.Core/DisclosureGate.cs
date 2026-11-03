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
}

public interface IConfidentialityClassifier
{
    Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken);
}

// No path, title, snippet, score, or classifier reasoning can be included in a denied result.
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

        if (!policy.PatternsEnabled && !policy.LlmEnabled)
            return new(documentId, true, DisclosureDecision.Pending, null);

        try
        {
            if (policy.PatternsEnabled)
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
}
