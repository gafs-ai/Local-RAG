using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class ManagementWindow
{
    private ObservableCollection<SpreadsheetOverrideDraft> _spreadsheetOverrides = [];
    private void InitializeSpreadsheetOptions()
    {
        Choice[] modes = [new("Automatic", "自動判定"), new("Records", "レコード一覧"), new("NumericTable", "数値表"),
            new("Document", "文章"), new("Form", "帳票（項目と値）"), new("Raw", "位置付きテキスト"), new("Skip", "取り込まない")];
        SpreadsheetModeBox.ItemsSource = modes;
        SpreadsheetOverrideModeColumn.ItemsSource = modes;
        CsvDelimiterBox.ItemsSource = new Choice[] { new("auto", "自動判別"), new(",", "コンマ"), new(";", "セミコロン"), new("\t", "タブ"), new("|", "縦棒") };
    }
    private void PopulateSpreadsheet(SpreadsheetSettings settings)
    {
        SpreadsheetModeBox.SelectedValue = settings.Mode.ToString();
        SpreadsheetHeaderBox.Text = settings.HeaderRows.ToString();
        CsvDelimiterBox.SelectedValue = settings.CsvDelimiter;
        SpreadsheetHiddenBox.IsChecked = settings.IncludeHidden;
        SpreadsheetCellLimitBox.Text = settings.MaxCells.ToString();
        _spreadsheetOverrides = new(settings.Overrides.Select(o => new SpreadsheetOverrideDraft(o)));
        SpreadsheetOverrideGrid.ItemsSource = _spreadsheetOverrides;
    }
    private SpreadsheetSettings CaptureSpreadsheet() => new() {
        Mode = Enum.Parse<SpreadsheetMode>(Selection(SpreadsheetModeBox)),
        HeaderRows = int.TryParse(SpreadsheetHeaderBox.Text, out var header) ? header : -2,
        CsvDelimiter = Selection(CsvDelimiterBox), IncludeHidden = SpreadsheetHiddenBox.IsChecked == true,
        MaxCells = int.TryParse(SpreadsheetCellLimitBox.Text, out var maximum) ? maximum : -1,
        Overrides = _spreadsheetOverrides.Select(o => o.Build()).ToList() };
    private void AddSpreadsheetOverride(object sender, RoutedEventArgs e)
    {
        var item = new SpreadsheetOverrideDraft(new() { Sheet = "*" });
        _spreadsheetOverrides.Add(item); SpreadsheetOverrideGrid.SelectedItem = item;
    }
    private void RemoveSpreadsheetOverride(object sender, RoutedEventArgs e)
    {
        if (SpreadsheetOverrideGrid.SelectedItem is SpreadsheetOverrideDraft selected) _spreadsheetOverrides.Remove(selected);
    }
    public sealed class SpreadsheetOverrideDraft(SpreadsheetOverride value) : INotifyPropertyChanged
    {
        public string Sheet { get; set; } = value.Sheet;
        public string Range { get; set; } = value.Range;
        private string _mode = value.Mode.ToString();
        public string Mode { get => _mode; set { _mode = value; PropertyChanged?.Invoke(this, new(nameof(HeaderVisibility))); } }
        public Visibility HeaderVisibility => Mode is "Automatic" or "Records" or "NumericTable" ? Visibility.Visible : Visibility.Collapsed;
        public string HeaderRows { get; set; } = value.HeaderRows.ToString();
        public event PropertyChangedEventHandler? PropertyChanged;
        public SpreadsheetOverride Build() => new() { Sheet = Sheet.Trim(), Range = Range.Trim(),
            Mode = Enum.TryParse<SpreadsheetMode>(Mode, out var mode) ? mode : (SpreadsheetMode)(-1),
            HeaderRows = int.TryParse(HeaderRows, out var count) ? count : -2 };
    }
}
