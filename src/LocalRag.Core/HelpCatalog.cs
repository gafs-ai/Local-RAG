using System.Text.Json;

namespace LocalRag.Core;

public sealed record HelpLink(string Title, string Url);
public sealed record HelpTopic(string Id, string Title, string Body, string[] Controls, HelpLink[]? Links = null);

public static class HelpCatalog
{
    public static IReadOnlyList<HelpTopic> Topics { get; } = Load();
    private static IReadOnlyList<HelpTopic> Load()
    {
        using var stream = typeof(HelpCatalog).Assembly.GetManifestResourceStream("LocalRag.Core.HelpTopics.json")
            ?? throw new InvalidDataException("設定ヘルプが見つかりません。");
        return Array.AsReadOnly(JsonSerializer.Deserialize<HelpTopic[]>(stream) ?? throw new InvalidDataException("設定ヘルプが不正です。"));
    }
    public static HelpTopic? ForControl(string name) => Topics.FirstOrDefault(t => t.Controls.Contains(name, StringComparer.Ordinal));
}
