using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class HelpWindow : Window
{
    public HelpWindow(string topicId = "start")
    {
        InitializeComponent();
        TopicList.ItemsSource = HelpCatalog.Topics;
        SelectTopic(topicId);
    }
    public void SelectTopic(string id)
    {
        FilterBox.Text = "";
        TopicList.SelectedItem = HelpCatalog.Topics.FirstOrDefault(t => t.Id == id) ?? HelpCatalog.Topics[0];
        TopicList.ScrollIntoView(TopicList.SelectedItem);
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (TopicList is null) return;
        var text = FilterBox.Text.Trim();
        TopicList.ItemsSource = HelpCatalog.Topics.Where(t => t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
            t.Body.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        TopicList.SelectedIndex = 0;
    }
    private void TopicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TopicTitle is null) return;
        var topic = TopicList.SelectedItem as HelpTopic;
        TopicTitle.Text = topic?.Title ?? "該当する説明がありません";
        TopicBody.Text = topic?.Body ?? "検索語を短くするか、別の設定名で検索してください。";
        TopicLinks.Children.Clear();
        foreach (var link in topic?.Links ?? [])
        {
            var hyperlink = new Hyperlink(new Run(link.Title)) { NavigateUri = new Uri(link.Url) };
            hyperlink.RequestNavigate += (_, args) =>
            {
                args.Handled = true;
                try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { MessageBox.Show(this, "ブラウザを開けませんでした。" + Environment.NewLine + args.Uri.AbsoluteUri, "Local RAG"); }
            };
            TopicLinks.Children.Add(new TextBlock(hyperlink) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        }
    }
}

internal static class SettingHelp
{
    public static void Attach(Window owner)
    {
        foreach (var topic in HelpCatalog.Topics)
            foreach (var name in topic.Controls)
                if (owner.FindName(name) is FrameworkElement element)
                    element.ToolTip = new TextBlock { Text = topic.Title + "\n" + topic.Body.Split('\n')[0] + "\nF1で詳しい説明", MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
        owner.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F1) return;
            e.Handled = true;
            var current = Keyboard.FocusedElement as DependencyObject;
            HelpTopic? topic = null;
            while (current is not null && current != owner)
            {
                if (current is FrameworkElement element && (topic = HelpCatalog.ForControl(element.Name)) is not null) break;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }
            Show(owner, topic?.Id ?? "start");
        };
    }
    public static void Show(Window owner, string topic = "start")
    {
        var window = owner.OwnedWindows.OfType<HelpWindow>().FirstOrDefault();
        if (window is null) { window = new HelpWindow(topic) { Owner = owner }; window.Show(); }
        else { window.SelectTopic(topic); window.Activate(); }
    }
}
