using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class LicensesWindow : Window
{
    public LicensesWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = LicenseCatalog.Entries;
        EntryList.SelectedIndex = 0;
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (EntryList is null) return;
        var query = FilterBox.Text.Trim();
        EntryList.ItemsSource = LicenseCatalog.Entries.Where(entry =>
            (entry.DisplayName + " " + entry.License + " " + entry.Scope).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        EntryList.SelectedIndex = 0;
    }
    private void EntryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LicenseText is null) return;
        LicenseText.Text = EntryList.SelectedItem is LicenseEntry entry ? LicenseCatalog.Text(entry) : "該当するソフトウェアがありません。";
        LicenseText.ScrollToHome();
    }
}
