namespace LocalRag.Infrastructure;

/// <summary>One operation at a time, with retrieval admitted before waiting background work.
/// The single ingestion writer cooperatively lends its lane at safe AI boundaries.</summary>
internal sealed class RetrievalPriorityGate : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _retrieval = new();
    private readonly LinkedList<Waiter> _background = new();
    private bool _held;
    private bool _disposed;

    private sealed class Waiter
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
    }

    public Task WaitAsync(CancellationToken token) => EnterAsync(false, token);
    public Task WaitRetrievalAsync(CancellationToken token) => EnterAsync(true, token);

    private Task EnterAsync(bool retrieval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_held) { _held = true; return Task.CompletedTask; }
            waiter = new();
            waiter.Node = (retrieval ? _retrieval : _background).AddLast(waiter);
        }
        return AwaitQueuedAsync(waiter, token);
    }

    private async Task AwaitQueuedAsync(Waiter waiter, CancellationToken token)
    {
        // Register/dispose outside the lock: cancellation callbacks also take it.
        using var registration = token.Register(() =>
        {
            lock (_sync)
            {
                if (waiter.Node?.List is not { } queue) return; // Already owns the lane.
                queue.Remove(waiter.Node);
                waiter.Node = null;
                waiter.Ready.TrySetCanceled(token);
            }
        });
        await waiter.Ready.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        lock (_sync)
        {
            if (!_held) throw new InvalidOperationException("The operation lane is not held.");
            var queue = _retrieval.Count > 0 ? _retrieval : _background;
            if (queue.First is not { } next) { _held = false; return; }
            queue.RemoveFirst();
            next.Value.Node = null;
            next.Value.Ready.TrySetResult();
        }
    }

    // Caller must own the background lane. Never cancel reacquisition: the caller's
    // finally block still owes a Release, including when ingestion is being stopped.
    public async Task YieldToRetrievalAsync()
    {
        lock (_sync)
        {
            if (!_held) throw new InvalidOperationException("The operation lane is not held.");
            if (_retrieval.Count == 0) return;
            Release();
        }
        await WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var waiter in _retrieval.Concat(_background))
            {
                waiter.Node = null;
                waiter.Ready.TrySetException(new ObjectDisposedException(nameof(RetrievalPriorityGate)));
            }
            _retrieval.Clear(); _background.Clear();
        }
    }
}
