using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class MonitoringRecoveryTests
{
    [Fact]
    public async Task InternalTimeoutRestartsImmediatelyAndBackoffIsBounded()
    {
        var runs = new List<bool>(); var waits = new List<double>(); var reports = 0;
        await MonitoringRecovery.RunAsync((restart, _) =>
        {
            runs.Add(restart);
            return runs.Count <= 6 ? Task.FromException(new TaskCanceledException("internal deadline")) : Task.CompletedTask;
        }, (_, _) => { reports++; return Task.CompletedTask; }, default,
        (wait, _) => { waits.Add(wait.TotalSeconds); return Task.CompletedTask; });
        Assert.False(runs[0]); Assert.All(runs.Skip(1), Assert.True);
        Assert.Equal(new double[] { 5, 15, 30, 60, 60, 60 }, waits); Assert.Equal(6, reports);
    }

    [Fact]
    public async Task ExplicitStopNeverRestartsEvenIfReportedAsTaskCanceled()
    {
        using var stop = new CancellationTokenSource(); var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MonitoringRecovery.RunAsync((_, _) =>
        {
            attempts++; stop.Cancel(); return Task.FromException(new TaskCanceledException());
        }, (_, _) => throw new Exception("Must not retry"), stop.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task StopDuringBackoffCancelsThePendingRestart()
    {
        using var stop = new CancellationTokenSource(); var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MonitoringRecovery.RunAsync((_, _) =>
        { attempts++; return Task.FromException(new TimeoutException()); }, (_, _) => Task.CompletedTask, stop.Token,
        (_, token) => { stop.Cancel(); return Task.Delay(Timeout.Infinite, token); }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PersistenceTimeoutRecoversButCorruptStateAndUnauthorizedDoNot()
    {
        var attempts = 0;
        await MonitoringRecovery.RunAsync((_, _) => ++attempts == 1
            ? Task.FromException(new MonitoringPersistenceException(new TimeoutException())) : Task.CompletedTask,
            (_, _) => Task.CompletedTask, default, (_, _) => Task.CompletedTask);
        Assert.Equal(2, attempts);
        foreach (var failure in new Exception[] { new MonitoringPersistenceException(new InvalidDataException()),
            new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized) })
        {
            Assert.False(MonitoringRecovery.IsTransient(failure));
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => MonitoringRecovery.RunAsync(
                (_, _) => Task.FromException(failure), (_, _) => throw new Exception("Must not retry"), default)));
        }
    }

    [Fact]
    public void DiagnosticOmitsExceptionMessagesAndCannotFailOnUnavailableDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N"));
        OperationalDiagnostics.Write(root, "monitor.test", new TaskCanceledException("secret-token-123", new TimeoutException("private document text")));
        var text = File.ReadAllText(Path.Combine(root, "logs", "operations.jsonl"));
        Assert.Contains("TaskCanceledException", text); Assert.Contains("TimeoutException", text);
        Assert.DoesNotContain("secret-token-123", text); Assert.DoesNotContain("private document text", text);
        var file = Path.Combine(root, "not-a-directory"); File.WriteAllText(file, "x");
        OperationalDiagnostics.Write(file, "monitor.test", new TimeoutException());
    }

    [ServerFact]
    public async Task ResultDisplayTimeoutDoesNotStopWorkerOrDiscardRemainingFiles()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = Path.Combine(fixture.Root, "input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "one.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(input, "two.txt"), "second");
        var settings = TextRagTests.Settings(input);
        await using var repo = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        var data = Path.Combine(fixture.Root, "data");
        using var service = new TextRagService(repo, new FakeTextAi(), settings, data);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var indexed = 0; var failures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunMonitoringAsync(
            new PersistentMonitor(repo, MonitoringCheckpoint.ScopeFor(settings)), null, result =>
            {
                if (result.Indexed > 0 && ++indexed == 1) throw new TaskCanceledException("status query timeout");
                if (indexed == 2) stop.Cancel();
                return Task.CompletedTask;
            }, _ => { failures++; return Task.CompletedTask; }, _ => Task.CompletedTask, stop.Token, immediate: true));
        Assert.Equal(2, indexed); Assert.Equal(1, failures);
        Assert.All(await repo.DocumentsAsync(default), d => Assert.Equal("ready", d.Status));
        Assert.Equal(2, (await repo.HistoryAsync()).Count);
        Assert.Contains("monitor.result-refresh", File.ReadAllText(Path.Combine(data, "logs", "operations.jsonl")));
    }
}
