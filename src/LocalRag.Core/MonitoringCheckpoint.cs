// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace LocalRag.Core;

public sealed record MonitoringCheckpoint(string Scope, bool Running, DateTimeOffset? LastCompletedUtc, int SchemaVersion = 1)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || Scope is null || Scope.Length != 64 || Scope.Any(c => !char.IsAsciiHexDigit(c)) ||
            (!Running && LastCompletedUtc is null))
            throw new InvalidDataException("監視の保存状態が不正です。");
    }

    public static string ScopeFor(AppSettings settings) => TextRagSettings.Hash(JsonSerializer.Serialize(new
    {
        pipeline = settings.PipelineConfigurationId(),
        folders = settings.Folders.Select(f => new
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)).ToUpperInvariant(), f.Recursive
        }).OrderBy(f => f.path, StringComparer.Ordinal).ToArray()
    }));

    public TimeSpan Remaining(string scope, DateTimeOffset now, TimeSpan interval)
    {
        Validate();
        if (scope != Scope || Running) return TimeSpan.Zero;
        var elapsed = now - LastCompletedUtc!.Value;
        // A clock correction must not postpone a scan for longer than a full interval.
        if (elapsed < TimeSpan.Zero) return interval;
        return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
    }
}

public interface IMonitoringCheckpointStore
{
    Task<MonitoringCheckpoint?> LoadMonitoringAsync(CancellationToken token);
    Task SaveMonitoringAsync(MonitoringCheckpoint checkpoint, CancellationToken token);
}

public sealed class MonitoringPersistenceException(Exception inner)
    : IOException("監視状態を保存・復元できないため自動監視を停止しました。DB接続を確認してください。", inner);
