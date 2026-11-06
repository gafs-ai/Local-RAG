using System.Xml.Linq;
using LocalRag.Core;

namespace LocalRag.Tests;

public sealed class HelpCatalogTests
{
    [Fact]
    public void CatalogHasUniqueTopicsAndControlMappingsAndSafeLinks()
    {
        var topics = HelpCatalog.Topics;
        Assert.NotEmpty(topics);
        Assert.Equal(topics.Count, topics.Select(t => t.Id).Distinct().Count());
        var controls = topics.SelectMany(t => t.Controls).ToArray();
        Assert.Equal(controls.Length, controls.Distinct().Count());
        foreach (var topic in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Body));
            foreach (var link in topic.Links ?? []) Assert.Equal("https", new Uri(link.Url).Scheme);
        }
        Assert.Null(HelpCatalog.ForControl("unknown-control"));
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("ManagementWindow.xaml")]
    public void EveryNamedEditableSettingHasHelp(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LocalRAG.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var doc = XDocument.Load(Path.Combine(root.FullName, "src", "LocalRag.Desktop", file));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editable = new[] { "TextBox", "ComboBox", "CheckBox", "PasswordBox", "DataGrid" };
        foreach (var element in doc.Descendants().Where(e => editable.Contains(e.Name.LocalName)))
        {
            var name = (string?)element.Attribute(x + "Name");
            if (name is null || (string?)element.Attribute("IsReadOnly") == "True") continue;
            Assert.True(HelpCatalog.ForControl(name) is not null, $"Missing help for {file}: {name}");
        }
    }
}
