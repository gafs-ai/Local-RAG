using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class CheckpointTests
{
    [ServerFact]
    public async Task PersistedCheckpointSurvivesReopenAndRecoversOnlyUnfinishedStage()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var job = new JobCheckpoint(Guid.NewGuid(), Guid.NewGuid(), "source-hash", "configuration-1",
            JobStatus.Running, ["extract", "format"], "classify");
        await using (var store = await SurrealCheckpointStore.OpenAsync(fixture.Manager.Connection!))
        {
            await store.SaveAsync(job);
            await store.SaveAsync(job); // Same identity updates the checkpoint instead of adding another job.
        }
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "restart_probe"))
        {
            (await db.RawQuery("DEFINE TABLE vectors SCHEMALESS; DEFINE INDEX vector_idx ON vectors FIELDS embedding DISKANN DIMENSION 3 DIST COSINE TYPE F32; CREATE vectors:one SET embedding = [1.0,0.0,0.0], name = 'persisted';")).EnsureAllOks();
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using (var db = await SurrealConnection.OpenAsync(fixture.Manager.Connection!, "restart_probe"))
        {
            var read = await db.RawQuery("SELECT VALUE name FROM vectors WHERE embedding <|1,64|> [1.0,0.0,0.0];");
            read.EnsureAllOks();
            Assert.Equal("persisted", read.GetValue<string[]>(0)!.Single());
        }
        await using var reopened = await SurrealCheckpointStore.OpenAsync(fixture.Manager.Connection!);
        var loaded = await reopened.LoadAsync(job.JobId);
        Assert.NotNull(loaded);
        Assert.Equal(job.SourceHash, loaded.SourceHash);
        Assert.Equal(job.CompletedStages, loaded.CompletedStages);
        var recovered = loaded.RecoverAfterInterruption();
        Assert.Equal(JobStatus.Queued, recovered.Status);
        Assert.Null(recovered.CurrentStage);
        Assert.Equal(job.CompletedStages, recovered.CompletedStages);
        Assert.Null(await reopened.LoadAsync(Guid.NewGuid()));
    }
}
