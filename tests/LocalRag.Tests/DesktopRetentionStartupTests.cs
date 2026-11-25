using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class DesktopRetentionStartupTests
{
    [ServerFact]
    public async Task SearchAndFetchRemainAvailableDuringDeferredMaintenanceAndAfterCancellation()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "docs")).FullName;
        const string body = "公開資料。花壇の水やりは朝に行います。";
        await File.WriteAllTextAsync(Path.Combine(root, "guide.txt"), body);
        var settings = TextRagTests.Settings(root);
        var ai = new FakeTextAi();
        var data = Path.Combine(fixture.Root, "data");
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();
        var checkCount = 0;
        var suspend = false;
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!, default, settings.DataSourceName, () =>
        {
            Interlocked.Increment(ref checkCount);
            if (!Volatile.Read(ref suspend)) return true;
            entered.TrySetResult();
            gate.Wait(cancel.Token);
            return true;
        }, deferAutomaticRetention: true);
        Assert.Equal(0, checkCount);
        using var service = new TextRagService(repository, ai, settings, data);
        Assert.Equal(1, (await service.ScanAsync()).Indexed);
        Volatile.Write(ref suspend, true);
        var cleanup = Task.Run(() => repository.RunAutomaticRetentionAsync(cancel.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var document = Assert.Single(await service.SearchAsync("水やり", 1).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(body, (await service.FetchAsync(document.DocumentId))!.Content);
            Assert.False(cleanup.IsCompleted);
        }
        finally
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        Assert.Single(await repository.DocumentStatusesAsync(default));
    }
}
