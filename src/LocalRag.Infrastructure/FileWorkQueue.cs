using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record FileWorkItem(string Path, int Priority, DateTime LastWriteUtc, long Length, DateTimeOffset CheckedAt);
public sealed record FileDiscovery(TextScan Scan, IReadOnlyList<FileWorkItem> Eligible, int Deferred);

public static class FileDiscoveryPolicy
{
    public static FileDiscovery Select(TextScan scan, IReadOnlyList<WatchedFolder> folders, DateTimeOffset checkedAt, int exclusionMinutes)
    {
        var candidates = new List<FileWorkItem>();
        var deferred = 0;
        foreach (var path in scan.Files)
        {
            try
            {
                var info = new FileInfo(path);
                var modified = info.LastWriteTimeUtc;
                if (!info.Exists || modified > checkedAt.UtcDateTime - TimeSpan.FromMinutes(exclusionMinutes)) { deferred++; continue; }
                candidates.Add(new(path, folders.Where(f => TextFiles.InFolder(path, f)).Max(f => f.Priority), modified, info.Length, checkedAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { deferred++; }
        }
        return new(scan, Order(candidates).ToArray(), deferred);
    }

    public static IOrderedEnumerable<FileWorkItem> Order(IEnumerable<FileWorkItem> files) => files
        .OrderByDescending(f => f.Priority).ThenByDescending(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

    public static bool StillEligible(FileWorkItem item, int exclusionMinutes)
    {
        try
        {
            TextFiles.RejectLinks(item.Path);
            var info = new FileInfo(item.Path);
            return info.Exists && info.Length == item.Length && info.LastWriteTimeUtc == item.LastWriteUtc &&
                info.LastWriteTimeUtc <= item.CheckedAt.UtcDateTime - TimeSpan.FromMinutes(exclusionMinutes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>One active file is never preempted. Each discovery replaces only waiting work.</summary>
public sealed class FileWorkQueue
{
    private readonly object _sync = new();
    private List<FileWorkItem> _waiting = [];
    private FileWorkItem? _active;
    private readonly Dictionary<string, FileWorkItem> _completed = new(StringComparer.OrdinalIgnoreCase);
    private FileDiscovery? _reconciliation;
    public int Count { get { lock (_sync) return _waiting.Count; } }
    public bool HasReconciliation { get { lock (_sync) return _reconciliation is not null; } }
    public IReadOnlyList<FileWorkItem> Snapshot() { lock (_sync) return _waiting.ToArray(); }
    public void Replace(FileDiscovery discovery)
    {
        lock (_sync)
        {
            var present = discovery.Eligible.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _completed.Keys.Where(p => !present.Contains(p)).ToArray()) _completed.Remove(path);
            _waiting = FileDiscoveryPolicy.Order(discovery.Eligible.Where(f => !string.Equals(f.Path, _active?.Path, StringComparison.OrdinalIgnoreCase) &&
                (!_completed.TryGetValue(f.Path, out var done) || done.Length != f.Length || done.LastWriteUtc != f.LastWriteUtc)))
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            _reconciliation = discovery;
        }
    }
    public FileDiscovery? TakeReconciliation()
    {
        lock (_sync) { var scan = _reconciliation; _reconciliation = null; return scan; }
    }
    public FileWorkItem? Take()
    {
        lock (_sync)
        {
            if (_active is not null) throw new InvalidOperationException("A file is already active.");
            if (_waiting.Count == 0) return null;
            var next = _waiting[0]; _waiting.RemoveAt(0); _active = next; return next;
        }
    }
    public void Complete(bool remember = false)
    {
        lock (_sync)
        {
            if (remember && _active is not null) _completed[_active.Path] = _active;
            _active = null;
        }
    }
}
