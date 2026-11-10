using System.Collections.Concurrent;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;
using LocalRag.Mcp;

namespace LocalRag.Tests;

public class RetrievalPriorityTests
{
    [Fact]
    public async Task RetrievalRunsFirstInFifoOrderAndCancelledWaitersDoNotHoldTheLane()
    {
        using var gate = new RetrievalPriorityGate();
        await gate.WaitAsync(default);
        var background = gate.WaitAsync(default);
        using var cancelled = new CancellationTokenSource();
        var abandoned = gate.WaitRetrievalAsync(cancelled.Token);
        var first = gate.WaitRetrievalAsync(default);
        var second = gate.WaitRetrievalAsync(default);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted); Assert.False(background.IsCompleted);
        gate.Release(); await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(background.IsCompleted);
        gate.Release(); await background.WaitAsync(TimeSpan.FromSeconds(5)); gate.Release();
        await gate.WaitAsync(default); gate.Release();
    }

    [Fact]
    public async Task CancellingIngestionWhileYieldedCannotReleaseTheRetrievalOwnersLane()
    {
        using var gate = new RetrievalPriorityGate();
        using var stop = new CancellationTokenSource();
        await gate.WaitAsync(default);
        var retrieval = gate.WaitRetrievalAsync(default);
        var yielded = gate.YieldToRetrievalAsync();
        await retrieval.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var waitingBackground = gate.WaitAsync(default);
        Assert.False(yielded.IsCompleted); Assert.False(waitingBackground.IsCompleted);
        gate.Release(); await yielded.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<OperationCanceledException>(() => stop.Token.ThrowIfCancellationRequested());
        Assert.False(waitingBackground.IsCompleted);
        gate.Release(); await waitingBackground.WaitAsync(TimeSpan.FromSeconds(5)); gate.Release();
    }

    [Fact]
    public async Task FailedAiCallStillYieldsToQueuedRetrievalBeforeReturningItsError()
    {
        using var gate = new RetrievalPriorityGate();
        await gate.WaitAsync(default);
        var inner = new ControlledAi { Armed = true, FailFirst = true };
        var ai = new YieldingTextRagAi(inner, gate);
        var inference = ai.EmbedAsync(["document"], default);
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retrieval = gate.WaitRetrievalAsync(default);
        inner.FinishFirst.TrySetResult();
        await retrieval.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inference.IsCompleted);
        gate.Release();
        await Assert.ThrowsAsync<HttpRequestException>(() => inference);
        gate.Release();
        await gate.WaitRetrievalAsync(default); gate.Release();
    }

    [Fact]
    public async Task AiCancellationWhileRetrievalOwnsTheLaneReacquiresBeforeUnwinding()
    {
        using var gate = new RetrievalPriorityGate();
        using var stop = new CancellationTokenSource();
        await gate.WaitAsync(default);
        var inner = new ControlledAi { Armed = true };
        var ai = new YieldingTextRagAi(inner, gate);
        var inference = ai.EmbedAsync(["document"], stop.Token);
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retrieval = gate.WaitRetrievalAsync(default);
        inner.FinishFirst.TrySetResult();
        await retrieval.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var nextRetrieval = gate.WaitRetrievalAsync(default);
        Assert.False(inference.IsCompleted);
        gate.Release(); await nextRetrieval.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inference.IsCompleted);
        gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inference);
        gate.Release();
        await gate.WaitAsync(default); gate.Release();
    }

    [ServerFact]
    public Task SearchAndFetchInterruptBetweenEmbeddings() => ExerciseServiceAsync(blockChat: false, cancelSearch: false);

    [ServerFact]
    public Task SearchAndDisclosureInterruptBetweenLlmCalls() => ExerciseServiceAsync(blockChat: true, cancelSearch: false);

    [ServerFact]
    public Task CancelledSearchDoesNotStopIngestionOrLaterRetrieval() => ExerciseServiceAsync(blockChat: false, cancelSearch: true);

    [ServerFact]
    public async Task HttpMcpReturnsPublishedDataBeforeTheActiveDocumentFinishes()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "guide.txt"), "Published guide for retrieval.");
        var settings = TextRagTests.Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new ControlledAi();
        using var service = new TextRagService(repo, ai, settings, Path.Combine(fixture.Root, "data"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Assert.Equal(1, (await service.ScanAsync(token: timeout.Token)).Indexed);
        var ready = Assert.Single(await repo.DocumentsAsync(timeout.Token));
        var observed = new ObservedRetrieval(service);
        var port = McpTests.FreePort(); var secret = new string('e', 64);
        await using var host = await LocalMcpHost.StartAsync(observed, port, secret, timeout.Token);
        await using var client = await McpTests.ConnectAsync(port, secret);
        await File.WriteAllTextAsync(Path.Combine(input, "working.txt"), "first\n\nsecond\n\nthird");
        ai.Armed = true;
        var scan = service.ScanAsync(token: timeout.Token);
        try
        {
            await ai.Started.Task.WaitAsync(timeout.Token);
            var search = client.CallToolAsync("search", new Dictionary<string, object?>
                { ["query"] = "retrieval-priority-query" }, cancellationToken: timeout.Token).AsTask();
            await observed.SearchQueued.Task.WaitAsync(timeout.Token);
            Assert.False(search.IsCompleted);
            ai.FinishFirst.TrySetResult();
            var reply = await search.WaitAsync(timeout.Token);
            Assert.False(reply.IsError == true);
            Assert.Contains(McpTests.Body(reply).GetProperty("results").EnumerateArray(),
                r => r.GetProperty("id").GetString() == ready.DocumentId.ToString("D") && r.TryGetProperty("text", out _));
            await ai.SecondStarted.Task.WaitAsync(timeout.Token);
            Assert.False(scan.IsCompleted);
            Assert.True(Array.IndexOf(ai.Events.ToArray(), "query") < Array.IndexOf(ai.Events.ToArray(), "background-2"));
        }
        finally { ai.FinishFirst.TrySetResult(); ai.FinishRest.TrySetResult(); }
        Assert.Equal(1, (await scan.WaitAsync(timeout.Token)).Indexed);
    }

    private sealed class ObservedRetrieval(ITextRetrieval inner) : ITextRetrieval
    {
        public TaskCompletionSource SearchQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TextSearchResult>> SearchAsync(string query, int limit = 5, CancellationToken token = default)
        {
            var task = inner.SearchAsync(query, limit, token);
            SearchQueued.TrySetResult();
            return task;
        }
        public Task<TextSearchResult?> FetchAsync(Guid documentId, CancellationToken token = default) => inner.FetchAsync(documentId, token);
    }

    private static async Task ExerciseServiceAsync(bool blockChat, bool cancelSearch)
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ready.txt"), "Published guide for retrieval.");
        var settings = TextRagTests.Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var ai = new ControlledAi { BlockChat = blockChat };
        var data = Path.Combine(fixture.Root, "data");
        using var service = new TextRagService(repo, ai, settings, data);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Assert.Equal(1, (await service.ScanAsync(token: timeout.Token)).Indexed);
        var ready = Assert.Single(await repo.DocumentsAsync(timeout.Token));
        await File.WriteAllTextAsync(Path.Combine(input, "working.txt"), "first section\n\nsecond section\n\nthird section");
        ai.Armed = true;
        var scan = service.ScanAsync(token: timeout.Token);
        try
        {
            await ai.Started.Task.WaitAsync(timeout.Token);
            var processing = (await repo.DocumentsAsync(timeout.Token)).Single(d => d.Path.EndsWith("working.txt"));
            Assert.Equal("processing", processing.Status);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var search = service.SearchAsync("retrieval-priority-query", token: cancel.Token);
            var fetch = service.FetchAsync(ready.DocumentId, timeout.Token);
            var unfinished = service.FetchAsync(processing.DocumentId, timeout.Token);
            Assert.False(search.IsCompleted); Assert.False(fetch.IsCompleted);
            if (cancelSearch)
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
                Assert.False(scan.IsCompleted);
            }
            ai.FinishFirst.TrySetResult();
            if (!cancelSearch)
            {
                var result = await search.WaitAsync(timeout.Token);
                Assert.Contains(result, r => r.DocumentId == ready.DocumentId && r.Content is not null);
                Assert.DoesNotContain(result, r => r.DocumentId == processing.DocumentId);
            }
            Assert.Equal(ready.Original, (await fetch.WaitAsync(timeout.Token))!.Content);
            Assert.Null(await unfinished.WaitAsync(timeout.Token));
            await ai.SecondStarted.Task.WaitAsync(timeout.Token);
            Assert.False(scan.IsCompleted); // Still the same document, now resumed at its next inference.
            var events = ai.Events.ToArray();
            if (!cancelSearch) Assert.True(Array.IndexOf(events, "query") < Array.IndexOf(events, "background-2"));
            else Assert.DoesNotContain("query", events);
            Assert.Equal(1, ai.MaxConcurrentCalls);
        }
        finally { ai.FinishFirst.TrySetResult(); ai.FinishRest.TrySetResult(); }
        Assert.Equal(1, (await scan.WaitAsync(timeout.Token)).Indexed);
        Assert.All(await repo.DocumentsAsync(timeout.Token), d => Assert.Equal("ready", d.Status));
        Assert.NotEmpty(await service.SearchAsync("retrieval-priority-query", token: timeout.Token));
        var log = (await File.ReadAllLinesAsync(Path.Combine(data, "logs", "operations.jsonl")))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Contains(log, l => l.GetProperty("Operation").GetString() == "retrieval.search" &&
            l.GetProperty("Outcome").GetString() == (cancelSearch ? "cancelled" : "completed"));
        Assert.DoesNotContain("retrieval-priority-query", await File.ReadAllTextAsync(Path.Combine(data, "logs", "operations.jsonl")));
    }

    private sealed class ControlledAi : ITextRagAi
    {
        private readonly FakeTextAi _inner = new();
        private int _backgroundCalls;
        private int _active;
        public bool Armed { get; set; }
        public bool BlockChat { get; init; }
        public bool FailFirst { get; init; }
        public int MaxConcurrentCalls { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishRest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task<T> CallAsync<T>(bool chat, bool retrieval, Func<Task<T>> action, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);
            try
            {
                if (retrieval) Events.Enqueue(chat ? "disclosure" : "query");
                else if (Armed && (BlockChat || !chat))
                {
                    var count = ++_backgroundCalls;
                    Events.Enqueue("background-" + count);
                    if (count == 1)
                    {
                        Started.TrySetResult(); await FinishFirst.Task.WaitAsync(token);
                        if (FailFirst) throw new HttpRequestException("test");
                    }
                    else if (count == 2) { SecondStarted.TrySetResult(); await FinishRest.Task.WaitAsync(token); }
                }
                return await action();
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) =>
            CallAsync(true, input.Contains("\"fragment\""), () => _inner.CompleteAsync(system, input, token), token);
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) =>
            CallAsync(false, inputs.Any(i => i.Contains("retrieval-priority-query")), () => _inner.EmbedAsync(inputs, token), token);
    }
}
