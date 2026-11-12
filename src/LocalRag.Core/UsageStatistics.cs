namespace LocalRag.Core;

/// <summary>Collection and transmission require the notice to have been shown first.</summary>
public sealed record UsageStatisticsSettings
{
    public bool Enabled { get; init; } = true;
    public bool NoticeAcknowledged { get; init; }
}

// No source/document/user/installation identifiers or free-form settings are part of this contract.
public sealed record UsageStatisticsHardware(string Cpu, string Gpu, string Npu, int MemoryGiB);
public sealed record UsageStatisticsModels(string Language, string Embedding);
public sealed record UsageStatisticsPromptChange(string Purpose, int CustomCount, bool Changed);
public sealed record UsageStatisticsRoute(string Input, string ChildMode, string ParentMode,
    string ParentEmbedding, bool Classify, bool Format, bool CustomPrompts, int Count);
public sealed record UsageStatisticsConfiguration(bool PipelineChanged, bool CategoriesChanged,
    bool RetrievalChanged, IReadOnlyList<UsageStatisticsPromptChange> Prompts, IReadOnlyList<UsageStatisticsRoute> Routes);
public sealed record UsageStatisticsProcessing(string Extension, long Completed, long Failed, long Skipped,
    long TotalMilliseconds, long AverageMilliseconds);
public sealed record UsageStatisticsReport(int SchemaVersion, string AppVersion, UsageStatisticsHardware Hardware,
    UsageStatisticsModels Models, UsageStatisticsConfiguration Settings, IReadOnlyList<UsageStatisticsProcessing> Processing);
