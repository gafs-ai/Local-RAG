using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class ManagementWindow
{
    private bool _updatingUsage;
    private string _mechanicalPrompt = "system.chunk";
    private void UsageChanged(object sender, RoutedEventArgs e) => RefreshUsage();
    private static Visibility Visible(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void MechanicalJoinChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _updatingUsage) return;
        if (MechanicalJoinBox.IsChecked == true) DefaultChunkBox.SelectedValue = _mechanicalPrompt;
        else
        {
            if (Selection(DefaultChunkBox) != Skip) _mechanicalPrompt = Selection(DefaultChunkBox);
            DefaultChunkBox.SelectedValue = Skip;
        }
        RefreshUsage();
    }

    private void RefreshUsage()
    {
        if (_refreshing || _updatingUsage || _selectedRoute is null) return;
        _updatingUsage = true;
        try
        {
            if (!Enum.TryParse<SourceCodeMode>(Selection(SourceModeBox), out var source) ||
                !Enum.TryParse<ChildChunkMode>(Selection(ChildModeBox), out var child) ||
                !Enum.TryParse<ParentChunkMode>(Selection(ParentModeBox), out var parent) ||
                !Enum.TryParse<ParentEmbeddingMode>(Selection(ParentEmbeddingBox), out var embedding)) return;
            var route = _selectedRoute.Value with { SourceMode = source, ChildMode = child, ParentMode = parent, ParentEmbedding = embedding,
                Extensions = RouteExtensionsBox.Text.Split([' ', ',', ';', '\r', '\n', '\t', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
            var usage = TextRouteUsage.For(route, Limits with { ParentChunksEnabled = ParentChunksBox.IsChecked == true,
                ParagraphChildren = ParagraphChildrenBox.IsChecked == true });
            var classify = ClassifyBox.IsChecked == true;
            var category = DefaultCategoryBox.SelectedValue as string;
            // A category chosen while classification is off must remain eligible when saved.
            if (!classify && _routeRows.SingleOrDefault(r => r.Id == category) is { } selected) selected.Enabled = true;
            foreach (var row in _routeRows) row.ProcessingActive = !row.Excluded && (classify ? row.Enabled : row.Id == category);
            var active = _routeRows.Where(r => r.ProcessingActive).ToArray();
            var process = active.Length > 0;
            CodeOptions.Visibility = Visible(!usage.Text && process);
            ClassifyOptions.Visibility = Visible(classify);
            TextOptions.Visibility = Visible(usage.Text && process);
            DefaultFormatOptions.Visibility = Visible(active.Any(r => r.FormatKey == Inherit));
            ChildPromptOptions.Visibility = Visible(usage.LlmChildren && active.Any(r => r.ChunkKey == Inherit));
            MechanicalJoinBox.Visibility = Visible(usage.MechanicalChildren && !usage.LlmChildren && active.Any(r => r.ChunkKey == Inherit));
            MechanicalJoinBox.IsChecked = Selection(DefaultChunkBox) != Skip;
            MarkdownBoundsOptions.Visibility = Visible(usage.Markdown && usage.StructureParents);
            ParentPromptOptions.Visibility = Visible(usage.LlmParents && active.Any(r => r.ParentKey == Inherit));
            ParentEmbeddingOptions.Visibility = Visible(usage.ParentEmbedding);
            ParentDescriptionOptions.Visibility = Visible(usage.ParentDescription);
            EnabledColumn.Visibility = Visible(classify);
            FormatColumn.Visibility = Visible(usage.Text && process);
            ChunkColumn.Visibility = Visible(usage.LlmChildren && process);
            MechanicalColumn.Visibility = Visible(usage.MechanicalChildren && !usage.LlmChildren && process);
            ParentColumn.Visibility = Visible(usage.LlmParents && process);
            CategoryOptionsTitle.Text = classify ? "有効カテゴリとカテゴリ別の処理" : "選択したカテゴリの処理";
            var shown = _routeRows.Where(r => classify || r.Id == category).ToArray();
            if (RouteCategoryGrid.ItemsSource is not IEnumerable<RouteCategoryDraft> current || !current.SequenceEqual(shown))
                RouteCategoryGrid.ItemsSource = shown;
            ParentChunksBox.Visibility = Visible(usage.CommonParents);
            ParagraphChildrenBox.Visibility = Visible(usage.CommonChildren);
            // Heading boundaries also constrain child grouping and LLM parent batches.
            HeadingOptions.Visibility = Visible(usage.Markdown);
            CommonOptions.Visibility = Visible(process && (usage.CommonParents || usage.CommonChildren || usage.Markdown));
            ChunkLimitOptions.Visibility = Visible(process);
            var format = active.Any(r => (r.FormatKey == Inherit ? Selection(DefaultFormatBox) : r.FormatKey) != Skip);
            var merge = active.Any(r => (r.ChunkKey == Inherit ? Selection(DefaultChunkBox) : r.ChunkKey) != Skip);
            OutputLimitOptions.Visibility = Visible(classify || process && (!usage.Text || format || usage.LlmChildren && merge || usage.LlmParents || usage.ParentDescription));
        }
        finally { _updatingUsage = false; }
    }
}
