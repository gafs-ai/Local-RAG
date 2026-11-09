using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record IngestionActivity(long Sequence, string? CurrentFile, string Stage, IReadOnlyList<FileWorkItem> Waiting);

public sealed partial class TextRagService
{
    private readonly object _activitySync = new();
    private IngestionActivity _activity = new(0, null, "待機中", []);
    public event Action<IngestionActivity>? ActivityChanged;
    public event Action? HistoryChanged;
    private void ChangeActivity(string? path, string stage, IReadOnlyList<FileWorkItem>? waiting = null, bool queueOnly = false)
    {
        IngestionActivity snapshot;
        lock (_activitySync)
        {
            snapshot = _activity = new(_activity.Sequence + 1, queueOnly ? _activity.CurrentFile : path,
                queueOnly ? _activity.Stage : stage, waiting ?? _activity.Waiting);
        }
        ActivityChanged?.Invoke(snapshot);
    }
    private void ShowQueue(FileWorkQueue queue) => ChangeActivity(null, "", queue.Snapshot(), queueOnly: true);
    private sealed class StageProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }

    private async Task<TextCycleResult> ProcessFileAsync(FileWorkItem file, IProgress<string>? progress, CancellationToken token, bool laneHeld = false)
    {
        if (!laneHeld) await _operations.WaitAsync(token).ConfigureAwait(false);
        var started = _clock.GetUtcNow();
        var completed = false;
        ChangeActivity(file.Path, "読み取り・変更確認");
        try
        {
            var result = await ProcessFileCoreAsync(file, new StageProgress(message =>
            {
                ChangeActivity(file.Path, message);
                progress?.Report(message);
            }), token, laneHeld: true).ConfigureAwait(false);
            completed = true;
            if (result.RetrySuppressed > 0) return result; // No new attempt: do not flood history.
            using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var state = await repository.GetAsync(DocumentIdentity.Key(settings.DataSourceName, file.Path), finalization.Token).ConfigureAwait(false);
            var status = result.Deferred > 0 ? "deferred" : result.Unchanged > 0 ? "unchanged" : state?.Status ?? "skipped";
            await repository.RecordHistoryAsync(new(Guid.NewGuid(), started, _clock.GetUtcNow(), file.Path, status,
                status is "failed" ? state?.ErrorStage : null, status is "failed" ? state?.ErrorDetailCode : null,
                state?.WithdrawalReason, state?.ProcessingNotices.ToArray(), _configuration, state?.Failure), finalization.Token).ConfigureAwait(false);
            HistoryChanged?.Invoke();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !completed)
        {
            // Save the attempt, while leaving the resumable document checkpoint untouched.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await repository.RecordHistoryAsync(new(Guid.NewGuid(), started, _clock.GetUtcNow(), file.Path, "interrupted",
                ConfigurationId: _configuration), timeout.Token).ConfigureAwait(false);
            HistoryChanged?.Invoke();
            throw;
        }
        finally
        {
            ChangeActivity(null, "待機中");
            if (!laneHeld) _operations.Release();
        }
    }
}
