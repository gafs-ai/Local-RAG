using System.Text.Json;
using LocalRag.Infrastructure;

namespace LocalRag.Diagnostics;

/// <summary>Runs a saved configuration against an explicitly prepared evaluation corpus.</summary>
internal static class ConfiguredScan
{
    public static async Task<int> RunAsync(string dataDirectory, string? toolsDirectory, CancellationToken token)
    {
        var store = new SettingsStore(dataDirectory);
        if (!File.Exists(store.FilePath)) throw new InvalidDataException("Create an evaluation settings file first.");
        var settings = await store.LoadAsync(token);
        if (settings.AutomaticMonitoring || settings.Folders.Count == 0)
            throw new InvalidDataException("Use a dedicated configuration with monitoring off and explicit input folders.");
        await using var manager = new SurrealServerManager(dataDirectory, toolsDirectory);
        try
        {
            await manager.EnsureRunningAsync(settings.Database, token: token);
            await using var repository = await TextRagRepository.OpenAsync(manager.Connection!, token);
            using var ai = new LemonadeTextAi(settings);
            using var service = new TextRagService(repository, ai, settings, dataDirectory);
            var result = await service.ScanAsync(new Progress<string>(Console.WriteLine), token);
            var documents = await repository.DocumentsAsync(token);
            // Do not log source contents, paths, prompts, model responses or arbitrary exception messages.
            var report = new
            {
                result,
                documents = documents.Select(d => new { d.DocumentId, d.Status, d.ErrorStage, d.ErrorDetailCode,
                    Characters = d.Original.Length, Chunks = d.Chunks?.Count ?? 0, d.EmbeddedChunks })
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "configured-scan-report.json"), json, token);
            Console.WriteLine(json);
            return result.Failed == 0 && result.InaccessibleRoots == 0 ? 0 : 2;
        }
        finally { await manager.ShutdownAsync(true); }
    }
}
