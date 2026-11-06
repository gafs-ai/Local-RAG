using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Tests;

public sealed class LicenseCatalogTests
{
    [Fact]
    public void EveryLicenseHasOfflineTextAndSourceAndUniqueIdentity()
    {
        var entries = LicenseCatalog.Entries;
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(e => e.DisplayName).Distinct().Count());
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.License));
            Assert.Equal("https", new Uri(entry.Source).Scheme);
            Assert.NotEmpty(entry.Files);
            Assert.True(LicenseCatalog.Text(entry).Length > 300, entry.DisplayName);
        }
        Assert.Contains(entries, e => e.Name == "SurrealDB Server" && e.License == "Business Source License 1.1");
        Assert.Contains(entries, e => e.Name == "SurrealDb.Net" && e.License == "Apache-2.0");
    }

    [Fact]
    public void InventoryCoversResolvedNugetPackagesAndVersionMatchesReleaseFile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LocalRAG.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Equal(File.ReadAllText(Path.Combine(root.FullName, "VERSION")).Trim(), ProductInfo.Version);
        Assert.Equal("バージョン" + ProductInfo.Version, ProductInfo.VersionLabel);
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "src/LocalRag.Desktop/obj/project.assets.json")));
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            var parts = library.Name.Split('/');
            Assert.Contains(LicenseCatalog.Entries, e => e.Name == parts[0] && e.Version == parts[1]);
        }
    }
}
