using System.Diagnostics;
using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Bounded local diagnostics without exception messages, document contents or credentials.</summary>
public static class OperationalDiagnostics
{
    private static readonly object Sync = new();
    public static void Write(string dataDirectory, string operation, Exception error)
    {
        try
        {
            var chain = new List<object>();
            for (Exception? current = error; current is not null && chain.Count < 8; current = current.InnerException)
                chain.Add(new { Type = current.GetType().FullName, current.HResult, Detail = PipelineErrors.DetailCode(current),
                    Stack = new StackTrace(current, false).ToString() });
            var line = JsonSerializer.Serialize(new { AtUtc = DateTimeOffset.UtcNow, Operation = operation, Errors = chain });
            Append(dataDirectory, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { /* Diagnostics must not stop ingestion, including on read-only/full disks. */ }
    }

    public static void WriteRetrievalTiming(string dataDirectory, string operation, double waitMilliseconds,
        double activeMilliseconds, string outcome)
    {
        try
        {
            Append(dataDirectory, JsonSerializer.Serialize(new { AtUtc = DateTimeOffset.UtcNow,
                Operation = "retrieval." + operation, WaitMilliseconds = waitMilliseconds,
                ActiveMilliseconds = activeMilliseconds, Outcome = outcome }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static void Append(string dataDirectory, string line)
    {
        lock (Sync)
        {
            var directory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "operations.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(directory, "operations.previous.jsonl"), overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
