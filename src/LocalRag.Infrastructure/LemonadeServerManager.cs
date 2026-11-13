using LocalRag.Core;
using System.Net;

namespace LocalRag.Infrastructure;

public sealed record LemonadeServerStatus(IReadOnlyList<LemonadeModel> Models, bool ManagedByApplication);

internal interface IOwnedLemonade : IDisposable
{
    Uri Endpoint { get; }
    bool IsAlive { get; }
    bool OwnsListener { get; }
    bool StartupPending => false;
    Task StopAsync(CancellationToken token);
}

internal interface ILemonadeRuntime
{
    Task<IReadOnlyList<LemonadeModel>?> ProbeAsync(AiConnectionSettings settings, CancellationToken token);
    Task<bool> PortOpenAsync(Uri endpoint, CancellationToken token);
    IOwnedLemonade Start(AiConnectionSettings settings);
}

/// <summary>Owns only processes started by this instance; never adopts a pre-existing server.</summary>
public sealed class LemonadeServerManager : IAsyncDisposable
{
    private readonly ILemonadeRuntime _runtime;
    private readonly TimeSpan _startupTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IOwnedLemonade> _owned = [];

    public LemonadeServerManager() : this(new WindowsLemonadeRuntime(), TimeSpan.FromSeconds(45)) { }
    internal LemonadeServerManager(ILemonadeRuntime runtime, TimeSpan timeout) { _runtime = runtime; _startupTimeout = timeout; }

    public async Task<LemonadeServerStatus> EnsureRunningAsync(AiConnectionSettings settings,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var endpoint = settings.GetBaseUri();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            progress?.Report("AIサーバーへの接続とモデル一覧を確認しています…");
            foreach (var exited in _owned.Where(p => !p.IsAlive).ToArray()) { exited.Dispose(); _owned.Remove(exited); }
            var models = await _runtime.ProbeAsync(settings, token).ConfigureAwait(false);
            // localhost/127.0.0.1 aliases must not cause a second startup on the same local port.
            var owned = _owned.LastOrDefault(p => p.Endpoint.Port == endpoint.Port && p.IsAlive);
            if (models is not null) return new(models, owned?.OwnsListener == true);
            if (owned is null)
            {
                if (!settings.AutoStartServer || !endpoint.IsLoopback) throw new InvalidOperationException("AIサーバーに接続できません。サーバーを起動して再確認してください。");
                if (await _runtime.PortOpenAsync(endpoint, token).ConfigureAwait(false))
                    throw new InvalidOperationException("AI接続先のポートは使用中ですが、モデル一覧を取得できません。重複起動せず接続先の確認を待ちます。");
                token.ThrowIfCancellationRequested();
                owned = _runtime.Start(settings);
                _owned.Add(owned); // Retain ownership even if readiness times out or cancellation arrives.
            }
            progress?.Report("AIサーバーを起動しています。モデル一覧を取得できるまで待っています…");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(_startupTimeout);
            try
            {
                while (owned.IsAlive)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    try { models = await _runtime.ProbeAsync(settings, startup.Token).ConfigureAwait(false); }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
                    { models = null; } // Only during a startup we own; never interpret 503 as permission to launch another process.
                    catch (OperationCanceledException) when (!startup.IsCancellationRequested) { models = null; }
                    if (models is not null)
                    {
                        if (owned.StartupPending) { await Task.Delay(200, startup.Token).ConfigureAwait(false); continue; }
                        if (!owned.OwnsListener) throw new InvalidOperationException("接続先を別のプロセスが使用しています。AIサーバーの起動状態を確認してください。");
                        return new(models, true);
                    }
                    await Task.Delay(200, startup.Token).ConfigureAwait(false);
                }
                throw new InvalidOperationException("AIサーバーの起動を確認できませんでした。実行ファイル・CLI・モデル設定を確認してください。LM Studioが別ポートで起動している場合はその接続先を指定してください。");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("AIサーバーの起動待ちが時間切れになりました。重複起動はしません。「接続確認・モデル一覧更新」で再試行できます。"); }
        }
        finally { _gate.Release(); }
    }

    public async Task ShutdownAsync(bool stopOnExit, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Do not lose ownership if one of several owned servers fails to stop.
            foreach (var process in _owned.ToArray())
            {
                if (stopOnExit && process.IsAlive) await process.StopAsync(token).ConfigureAwait(false);
                process.Dispose(); _owned.Remove(process);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Shutdown preference must be applied explicitly after all AI operations have drained.
        await ShutdownAsync(false).ConfigureAwait(false);
        _gate.Dispose();
    }
}
