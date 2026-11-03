using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;

namespace LocalRag.Desktop;

public partial class ManagementWindow : Window
{
    private const string Inherit = "@inherit", Skip = "@skip";
    private readonly ObservableCollection<PromptDraft> _prompts;
    private readonly ObservableCollection<CategoryDraft> _categories;
    private readonly ObservableCollection<RouteDraft> _routes;
    private List<RouteCategoryDraft> _routeRows = [];
    private RouteDraft? _selectedRoute;
    private bool _refreshing = true;
    private readonly RetrievalPipelineSettings _retrieval;
    public ManagementSettings? Result { get; private set; }
    public TextRagSettings Limits { get; private set; }

    public ManagementWindow(AppSettings settings, int tab)
    {
        InitializeComponent();
        var source = settings.Management ?? ManagementSettings.FromLegacy(settings.TextPipeline, settings.Confidentiality);
        Limits = settings.TextPipeline;
        _retrieval = source.Retrieval;
        _prompts = new(source.Prompts.Select(p => new PromptDraft(p, p.Id.StartsWith("system.", StringComparison.Ordinal))));
        _categories = new(source.Categories.Select(c => new CategoryDraft(c)));
        _routes = new(new[] { source.DefaultTextRoute }.Concat(source.ExtensionRoutes).Select(r => new RouteDraft(r)));
        PromptPurposeBox.ItemsSource = new[] { new PurposeChoice(PromptPurpose.Classification, "分類用"), new(PromptPurpose.Format, "フォーマット用"),
            new(PromptPurpose.Chunk, "チャンク用"), new(PromptPurpose.Search, "検索用"), new(PromptPurpose.Confidentiality, "秘匿判定用") };
        PromptList.ItemsSource = _prompts;
        CategoryGrid.ItemsSource = _categories;
        RouteList.ItemsSource = _routes;
        ExtensionsBox.Text = string.Join(" ", source.TextExtensions);
        DocumentLimitBox.Text = Limits.MaxDocumentCharacters.ToString();
        ChunkLimitBox.Text = Limits.MaxChunkCharacters.ToString();
        OutputLimitBox.Text = Limits.MaxOutputTokens.ToString();
        ParentChunksBox.IsChecked = Limits.ParentChunksEnabled;
        ParagraphChildrenBox.IsChecked = Limits.ParagraphChildren;
        ParentLevelBox.ItemsSource = Enumerable.Range(1, 6).ToArray();
        ParentLevelBox.SelectedItem = Limits.ParentHeadingLevel;
        ConfidentialityBox.IsChecked = _retrieval.ConfidentialityEnabled;
        PatternsBox.IsChecked = _retrieval.PatternsEnabled;
        LlmBox.IsChecked = _retrieval.LlmEnabled;
        PatternTextBox.Text = string.Join(Environment.NewLine, _retrieval.Patterns);
        RefreshRetrieval(_retrieval.SearchPromptId, _retrieval.ConfidentialityPromptId);
        var nomic = EmbeddingInput.IsNomicV2(settings.Ai.EmbeddingModel);
        SearchPromptPanel.Visibility = nomic ? Visibility.Collapsed : Visibility.Visible;
        SearchPromptNotice.Text = nomic
            ? "nomicはモデル専用の検索形式を使うため、検索プロンプトの設定は不要です。ここでは秘匿機能を設定します。"
            : "検索指示文に対応するEmbeddingモデルでは、選択した検索用プロンプトを使用します。";
        _refreshing = false;
        PromptList.SelectedIndex = 0;
        RouteList.SelectedIndex = 0;
        ManagerTabs.SelectedIndex = tab;
    }

    private sealed record PurposeChoice(PromptPurpose Value, string Label);
    private sealed record Choice(string Key, string Label);
    public sealed class PromptDraft(ManagedPrompt prompt, bool system) : INotifyPropertyChanged
    {
        public string Id { get; } = prompt.Id;
        public bool IsSystem { get; } = system;
        private string _name = prompt.Name;
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public PromptPurpose Purpose { get; set; } = prompt.Purpose;
        public string Text { get; set; } = prompt.Text;
        public ManagedPrompt Build() => new(Id, Name, Purpose, Text);
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class CategoryDraft(ManagedCategory category)
    {
        public string Id { get; } = category.Id;
        public string Name { get; set; } = category.Name;
        public string Description { get; set; } = category.Description;
        public bool Excluded { get; set; } = category.Excluded;
        public ManagedCategory Build() => new(Id, Name, Description, Excluded);
    }
    private sealed class RouteDraft(TextRoute value)
    {
        public TextRoute Value { get; set; } = value;
        public string Label => Value.Extension == "*" ? "共通設定（個別指定なし）" : Value.Extension;
    }
    public sealed class RouteCategoryDraft
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public bool Enabled { get; set; }
        public string FormatKey { get; set; } = Inherit;
        public string ChunkKey { get; set; } = Inherit;
    }
    private List<Choice> Choices(PromptPurpose purpose, bool skip = false, bool inherit = false)
    {
        var choices = new List<Choice>();
        if (inherit) choices.Add(new(Inherit, "既定を使う"));
        if (skip) choices.Add(new(Skip, "省略する"));
        choices.AddRange(_prompts.Where(p => p.Purpose == purpose).Select(p => new Choice(p.Id, p.Name)));
        return choices;
    }
    private static string Key(string? id) => id is null ? Inherit : id.Length == 0 ? Skip : id;
    private static string? PromptId(string key) => key == Inherit ? null : key == Skip ? "" : key;
    private static string Selection(ComboBox box) => box.SelectedValue as string ?? "missing_prompt";
    private void PromptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PromptList.SelectedItem is not PromptDraft selected) { PromptEditor.IsEnabled = false; return; }
        PromptEditor.IsEnabled = true; PromptEditor.DataContext = selected;
        PromptNameBox.IsReadOnly = PromptBodyBox.IsReadOnly = selected.IsSystem;
        PromptPurposeBox.IsEnabled = !selected.IsSystem;
        PromptNotice.Text = selected.IsSystem ? "配布プロンプトは編集できません。「コピー」で自分用のプロンプトを作成できます。" : "自分用のプロンプトです。保存後、パイプラインで選択すると適用されます。";
    }
    private void NewPrompt(object sender, RoutedEventArgs e) => CreatePrompt(false);
    private void CopyPrompt(object sender, RoutedEventArgs e) => CreatePrompt(true);
    private void CreatePrompt(bool copy)
    {
        var selected = PromptList.SelectedItem as PromptDraft;
        if (copy && selected is null) return;
        var row = new PromptDraft(new("user_" + Guid.NewGuid().ToString("N"), copy ? selected!.Name[..Math.Min(90, selected.Name.Length)] + " のコピー" : "新しいプロンプト",
            selected?.Purpose ?? PromptPurpose.Classification, copy ? selected!.Text : ""), false);
        _prompts.Add(row); PromptList.SelectedItem = row; ManagerStatus.Text = "";
    }
    private void DeletePrompt(object sender, RoutedEventArgs e)
    {
        if (PromptList.SelectedItem is not PromptDraft row) return;
        CaptureRoute();
        if (row.IsSystem) { ManagerStatus.Text = "配布プロンプトは削除できません。"; return; }
        if (Selection(SearchPromptBox) == row.Id || Selection(ConfidentialityPromptBox) == row.Id || _routes.Any(r =>
            r.Value.ClassifyPromptId == row.Id || r.Value.FormatPromptId == row.Id || r.Value.ChunkPromptId == row.Id ||
            r.Value.CategoryOverrides.Any(s => s.FormatPromptId == row.Id || s.ChunkPromptId == row.Id)))
        { ManagerStatus.Text = "使用中のプロンプトです。パイプラインの選択を変更してから削除してください。"; return; }
        _prompts.Remove(row); PromptList.SelectedIndex = 0; ManagerStatus.Text = "";
    }
    private void AddCategory(object sender, RoutedEventArgs e) => _categories.Add(new(new("cat_" + Guid.NewGuid().ToString("N"), "新しいカテゴリ")));
    private void DeleteCategory(object sender, RoutedEventArgs e)
    {
        CommitGrids(); CaptureRoute();
        if (CategoryGrid.SelectedItem is not CategoryDraft row) return;
        if (_routes.Any(r => r.Value.DefaultCategoryId == row.Id || r.Value.EnabledCategoryIds.Contains(row.Id) || r.Value.CategoryOverrides.Any(s => s.CategoryId == row.Id)))
        { ManagerStatus.Text = "使用中のカテゴリです。パイプラインで有効カテゴリと個別処理の指定を外してから削除してください。"; return; }
        _categories.Remove(row); ManagerStatus.Text = "";
    }
    private void CommitGrids()
    {
        CategoryGrid.CommitEdit(DataGridEditingUnit.Cell, true); CategoryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RouteCategoryGrid.CommitEdit(DataGridEditingUnit.Cell, true); RouteCategoryGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
    private void CaptureRoute()
    {
        if (_refreshing || _selectedRoute is null) return;
        CommitGrids();
        _selectedRoute.Value = _selectedRoute.Value with
        {
            ClassifyEnabled = ClassifyBox.IsChecked == true, ClassifyPromptId = Selection(ClassifyPromptBox),
            DefaultCategoryId = DefaultCategoryBox.SelectedValue as string ?? "",
            FormatPromptId = PromptId(Selection(DefaultFormatBox))!, ChunkPromptId = PromptId(Selection(DefaultChunkBox))!,
            EnabledCategoryIds = _routeRows.Where(r => r.Enabled).Select(r => r.Id).ToList(),
            CategoryOverrides = _routeRows.Where(r => r.FormatKey != Inherit || r.ChunkKey != Inherit)
                .Select(r => new CategorySteps(r.Id, PromptId(r.FormatKey), PromptId(r.ChunkKey))).ToList()
        };
    }
    private void PopulateRoute(RouteDraft route)
    {
        _refreshing = true;
        try
        {
            var value = route.Value;
            ClassifyBox.IsChecked = value.ClassifyEnabled;
            ClassifyPromptBox.ItemsSource = Choices(PromptPurpose.Classification); ClassifyPromptBox.SelectedValue = value.ClassifyPromptId;
            DefaultFormatBox.ItemsSource = Choices(PromptPurpose.Format, true); DefaultFormatBox.SelectedValue = Key(value.FormatPromptId);
            DefaultChunkBox.ItemsSource = Choices(PromptPurpose.Chunk, true); DefaultChunkBox.SelectedValue = Key(value.ChunkPromptId);
            DefaultCategoryBox.ItemsSource = _categories.ToArray(); DefaultCategoryBox.SelectedValue = value.DefaultCategoryId;
            FormatColumn.ItemsSource = Choices(PromptPurpose.Format, true, true); ChunkColumn.ItemsSource = Choices(PromptPurpose.Chunk, true, true);
            _routeRows = _categories.Select(c =>
            {
                var steps = value.CategoryOverrides.SingleOrDefault(s => s.CategoryId == c.Id);
                return new RouteCategoryDraft { Id = c.Id, Name = c.Name, Enabled = value.EnabledCategoryIds.Contains(c.Id), FormatKey = Key(steps?.FormatPromptId), ChunkKey = Key(steps?.ChunkPromptId) };
            }).ToList();
            RouteCategoryGrid.ItemsSource = _routeRows;
        }
        finally { _refreshing = false; }
    }
    private void RouteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        CaptureRoute(); _selectedRoute = RouteList.SelectedItem as RouteDraft;
        if (_selectedRoute is not null) PopulateRoute(_selectedRoute);
    }
    private void AddRoute(object sender, RoutedEventArgs e)
    {
        CaptureRoute();
        var ext = NewExtensionBox.Text.Trim().ToLowerInvariant();
        if (!ManagementSettings.ValidExtension(ext) || _routes.Any(r => r.Value.Extension == ext))
        { ManagerStatus.Text = "拡張子を .cs のように入力してください。同じ拡張子の設定は追加できません。"; return; }
        var entry = new RouteDraft(_routes[0].Value with { Extension = ext });
        _routes.Add(entry); RouteList.SelectedItem = entry;
        if (!Extensions().Contains(ext)) ExtensionsBox.Text += " " + ext;
        NewExtensionBox.Clear(); ManagerStatus.Text = "共通設定をコピーしました。この拡張子の処理を変更できます。";
    }
    private void RemoveRoute(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null || _selectedRoute.Value.Extension == "*") { ManagerStatus.Text = "共通設定は削除できません。"; return; }
        var row = _selectedRoute; _routes.Remove(row); RouteList.SelectedIndex = 0;
        ManagerStatus.Text = "対象拡張子は維持し、共通設定を使うようにしました。";
    }
    private List<string> Extensions() => ExtensionsBox.Text.Split([' ', ',', ';', '\r', '\n', '\t', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();
    private void RefreshRetrieval(string search, string secret)
    {
        SearchPromptBox.ItemsSource = Choices(PromptPurpose.Search); SearchPromptBox.SelectedValue = search;
        ConfidentialityPromptBox.ItemsSource = Choices(PromptPurpose.Confidentiality); ConfidentialityPromptBox.SelectedValue = secret;
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || !ReferenceEquals(e.Source, ManagerTabs)) return;
        CommitGrids(); CaptureRoute();
        if (_selectedRoute is not null) PopulateRoute(_selectedRoute);
        RefreshRetrieval(Selection(SearchPromptBox), Selection(ConfidentialityPromptBox));
    }
    private void Apply(object sender, RoutedEventArgs e)
    {
        CommitGrids(); CaptureRoute();
        if (!int.TryParse(DocumentLimitBox.Text, out var document) || !int.TryParse(ChunkLimitBox.Text, out var chunk) || !int.TryParse(OutputLimitBox.Text, out var output))
        { ManagerStatus.Text = "文書・チャンク・AI応答の上限を整数で入力してください。"; return; }
        var management = new ManagementSettings
        {
            UserPrompts = _prompts.Where(p => !p.IsSystem).Select(p => p.Build()).ToList(), Categories = _categories.Select(c => c.Build()).ToList(),
            TextExtensions = Extensions(), DefaultTextRoute = _routes[0].Value, ExtensionRoutes = _routes.Skip(1).Select(r => r.Value).ToList(),
            Retrieval = _retrieval with { SearchPromptId = Selection(SearchPromptBox), ConfidentialityPromptId = Selection(ConfidentialityPromptBox),
                ConfidentialityEnabled = ConfidentialityBox.IsChecked == true, PatternsEnabled = PatternsBox.IsChecked == true, LlmEnabled = LlmBox.IsChecked == true,
                Patterns = PatternTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() }
        };
        var limits = Limits with { MaxDocumentCharacters = document, MaxChunkCharacters = chunk, MaxOutputTokens = output,
            ParentChunksEnabled = ParentChunksBox.IsChecked == true, ParagraphChildren = ParagraphChildrenBox.IsChecked == true,
            ParentHeadingLevel = ParentLevelBox.SelectedItem is int level ? level : 2 };
        var errors = management.Validate().Concat(limits.Validate()).ToList();
        if (errors.Count > 0) { ManagerStatus.Text = string.Join("\n", errors.Distinct()); return; }
        Result = management; Limits = limits; DialogResult = true;
    }
}
