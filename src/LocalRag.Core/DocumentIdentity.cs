using System.Text.Json;

namespace LocalRag.Core;

public static class DocumentIdentity
{
    public static string Key(string sourceName, string path)
    {
        if (!AppSettings.ValidDataSourceName(sourceName)) throw new ArgumentException("データソース名が不正です。", nameof(sourceName));
        return TextRagSettings.Hash(JsonSerializer.Serialize(new[] { "source-path-v1", sourceName, Path.GetFullPath(path).ToUpperInvariant() }));
    }
}
