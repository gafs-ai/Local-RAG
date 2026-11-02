namespace LocalRag.Core;

public enum JobStatus { Queued, Running, Completed, Failed, Paused }

public sealed record JobCheckpoint(
    Guid JobId, Guid DocumentId, string SourceHash, string ConfigurationVersion,
    JobStatus Status, IReadOnlyList<string> CompletedStages, string? CurrentStage)
{
    public JobCheckpoint RecoverAfterInterruption() => Status == JobStatus.Running
        ? this with { Status = JobStatus.Queued, CurrentStage = null }
        : this;
}

public interface ICheckpointStore
{
    Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<JobCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default);
}
