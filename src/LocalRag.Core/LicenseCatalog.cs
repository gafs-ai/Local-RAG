using System.Text.Json;

namespace LocalRag.Core;

public sealed record LicenseEntry(string Name, string Version, string License, string Scope, string Description, string Source, string[] Files)
{
    public string DisplayName => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
}

public static class LicenseCatalog
{
    private const string Prefix = "LocalRag.Licenses.";
    public static IReadOnlyList<LicenseEntry> Entries { get; } = Array.AsReadOnly(
        JsonSerializer.Deserialize<LicenseEntry[]>(ReadFile("index.json")) ?? throw new InvalidDataException("ライセンス一覧が不正です。"));

    public static string Text(LicenseEntry entry) => string.Join("\n\n", new[]
    {
        $"{entry.DisplayName}\nライセンス: {entry.License}\n区分: {entry.Scope}\n{entry.Description}\n出典: {entry.Source}"
    }.Concat(entry.Files.Select(file => $"--- {file} ---\n{ReadFile(file)}")));

    private static string ReadFile(string name)
    {
        if (name != Path.GetFileName(name)) throw new InvalidDataException("ライセンスファイル名が不正です。");
        using var stream = typeof(LicenseCatalog).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException($"ライセンスが見つかりません: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
