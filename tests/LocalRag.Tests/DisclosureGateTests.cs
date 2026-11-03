using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Tests;

public class DisclosureGateTests
{
    [Fact]
    public async Task DefaultPolicyUsesPatternsWithoutCallingExperimentalClassifier()
    {
        var classifier = new Classifier(DisclosureDecision.Withheld);
        var gate = new DisclosureGate(classifier);
        var policy = new ConfidentialityPolicy();
        Assert.True(policy.Enabled);
        Assert.True(policy.PatternsEnabled);
        Assert.False(policy.LlmEnabled);
        Assert.Equal(DisclosureDecision.Allowed, (await gate.EvaluateAsync(Guid.NewGuid(), "公開資料", "公開資料", policy)).Decision);
        Assert.Equal(DisclosureDecision.Withheld, (await gate.EvaluateAsync(Guid.NewGuid(), "社外秘", "資料", policy)).Decision);
        Assert.Equal(0, classifier.Calls);
    }
    [Fact]
    public async Task RemovedConfidentialHeaderStillBlocksCleanCandidate()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "社外秘\n機密本文", "本文", new());
        Assert.Equal(DisclosureDecision.Withheld, result.Decision);
        Assert.Null(result.Content);
        Assert.DoesNotContain("機密本文", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CandidateAlsoGetsPatternInspection()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed));
        Assert.Null((await gate.EvaluateAsync(Guid.NewGuid(), "原文", "CONFIDENTIAL", new())).Content);
    }

    [Theory]
    [InlineData(DisclosureDecision.Withheld)]
    [InlineData(DisclosureDecision.Pending)]
    [InlineData(DisclosureDecision.Unchecked)]
    public async Task OnlyExplicitLlmAllowCanReleaseContent(DisclosureDecision decision)
    {
        var gate = new DisclosureGate(new Classifier(decision));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "原文", "返却候補", new() { LlmEnabled = true });
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ProviderFailureDoesNotReleaseContentOrErrorText()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed, new InvalidDataException("機密の応答")));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "原文", "機密の応答", new() { LlmEnabled = true });
        Assert.Equal(DisclosureDecision.Pending, result.Decision);
        Assert.Null(result.Content);
        Assert.DoesNotContain("機密の応答", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task InvalidRegexFailsClosed()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "原文", "候補", new() { Patterns = ["["] });
        Assert.Equal(DisclosureDecision.Pending, result.Decision);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task DisablingAllSubchecksDoesNotAccidentallyAllow()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "原文", "候補", new() { LlmEnabled = false, PatternsEnabled = false });
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ExplicitGlobalDisableIsMarkedUnchecked()
    {
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Withheld));
        var result = await gate.EvaluateAsync(Guid.NewGuid(), "社外秘", "候補", new() { Enabled = false });
        Assert.Equal(DisclosureDecision.Unchecked, result.Decision);
        Assert.Equal("候補", result.Content);
    }

    [Fact]
    public async Task CallerCancellationNeverReturnsData()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var gate = new DisclosureGate(new Classifier(DisclosureDecision.Allowed));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EvaluateAsync(Guid.NewGuid(), "原文", "候補", new(), cancelled.Token));
    }

    private sealed class Classifier(DisclosureDecision decision, Exception? failure = null) : IConfidentialityClassifier
    {
        public int Calls { get; private set; }
        public Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null ? Task.FromResult(decision) : Task.FromException<DisclosureDecision>(failure);
        }
    }
}
