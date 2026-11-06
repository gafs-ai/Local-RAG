using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

internal static class RecoverySmoke
{
    public static async Task<int> RunAsync(string root, string? tools, CancellationToken token)
    {
        var run = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(run, "data");
        var input = Path.Combine(run, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "recovery.txt"), "public first section\n\npublic second section", token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = Settings(run, port);
        await new SettingsStore(data).SaveAsync(settings, token);
        tools = Path.GetFullPath(tools ?? Path.Combine(data, "tools"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(RecoverySmoke).Assembly.Location, "recovery-worker", run, tools, port.ToString() }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException();
        var stdout = worker.StandardOutput.ReadToEndAsync(token);
        var stderr = worker.StandardError.ReadToEndAsync(token);
        Console.WriteLine($"Recovery fixture: {run}");
        await using var manager = new SurrealServerManager(data, tools);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            while (!File.Exists(Path.Combine(run, "second-embedding.started")))
            {
                if (worker.HasExited) throw new InvalidOperationException("Worker exited before checkpoint.");
                await Task.Delay(100, deadline.Token);
            }
            // Only the child created above and its identity-verified, fixture-owned database are terminated.
            var ownership = JsonSerializer.Deserialize<ServerOwnership>(await File.ReadAllTextAsync(Path.Combine(data, "server", "ownership.json"), token))!;
            using var db = Process.GetProcessById(ownership.ProcessId);
            if (ownership.Port != port || !ownership.Matches(db)) throw new InvalidOperationException("Fixture ownership mismatch.");
            worker.Kill(); await worker.WaitForExitAsync(token);
            db.Kill(); await db.WaitForExitAsync(token);
            Console.WriteLine("Interrupted worker and owned DB after first persisted embedding.");
            await manager.EnsureRunningAsync(settings.Database, token: token);
            Console.WriteLine("Recovered DB ready.");
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token, settings.DataSourceName);
            var state = (await repository.DocumentsAsync(token)).Single();
            var checkpoint = await repository.LoadMonitoringAsync(token);
            if (state.Status != "processing" || state.EmbeddedChunks != 1 || checkpoint?.Running != true ||
                checkpoint.Remaining(MonitoringCheckpoint.ScopeFor(settings), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)) != TimeSpan.Zero)
                throw new InvalidDataException("Unexpected recovery checkpoint.");
            var ai = new RecoveryAi(null);
            Console.WriteLine("Persisted incomplete checkpoint verified.");
            using var service = new TextRagService(repository, ai, settings, data);
            if (await service.FetchAsync(state.DocumentId, token) is not null) throw new InvalidDataException("Incomplete document disclosed.");
            var monitor = new PersistentMonitor(repository, MonitoringCheckpoint.ScopeFor(settings));
            TextCycleResult? result = null;
            await monitor.RunCycleAsync(async ct => result = await service.ScanAsync(token: ct), token);
            if (result?.Indexed != 1 || ai.EmbeddingCalls != 1 || ai.TransformCalls != 0) throw new InvalidDataException("Completed stages were replayed.");
            var complete = await repository.LoadMonitoringAsync(token);
            if (complete?.Running != false || complete.LastCompletedUtc is null) throw new InvalidDataException("Completion was not persisted.");
            var fetched = await service.FetchAsync(state.DocumentId, token);
            if (fetched?.Content != state.Original) throw new InvalidDataException("Recovered source mismatch.");
            Console.WriteLine("PASS: running checkpoint survived DB kill; incomplete hidden; only remaining embedding resumed; full original retrieved; completion persisted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Recovery failure: {ex.GetType().Name}, HTTP={(ex as HttpRequestException)?.StatusCode}");
            Console.Error.WriteLine(ex.StackTrace);
            throw;
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            // Also reclaim the surviving fixture DB if an assertion failed before reconnecting.
            if (manager.Connection is null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                await manager.EnsureRunningAsync(settings.Database, token: cleanup.Token);
            }
            await manager.ShutdownAsync(true);
        }
    }

    public static async Task<int> WorkerAsync(string run, string tools, int port, CancellationToken token)
    {
        var data = Path.Combine(run, "data");
        var settings = Settings(run, port);
        await using var manager = new SurrealServerManager(data, tools);
        try
        {
            await manager.EnsureRunningAsync(settings.Database, token: token);
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token, settings.DataSourceName);
            using var service = new TextRagService(repository, new RecoveryAi(Path.Combine(run, "second-embedding.started")), settings, data);
            var monitor = new PersistentMonitor(repository, MonitoringCheckpoint.ScopeFor(settings));
            await monitor.RunCycleAsync(async ct => { await service.ScanAsync(token: ct); }, token);
            return 2;
        }
        finally { await manager.ShutdownAsync(true); }
    }

    private static AppSettings Settings(string run, int port) => new()
    {
        UpdateExclusionMinutes = 0, // Synthetic fixtures are intentionally processed immediately.
        ExcludeDotHiddenEntries = false, // This isolated diagnostic intentionally uses .local fixtures.
        Folders = [new(Path.Combine(run, "input"))], Database = new() { Port = port },
        Ai = new() { ChatModel = "synthetic-recovery", EmbeddingModel = "synthetic-recovery" },
        TextPipeline = new() { ClassifyEnabled = false, FormatEnabled = false },
        Confidentiality = new() { LlmEnabled = false }
    };

    private sealed class RecoveryAi(string? marker) : ITextRagAi
    {
        public int EmbeddingCalls { get; private set; }
        public int TransformCalls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        { TransformCalls++; return Task.FromResult("{\"chunks\":[{\"start\":1,\"end\":1},{\"start\":2,\"end\":2}]}"); }
        public async Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
        {
            EmbeddingCalls++;
            if (marker is not null && EmbeddingCalls == 2)
            {
                await File.WriteAllTextAsync(marker, "persisted-first-chunk", token);
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(3, inputs.Select(_ => new float[] { 1, 0.1f, 0 }).ToArray());
        }
    }
}
