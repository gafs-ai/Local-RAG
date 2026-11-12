namespace LocalRag.Core;

/// <summary>A user-authored message with an explicitly selected, optional usage snapshot.</summary>
public sealed record FeedbackReport(int SchemaVersion, string AppVersion, string Category, string Message,
    UsageStatisticsReport? Usage);
