using System.Text.Json;

namespace LocalRag.Infrastructure;

public sealed record ReleaseUpdateState(DateTimeOffset? LastAttemptUtc = null,
    string? LatestVersion = null, string? LastNotifiedVersion = null, bool LastCheckFailed = false);

public interface IReleaseUpdateStateStore
{
    Task<ReleaseUpdateState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ReleaseUpdateState state, CancellationToken cancellationToken);
}

public sealed class ReleaseUpdateStateStore(string dataDirectory) : IReleaseUpdateStateStore
{
    public string FilePath { get; } = Path.Combine(dataDirectory, "release-update-state.json");

    public async Task<ReleaseUpdateState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > 4096) return new();
            return await JsonSerializer.DeserializeAsync<ReleaseUpdateState>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public async Task SaveAsync(ReleaseUpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record ReleaseUpdateResult(ReleaseUpdate? Available, string Message, bool Notify = false);

/// <summary>Independent from ingestion/model locks. Only release metadata is requested/retained.</summary>
public sealed class ReleaseUpdateMonitor(IReleaseUpdateSource source, IReleaseUpdateStateStore store,
    string installedVersion, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan ManualMinimumInterval = TimeSpan.FromMinutes(1);
    private const string FailureMessage = "更新を確認できませんでした。ネットワークを確認するか、時間をおいて再確認してください。";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Version _installed = ReleaseUpdate.ParseVersion(installedVersion)
        ?? throw new ArgumentException("Invalid installed version", nameof(installedVersion));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReleaseUpdateState _state = new();
    private bool _loaded;
    private ReleaseUpdateResult? _lastResult;

    public TimeSpan NextCheckDelay
    {
        get
        {
            var now = _clock.GetUtcNow();
            if (_state.LastAttemptUtc is not { } last || last > now) return TimeSpan.Zero;
            var remaining = last + CheckInterval - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public async Task<ReleaseUpdateResult> CachedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await LoadAsync(cancellationToken).ConfigureAwait(false); return Snapshot(); }
        finally { _gate.Release(); }
    }

    public async Task<ReleaseUpdateResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var interval = manual ? ManualMinimumInterval : CheckInterval;
            if (_state.LastAttemptUtc is { } last && last <= now && now - last < interval)
                return (_lastResult ?? Snapshot()) with { Notify = false };

            string? failure = null;
            try
            {
                var latest = await source.GetLatestAsync(cancellationToken).ConfigureAwait(false);
                if (ReleaseUpdate.ParseVersion(latest.Version) is null) throw new InvalidDataException();
                _state = _state with { LatestVersion = latest.Version };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // Offline/timeouts are not evidence that the installed version is current.
                failure = FailureMessage;
            }
            _state = _state with { LastAttemptUtc = _clock.GetUtcNow(), LastCheckFailed = failure is not null };
            var result = Snapshot();
            var notified = ReleaseUpdate.ParseVersion(_state.LastNotifiedVersion);
            if (failure is null && result.Available is { } available &&
                (notified is null || ReleaseUpdate.ParseVersion(available.Version)! > notified))
            {
                _state = _state with { LastNotifiedVersion = available.Version };
                result = result with { Notify = true };
            }
            try { await store.SaveAsync(_state, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result = result with { Message = result.Message + " 更新確認の履歴を保存できませんでした。" }; }
            return _lastResult = result;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        _state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private ReleaseUpdateResult Snapshot()
    {
        var latest = ReleaseUpdate.ParseVersion(_state.LatestVersion);
        if (latest is null) return new(null, _state.LastCheckFailed ? FailureMessage : "更新情報：未確認");
        var result = latest > _installed
            ? new(new(_state.LatestVersion!), $"バージョン{_state.LatestVersion}への更新が利用できます。")
            : new ReleaseUpdateResult(null, "新しいバージョンはありません。");
        return _state.LastCheckFailed ? result with { Message = FailureMessage } : result;
    }
}
