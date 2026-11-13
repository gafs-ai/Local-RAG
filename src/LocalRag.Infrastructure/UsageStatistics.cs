using System.Text.Json;
using System.Security.Cryptography;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>CPU/GPU names are catalogued; the bounded Windows NPU name is preserved as reported.</summary>
public sealed record UsageStatisticsHardwareInput(string? Cpu = null, string? Gpu = null, string? Npu = null, long MemoryBytes = 0);

public static class UsageStatisticsPrivacy
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".markdown", ".doc", ".docx", ".docm", ".pdf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv",
      ".cs", ".py", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".c", ".h", ".rs", ".java", ".kt", ".kts", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".toml", ".ini", ".sql", ".rst", ".tex", ".tsv" };
    private static readonly string[] Models = ["Gemma-3-4b-it-mm-NPU", "Gemma-3-4b-it-GGUF", "Gemma-3-4b-it-mm-GGUF",
        "Gemma-3-1b-it-GGUF", "Gemma-2-2b-it-GGUF", "nomic-embed-text-v2-moe-GGUF", "nomic-embed-text-v1.5-GGUF",
        "Qwen3-4B-GGUF", "Qwen3-8B-GGUF", "Qwen3-4B-Hybrid", "Phi-4-mini-instruct-GGUF"];
    private static readonly string[] Cpus = ["AMD Ryzen AI 5 PRO 340", "AMD Ryzen AI 9 HX 370", "AMD Ryzen AI 9 HX 375", "AMD Ryzen AI 9 365",
        "AMD Ryzen 7 7840U", "AMD Ryzen 7 8840U", "AMD Ryzen 7 8845HS", "Intel Core Ultra 7 258V", "Intel Core Ultra 7 155H",
        "Intel Core Ultra 5 125H", "Intel Core i7-13700", "Intel Core i5-13400", "Qualcomm Snapdragon X Elite", "Qualcomm Snapdragon X Plus"];
    private static readonly string[] Gpus = ["AMD Radeon 840M", "AMD Radeon 890M", "AMD Radeon 880M", "AMD Radeon 780M", "AMD Radeon 760M",
        "Intel Arc 140V", "Intel Arc 130V", "Intel Arc Graphics", "Intel Iris Xe Graphics", "NVIDIA GeForce RTX 3060",
        "NVIDIA GeForce RTX 4060", "NVIDIA GeForce RTX 4070", "NVIDIA GeForce RTX 4080", "NVIDIA GeForce RTX 4090", "Qualcomm Adreno"];
    public static string Extension(string? value) => value is not null && Extensions.Contains(value) ? value.ToLowerInvariant() : "other";
    public static string Model(string? value) => Known(value, Models, "other");
    private static string Known(string? value, string[] allowed, string fallback = "unknown") =>
        allowed.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
    public static bool IsValidNpuName(string? value) => value is { Length: <= 160 } &&
        value.Any(c => !char.IsWhiteSpace(c) && c != '\uFEFF') && !value.Any(char.IsControl);
    public static string NpuName(string? value) => IsValidNpuName(value) ? value! : "unknown";
    public static UsageStatisticsHardware Hardware(UsageStatisticsHardwareInput? input) => input is null ? new("unknown", "unknown", "unknown", 0) :
        new(Known(input.Cpu, Cpus), Known(input.Gpu, Gpus), NpuName(input.Npu), MemoryBucket(input.MemoryBytes));
    public static int MemoryBucket(long bytes)
    {
        if (bytes <= 0) return 0;
        var gib = bytes / (1024d * 1024 * 1024);
        foreach (var bucket in new[] { 4, 8, 16, 24, 32, 48, 64, 96, 128, 256 })
            if (gib <= bucket) return bucket;
        return 512;
    }
    public static string Version(string? value) => System.Version.TryParse(value, out var version) &&
        version.Major is >= 0 and <= 999 && version.Minor is >= 0 and <= 99 && version.Build is >= 0 and <= 9999 && version.Revision < 0
        ? version.ToString(3) : "unknown";
    private static string EnumName<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "unknown";
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
    public static UsageStatisticsConfiguration Configuration(AppSettings settings)
    {
        var current = settings.Management;
        var defaults = ManagementSettings.CreateDefault();
        if (current is null)
        {
            var baseline = new TextRagSettings();
            var legacy = settings.TextPipeline;
            var changes = new[] {
                new UsageStatisticsPromptChange("Classification", 0, legacy.ClassifyPrompt != baseline.ClassifyPrompt),
                new UsageStatisticsPromptChange("Format", 0, legacy.FormatPrompt != baseline.FormatPrompt),
                new UsageStatisticsPromptChange("Chunk", 0, legacy.ChunkPrompt != baseline.ChunkPrompt),
                new UsageStatisticsPromptChange("ParentChunk", 0, legacy.ParentPrompt != baseline.ParentPrompt),
                new UsageStatisticsPromptChange("CodeDescription", 0, legacy.CodePrompt != baseline.CodePrompt),
                new UsageStatisticsPromptChange("ParentDescription", 0, legacy.ParentDescriptionPrompt != baseline.ParentDescriptionPrompt),
                new UsageStatisticsPromptChange("Confidentiality", 0, legacy.ConfidentialityPrompt != baseline.ConfidentialityPrompt),
                new UsageStatisticsPromptChange("WordRepair", 0, legacy.Word.RepairPrompt != baseline.Word.RepairPrompt) };
            return new(!Same(legacy, baseline), !Same(legacy.Categories, baseline.Categories), !Same(settings.Confidentiality, ConfidentialityPolicy.CreateDefault()), changes,
                [new(EnumName(legacy.SourceMode), EnumName(legacy.EffectiveChildMode), EnumName(legacy.EffectiveParentMode), EnumName(legacy.ParentEmbedding),
                    legacy.ClassifyEnabled, legacy.FormatEnabled, changes.Any(c => c.Changed), 1)]);
        }
        var prompts = Enum.GetValues<PromptPurpose>().Where(p => p != PromptPurpose.Search).Select(p =>
            new UsageStatisticsPromptChange(p.ToString(), Math.Min(200, current.UserPrompts.Count(x => x.Purpose == p)),
                current.UserPrompts.Any(x => x.Purpose == p))).ToArray();
        bool Custom(string? id) => !string.IsNullOrEmpty(id) && !SystemPrompts.All.Any(p => p.Id == id);
        var routes = current.OrderedRoutes().Take(64).Select(r => new UsageStatisticsRoute(EnumName(r.SourceMode), EnumName(r.ChildMode),
            EnumName(r.ParentMode), EnumName(r.ParentEmbedding), r.ClassifyEnabled, !string.IsNullOrEmpty(r.FormatPromptId),
            new[] { r.ClassifyPromptId, r.FormatPromptId, r.ChunkPromptId, r.ParentPromptId, r.CodePromptId, r.ParentDescriptionPromptId, r.WordRepairPromptId }
                .Any(Custom) || r.CategoryOverrides.Any(c => Custom(c.FormatPromptId) || Custom(c.ChunkPromptId) || Custom(c.ParentPromptId)), 1))
            .GroupBy(r => r).Select(g => g.Key with { Count = g.Count() }).OrderBy(r => r.Input, StringComparer.Ordinal)
            .ThenBy(r => r.ChildMode, StringComparer.Ordinal).ThenBy(r => r.ParentMode, StringComparer.Ordinal).Take(32).ToArray();
        return new(!Same(current.OrderedRoutes(), defaults.OrderedRoutes()), !Same(current.Categories, defaults.Categories),
            !Same(current.Retrieval, defaults.Retrieval), prompts, routes);
    }
    internal static bool ValidReport(UsageStatisticsReport? report, bool allowEmptyProcessing = false)
    {
        if (report is null || report.SchemaVersion != 1 || report.AppVersion != Version(report.AppVersion) ||
            report.Hardware is not { } h || report.Models is not { } m || report.Settings is not { } s ||
            report.Processing is null || (!allowEmptyProcessing && report.Processing.Count == 0) ||
            !UsageStatisticsAccumulator.ValidCounters(report.Processing)) return false;
        bool Choice(string value, string[] choices) => value == "unknown" || choices.Contains(value, StringComparer.Ordinal);
        bool Mode<T>(string value) where T : struct, Enum => Choice(value, Enum.GetNames<T>());
        if (!Choice(h.Cpu, Cpus) || !Choice(h.Gpu, Gpus) || !IsValidNpuName(h.Npu) ||
            !new[] { 0, 4, 8, 16, 24, 32, 48, 64, 96, 128, 256, 512 }.Contains(h.MemoryGiB) ||
            m.Language != Model(m.Language) || m.Embedding != Model(m.Embedding) ||
            s.Prompts is null || s.Prompts.Count > 8 || s.Prompts.Any(p => p is null ||
                !Enum.GetNames<PromptPurpose>().Contains(p.Purpose) || p.Purpose == "Search" || p.CustomCount is < 0 or > 200) ||
            s.Prompts.Select(p => p.Purpose).Distinct().Count() != s.Prompts.Count ||
            s.Routes is null || s.Routes.Count > 32 || s.Routes.Any(r => r is null || r.Count is < 1 or > 64 ||
                !Mode<SourceCodeMode>(r.Input) || !Mode<ChildChunkMode>(r.ChildMode) || !Mode<ParentChunkMode>(r.ParentMode) ||
                !Mode<ParentEmbeddingMode>(r.ParentEmbedding)) || s.Routes.Sum(r => r.Count) > 64) return false;
        return true;
    }
}

/// <summary>A pending batch has no device ID; its local owner and epoch never enter JSON.</summary>
public sealed class UsageStatisticsBatch
{
    internal UsageStatisticsBatch(UsageStatisticsReport report, string? nonce = null, long? createdAt = null)
    {
        Report = report; Json = JsonSerializer.SerializeToUtf8Bytes(report, UsageStatisticsPrivacy.JsonOptions);
        Nonce = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
    internal string Nonce { get; }
    internal long CreatedAt { get; }
    public UsageStatisticsReport Report { get; }
    public byte[] Json { get; }
}

/// <summary>Bounded, aggregate-only, in-memory statistics. No file history is retained or replayed.</summary>
public sealed class UsageStatisticsAccumulator
{
    public const long MaximumSamplesPerExtension = 100_000;
    public const long MaximumDurationMilliseconds = 86_400_000;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageStatisticsProcessing> _counts = new(StringComparer.Ordinal);
    private bool _enabled;
    private UsageStatisticsBatch? _pending;
    public void Configure(UsageStatisticsSettings? settings)
    {
        lock (_gate)
        {
            _enabled = settings is { Enabled: true, NoticeAcknowledged: true };
            if (!_enabled) ClearCore();
        }
    }
    public void Record(string? extension, string? outcome, TimeSpan duration)
    {
        var completed = outcome is "ready" or "indexed" or "completed";
        var failed = outcome == "failed";
        var skipped = outcome is "excluded" or "skipped";
        if (!completed && !failed && !skipped) return;
        if (duration < TimeSpan.Zero) return;
        var ms = (long)Math.Clamp(duration.TotalMilliseconds, 0, MaximumDurationMilliseconds);
        lock (_gate)
        {
            if (!_enabled) return;
            var ext = UsageStatisticsPrivacy.Extension(extension);
            var old = _counts.GetValueOrDefault(ext) ?? new(ext, 0, 0, 0, 0, 0);
            var count = old.Completed + old.Failed + old.Skipped;
            if (count >= MaximumSamplesPerExtension) return;
            var total = old.TotalMilliseconds + ms;
            _counts[ext] = new(ext, old.Completed + (completed ? 1 : 0), old.Failed + (failed ? 1 : 0), old.Skipped + (skipped ? 1 : 0), total, total / (count + 1));
        }
    }
    public UsageStatisticsReport? Preview(AppSettings settings, string version, UsageStatisticsHardwareInput? hardware = null)
    {
        lock (_gate) return !_enabled || settings.Ai.UsesExternalInference() || settings.UsageStatistics is not { Enabled: true, NoticeAcknowledged: true }
            ? null : _pending?.Report ?? Report(settings, version, hardware);
    }
    /// <summary>
    /// Called only for a user's explicit feedback attachment. Never enables automatic collection,
    /// acknowledges a pending batch, or changes its schedule. Opted-out history remains empty.
    /// </summary>
    public UsageStatisticsReport SnapshotForFeedback(AppSettings settings, string version, UsageStatisticsHardwareInput? hardware = null)
    {
        lock (_gate)
        {
            var processing = _enabled && !settings.Ai.UsesExternalInference() && settings.UsageStatistics is { Enabled: true, NoticeAcknowledged: true }
                ? _counts.Values.OrderBy(p => p.Extension, StringComparer.Ordinal).ToArray()
                : [];
            return new(1, UsageStatisticsPrivacy.Version(version), UsageStatisticsPrivacy.Hardware(hardware),
                new(UsageStatisticsPrivacy.Model(settings.Ai.ChatModel), UsageStatisticsPrivacy.Model(settings.Ai.EmbeddingModel)),
                UsageStatisticsPrivacy.Configuration(settings), processing);
        }
    }
    public UsageStatisticsBatch? Capture(AppSettings settings, string version, UsageStatisticsHardwareInput? hardware = null)
    {
        lock (_gate)
        {
            if (!_enabled || settings.Ai.UsesExternalInference() || settings.UsageStatistics is not { Enabled: true, NoticeAcknowledged: true }) return null;
            if (_pending is not null) return _pending;
            var report = Report(settings, version, hardware);
            return report is null ? null : _pending = new(report);
        }
    }
    private UsageStatisticsReport? Report(AppSettings settings, string version, UsageStatisticsHardwareInput? hardware)
    {
        if (!_enabled || settings.UsageStatistics is not { Enabled: true, NoticeAcknowledged: true } || _counts.Count == 0) return null;
        return new(1, UsageStatisticsPrivacy.Version(version), UsageStatisticsPrivacy.Hardware(hardware),
            new(UsageStatisticsPrivacy.Model(settings.Ai.ChatModel), UsageStatisticsPrivacy.Model(settings.Ai.EmbeddingModel)),
            UsageStatisticsPrivacy.Configuration(settings), _counts.Values.OrderBy(p => p.Extension, StringComparer.Ordinal).ToArray());
    }
    public void Acknowledge(UsageStatisticsBatch batch)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, batch)) return;
            foreach (var sent in batch.Report.Processing)
            {
                if (!_counts.TryGetValue(sent.Extension, out var current)) continue;
                var completed = current.Completed - sent.Completed;
                var failed = current.Failed - sent.Failed;
                var skipped = current.Skipped - sent.Skipped;
                var total = current.TotalMilliseconds - sent.TotalMilliseconds;
                var count = completed + failed + skipped;
                if (count <= 0) _counts.Remove(sent.Extension);
                else _counts[sent.Extension] = new(sent.Extension, completed, failed, skipped, total, total / count);
            }
            _pending = null;
        }
    }
    public void Clear() { lock (_gate) ClearCore(); }
    public IReadOnlyList<UsageStatisticsProcessing> GetCounters()
    {
        lock (_gate) return _counts.Values.OrderBy(p => p.Extension, StringComparer.Ordinal).ToArray();
    }
    public bool RestoreCounters(IReadOnlyList<UsageStatisticsProcessing>? counters)
    {
        if (!ValidCounters(counters)) return false;
        lock (_gate)
        {
            if (!_enabled || _pending is not null || _counts.Count != 0) return false;
            foreach (var row in counters!) _counts.Add(row.Extension, row);
            return true;
        }
    }
    internal static bool ValidCounters(IReadOnlyList<UsageStatisticsProcessing>? counters)
    {
        if (counters is null || counters.Count > 48 || counters.Any(c => c is null) || counters.Select(c => c.Extension).Distinct(StringComparer.Ordinal).Count() != counters.Count) return false;
        foreach (var row in counters)
        {
            if (row.Extension != "other" && UsageStatisticsPrivacy.Extension(row.Extension) != row.Extension ||
                row.Completed < 0 || row.Failed < 0 || row.Skipped < 0 || row.Completed > MaximumSamplesPerExtension ||
                row.Failed > MaximumSamplesPerExtension || row.Skipped > MaximumSamplesPerExtension) return false;
            var count = row.Completed + row.Failed + row.Skipped;
            if (count is <= 0 or > MaximumSamplesPerExtension || row.TotalMilliseconds < 0 ||
                row.TotalMilliseconds > count * MaximumDurationMilliseconds || row.AverageMilliseconds != row.TotalMilliseconds / count) return false;
        }
        return true;
    }
    internal UsageStatisticsStoredState Export()
    { lock (_gate) return new(GetCounters(), _pending?.Report, _pending?.Nonce, _pending?.CreatedAt); }
    internal bool Restore(UsageStatisticsStoredState? state)
    {
        if (state is null || !ValidCounters(state.Counters)) return false;
        UsageStatisticsBatch? pending = null;
        if (state.Pending is not null)
        {
            if (!UsageStatisticsPrivacy.ValidReport(state.Pending) || state.Nonce is not { Length: 32 } ||
                state.Nonce.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                state.CreatedAt is null or <= 0 || state.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()) return false;
            foreach (var sent in state.Pending.Processing)
            {
                var current = state.Counters.FirstOrDefault(c => c.Extension == sent.Extension);
                if (current is null || sent.Completed > current.Completed || sent.Failed > current.Failed || sent.Skipped > current.Skipped ||
                    sent.TotalMilliseconds > current.TotalMilliseconds) return false;
            }
            pending = new(state.Pending, state.Nonce, state.CreatedAt);
            if (pending.Json.Length > UsageStatisticsSender.MaximumBodyBytes) return false;
        }
        else if (state.Nonce is not null || state.CreatedAt is not null) return false;
        lock (_gate)
        {
            if (!_enabled || _pending is not null || _counts.Count != 0) return false;
            foreach (var row in state.Counters) _counts.Add(row.Extension, row);
            _pending = pending;
            return true;
        }
    }
    private void ClearCore() { _counts.Clear(); _pending = null; }
}

internal sealed record UsageStatisticsStoredState(IReadOnlyList<UsageStatisticsProcessing> Counters,
    UsageStatisticsReport? Pending, string? Nonce, long? CreatedAt);
