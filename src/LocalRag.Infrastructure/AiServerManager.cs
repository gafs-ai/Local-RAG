using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Lifecycle ownership is independent of model selection. Removed profiles retain their exit preference.</summary>
public sealed class AiServerManager : IAsyncDisposable
{
    private sealed class Entry(ILemonadeRuntime runtime, LemonadeServerManager manager, AiEndpointSettings endpoint)
    { public ILemonadeRuntime Runtime { get; } = runtime; public LemonadeServerManager Manager { get; } = manager; public AiEndpointSettings Endpoint { get; set; } = endpoint; }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<AiEndpointSettings, string?, ILemonadeRuntime> _factory;
    private readonly TimeSpan _timeout;
    public AiServerManager() : this((endpoint, key) => new WindowsAiRuntime(endpoint, key), TimeSpan.FromSeconds(90)) { }
    internal AiServerManager(Func<AiEndpointSettings, string?, ILemonadeRuntime> factory, TimeSpan timeout) { _factory = factory; _timeout = timeout; }

    public async Task<LemonadeServerStatus> EnsureRunningAsync(AiEndpointSettings endpoint, string? apiKey,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var errors = endpoint.Validate();
        if (errors.Count > 0) throw new InvalidDataException(errors[0]);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var key = endpoint.Id + "|" + endpoint.Provider + "|" + endpoint.GetBaseUri().AbsoluteUri;
            if (!_entries.TryGetValue(key, out var entry))
            {
                var runtime = _factory(endpoint, apiKey);
                entry = new(runtime, new LemonadeServerManager(runtime, _timeout), endpoint);
                _entries.Add(key, entry);
            }
            entry.Endpoint = endpoint;
            if (entry.Runtime is WindowsAiRuntime windows) { windows.Endpoint = endpoint; windows.ApiKey = apiKey; }
            progress?.Report(endpoint.Name + "：接続とモデル一覧を確認しています…");
            return await entry.Manager.EnsureRunningAsync(AiConnectionSettings.ForEndpoint(endpoint), null, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ShutdownAsync(AiConnectionSettings settings, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            foreach (var entry in _entries.Values)
            {
                var profile = settings.Endpoints().FirstOrDefault(e => e.Id == entry.Endpoint.Id) ?? entry.Endpoint;
                try { await entry.Manager.ShutdownAsync(profile.StopServerOnExit, token).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new InvalidOperationException("AIサーバーの一部を終了できませんでした。強制終了はしていません。", new AggregateException(errors));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values) await entry.Manager.DisposeAsync().ConfigureAwait(false);
        _entries.Clear(); _gate.Dispose();
    }
}
