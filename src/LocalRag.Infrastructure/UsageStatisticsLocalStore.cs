using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Stores only bounded aggregates; never store source paths, dates, IDs or exception messages.</summary>
public sealed class UsageStatisticsLocalStore(string dataDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "statistics-counters.json");
    private static readonly JsonSerializerOptions Options = new(UsageStatisticsPrivacy.JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public async Task<bool> LoadAsync(UsageStatisticsAccumulator accumulator, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return false;
            using var stream = File.OpenRead(_path);
            if (stream.Length > 65536) return false;
            var state = await JsonSerializer.DeserializeAsync<UsageStatisticsStoredState>(stream, Options, token).ConfigureAwait(false);
            return accumulator.Restore(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(UsageStatisticsAccumulator accumulator, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = accumulator.Export();
            if (state.Counters.Count == 0) { File.Delete(_path); return; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
            if (bytes.Length > 65536) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllBytesAsync(_path + ".tmp", bytes, token).ConfigureAwait(false);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }
    public async Task ClearAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { File.Delete(_path); File.Delete(_path + ".tmp"); }
        finally { _gate.Release(); }
    }
}
