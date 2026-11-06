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
    private RetrievalPipelineSettings _retrieval;
    public ManagementSettings? Result { get; private set; }
    public TextRagSettings Limits { get; private set; }

    public ManagementWindow(AppSettings settings, int tab)
    {
        InitializeComponent();
        var source = (settings.Management ?? ManagementSettings.FromLegacy(settings.TextPipeline, settings.Confidentiality)).ToOrderedSettings();
        Limits = settings.TextPipeline;
        _retrieval = source.Retrieval;
        _prompts = new(source.Prompts.Select(p => new PromptDraft(p, p.Id.StartsWith("system.", StringComparison.Ordinal))));
        _categories = new(source.Categories.Select(c => new CategoryDraft(c)));
        _routes = new(source.OrderedRoutes().Select(r => new RouteDraft(r)));
        PromptPurposeBox.ItemsSource = new[] { new PurposeChoice(PromptPurpose.Classification, "分類用"), new(PromptPurpose.Format, "フォーマット用"),
            new(PromptPurpose.Chunk, "チャンクの結合用"), new(PromptPurpose.ParentChunk, "チャンクをまとめる用"),
            new(PromptPurpose.CodeDescription, "コード説明用"), new(PromptPurpose.ParentDescription, "文書の親チャンク説明用"), new(PromptPurpose.Confidentiality, "秘匿判定用") };
        ChildModeBox.ItemsSource = new Choice[] { new("Inherit", "共通動作（Markdownは機械的に結合）"), new("Paragraphs", "結合しない：段落単位"),
            new("Mechanical", "機械的に結合：文字数とモデル上限"), new("Llm", "LLMで結合を判断") };
        ParentModeBox.ItemsSource = new Choice[] { new("Inherit", "共通の親チャンク設定"), new("None", "親を作らない"),
            new("Structure", "機械的：Markdownは章、ほかは全文"), new("Llm", "LLMで親へのまとめを判断") };
        ParentEmbeddingBox.ItemsSource = new Choice[] { new("None", "親を埋め込まない"), new("TextWhenFits", "入力上限に収まる親全文だけ埋め込む"),
            new("TextOrSummary", "親全文を埋め込む：長い場合は子の説明から生成") };
        Choice[] inputChoices = [new("Text", "通常のテキスト"), .. SourceLanguages.All.Select(l => new Choice(l.Mode.ToString(), l.Name + "：コード説明を検索"))];
        SourceModeBox.ItemsSource = inputChoices;
        EncodingModeBox.ItemsSource = TextEncodings.All;
        AddSourceModeBox.ItemsSource = inputChoices; AddSourceModeBox.SelectedValue = "Text";
        PromptList.ItemsSource = _prompts;
        CategoryGrid.ItemsSource = _categories;
        RouteList.ItemsSource = _routes;
        DocumentLimitBox.Text = Limits.MaxDocumentCharacters.ToString();
        ChunkLimitBox.Text = Limits.MaxChunkCharacters.ToString();
        OutputLimitBox.Text = Limits.MaxOutputTokens.ToString();
        ParentChunksBox.IsChecked = Limits.ParentChunksEnabled;
        ParagraphChildrenBox.IsChecked = Limits.ParagraphChildren;
        ParentLevelBox.ItemsSource = Enumerable.Range(1, 6).ToArray();
        ParentLevelBox.SelectedItem = Limits.ParentHeadingLevel;
        ConfidentialityBox.IsChecked = _retrieval.ConfidentialityEnabled;
        CodeResultLimitBox.Text = _retrieval.CodeResultMaxCharacters.ToString();
        LoadHierarchySearch();
        PatternsBox.IsChecked = _retrieval.PatternsEnabled;
        LlmBox.IsChecked = _retrieval.LlmEnabled;
        PatternTextBox.Text = string.Join(Environment.NewLine, _retrieval.Patterns);
        RefreshRetrieval(_retrieval.ConfidentialityPromptId);
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
    private sealed class RouteDraft(TextRoute value) : INotifyPropertyChanged
    {
        private TextRoute _value = value;
        public TextRoute Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
        public string Label => $"{Value.Name}（{string.Join(" ", Value.Extensions ?? [])}）";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class RouteCategoryDraft : INotifyPropertyChanged
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public bool Excluded { get; init; }
        private bool _enabled, _active;
        private string _format = Inherit, _chunk = Inherit, _parent = Inherit, _rememberedChunk = "system.chunk";
        public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); } }
        public bool ProcessingActive { get => _active; set { if (_active == value) return; _active = value; Changed(nameof(ProcessingActive)); } }
        public string FormatKey { get => _format; set { _format = value; Changed(nameof(FormatKey)); } }
        public string ChunkKey { get => _chunk; set { _chunk = value; if (value != Inherit && value != Skip) _rememberedChunk = value; Changed(nameof(ChunkKey)); Changed(nameof(JoinKey)); } }
        public string ParentKey { get => _parent; set { _parent = value; Changed(nameof(ParentKey)); } }
        public string JoinKey { get => ChunkKey == Inherit ? Inherit : ChunkKey == Skip ? Skip : "@join";
            set => ChunkKey = value == "@join" ? _rememberedChunk : value; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
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
        if (Selection(ConfidentialityPromptBox) == row.Id || _routes.Any(r =>
            r.Value.ClassifyPromptId == row.Id || r.Value.FormatPromptId == row.Id || r.Value.ChunkPromptId == row.Id ||
            r.Value.ParentPromptId == row.Id || r.Value.CodePromptId == row.Id || r.Value.ParentDescriptionPromptId == row.Id ||
            r.Value.CategoryOverrides.Any(s => s.FormatPromptId == row.Id || s.ChunkPromptId == row.Id || s.ParentPromptId == row.Id)))
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
            Name = RouteNameBox.Text.Trim(), Extensions = RouteExtensionsBox.Text.Split([' ', ',', ';', '\r', '\n', '\t', '、'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList(),
            ClassifyEnabled = ClassifyBox.IsChecked == true, ClassifyPromptId = Selection(ClassifyPromptBox),
            ChildMode = Enum.Parse<ChildChunkMode>(Selection(ChildModeBox)), ParentMode = Enum.Parse<ParentChunkMode>(Selection(ParentModeBox)),
            ParentEmbedding = Enum.Parse<ParentEmbeddingMode>(Selection(ParentEmbeddingBox)), ParentDescriptionPromptId = Selection(ParentDescriptionBox),
            SourceMode = Enum.Parse<SourceCodeMode>(Selection(SourceModeBox)),
            EncodingMode = (TextEncodingMode)EncodingModeBox.SelectedValue,
            MarkdownParentMinCharacters = int.TryParse(MarkdownParentMinBox.Text, out var minimum) ? minimum : -1,
            MarkdownParentMaxCharacters = int.TryParse(MarkdownParentMaxBox.Text, out var maximum) ? maximum : -1,
            ParentPromptId = Selection(DefaultParentBox), CodePromptId = Selection(CodePromptBox),
            DefaultCategoryId = DefaultCategoryBox.SelectedValue as string ?? "",
            FormatPromptId = PromptId(Selection(DefaultFormatBox))!, ChunkPromptId = PromptId(Selection(DefaultChunkBox))!,
            EnabledCategoryIds = _routeRows.Where(r => r.Enabled).Select(r => r.Id).ToList(),
            CategoryOverrides = _routeRows.Where(r => r.FormatKey != Inherit || r.ChunkKey != Inherit || r.ParentKey != Inherit)
                .Select(r => new CategorySteps(r.Id, PromptId(r.FormatKey), PromptId(r.ChunkKey), PromptId(r.ParentKey))).ToList()
        };
    }
    private void PopulateRoute(RouteDraft route)
    {
        _refreshing = true;
        try
        {
            var value = route.Value;
            RouteNameBox.Text = value.Name; RouteExtensionsBox.Text = string.Join(" ", value.Extensions ?? []);
            ChildModeBox.SelectedValue = value.ChildMode.ToString(); ParentModeBox.SelectedValue = value.ParentMode.ToString();
            ParentEmbeddingBox.SelectedValue = value.ParentEmbedding.ToString();
            ParentDescriptionBox.ItemsSource = Choices(PromptPurpose.ParentDescription); ParentDescriptionBox.SelectedValue = value.ParentDescriptionPromptId;
            SourceModeBox.SelectedValue = value.SourceMode.ToString();
            EncodingModeBox.SelectedValue = value.EncodingMode;
            MarkdownParentMinBox.Text = value.MarkdownParentMinCharacters.ToString();
            MarkdownParentMaxBox.Text = value.MarkdownParentMaxCharacters.ToString();
            DefaultParentBox.ItemsSource = Choices(PromptPurpose.ParentChunk); DefaultParentBox.SelectedValue = value.ParentPromptId;
            CodePromptBox.ItemsSource = Choices(PromptPurpose.CodeDescription); CodePromptBox.SelectedValue = value.CodePromptId;
            ClassifyBox.IsChecked = value.ClassifyEnabled;
            ClassifyPromptBox.ItemsSource = Choices(PromptPurpose.Classification); ClassifyPromptBox.SelectedValue = value.ClassifyPromptId;
            DefaultFormatBox.ItemsSource = Choices(PromptPurpose.Format, true); DefaultFormatBox.SelectedValue = Key(value.FormatPromptId);
            DefaultChunkBox.ItemsSource = Choices(PromptPurpose.Chunk, true); DefaultChunkBox.SelectedValue = Key(value.ChunkPromptId);
            DefaultCategoryBox.ItemsSource = _categories.ToArray(); DefaultCategoryBox.SelectedValue = value.DefaultCategoryId;
            FormatColumn.ItemsSource = Choices(PromptPurpose.Format, true, true); ChunkColumn.ItemsSource = Choices(PromptPurpose.Chunk, true, true);
            ParentColumn.ItemsSource = Choices(PromptPurpose.ParentChunk, inherit: true);
            _routeRows = _categories.Select(c =>
            {
                var steps = value.CategoryOverrides.SingleOrDefault(s => s.CategoryId == c.Id);
                return new RouteCategoryDraft { Id = c.Id, Name = c.Name, Excluded = c.Excluded, Enabled = value.EnabledCategoryIds.Contains(c.Id), FormatKey = Key(steps?.FormatPromptId), ChunkKey = Key(steps?.ChunkPromptId), ParentKey = Key(steps?.ParentPromptId) };
            }).ToList();
            foreach (var row in _routeRows) row.PropertyChanged += (_, _) => RefreshUsage();
            _mechanicalPrompt = value.ChunkPromptId.Length == 0 ? "system.chunk" : value.ChunkPromptId;
            MechanicalColumn.ItemsSource = new Choice[] { new(Inherit, "既定を使う"), new("@join", "結合する"), new(Skip, "結合しない") };
            RouteCategoryGrid.ItemsSource = _routeRows;
        }
        finally { _refreshing = false; }
        RefreshUsage();
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
        if (_routes.Count >= 64) { ManagerStatus.Text = "設定は64件以内で指定してください。"; return; }
        var kind = AddSourceModeBox.SelectedValue as string ?? "Text";
        var mode = Enum.Parse<SourceCodeMode>(kind);
        var language = SourceLanguages.Find(mode);
        var entry = new RouteDraft(_routes[^1].Value with { Name = (language?.Name ?? "テキスト") + "用",
            Extensions = language is null ? [".txt"] : [.. language.Extensions], SourceMode = mode });
        _routes.Insert(0, entry); RouteList.SelectedItem = entry;
        ManagerStatus.Text = "最優先の位置へ追加しました。名前・拡張子・処理方法を編集し、上下ボタンで順序を調整できます。";
    }
    private void RemoveRoute(object sender, RoutedEventArgs e)
    {
        if (_selectedRoute is null) return;
        if (_routes.Count == 1) { ManagerStatus.Text = "適用する設定を少なくとも1件残してください。"; return; }
        var row = _selectedRoute;
        _refreshing = true;
        try { _routes.Remove(row); RouteList.SelectedIndex = 0; _selectedRoute = _routes[0]; }
        finally { _refreshing = false; }
        PopulateRoute(_selectedRoute);
        ManagerStatus.Text = "削除しました。残りの設定に一致しない拡張子は処理対象外になります。";
    }
    private void MoveRoute(object sender, RoutedEventArgs e)
    {
        CaptureRoute();
        if (_selectedRoute is null) return;
        var index = _routes.IndexOf(_selectedRoute);
        var next = index + ((sender as Button)?.Tag as string == "up" ? -1 : 1);
        if (next < 0 || next >= _routes.Count) return;
        _refreshing = true;
        try { _routes.Move(index, next); RouteList.SelectedItem = _selectedRoute; }
        finally { _refreshing = false; }
        ManagerStatus.Text = "上の設定が優先されます。拡張子が一致した最初の設定だけを使用します。";
    }
    private void RefreshRetrieval(string secret)
    {
        ConfidentialityPromptBox.ItemsSource = Choices(PromptPurpose.Confidentiality); ConfidentialityPromptBox.SelectedValue = secret;
    }
    private void LoadHierarchySearch()
    {
        HierarchySearchBox.IsChecked = _retrieval.HierarchySearchEnabled;
        HierarchySelfBox.Text = _retrieval.HierarchySelfWeight.ToString();
        HierarchyParentBox.Text = _retrieval.HierarchyParentWeight.ToString();
        HierarchyChildBox.Text = _retrieval.HierarchyChildWeight.ToString();
    }
    private void ResetDefaults(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "ユーザー作成のプロンプトとカテゴリ、すべての取り込み・取得パイプライン設定、秘匿パターン、文字数などの上限を初期状態に戻します。\n\n" +
            "現在の編集内容は破棄され、「保存して適用」を押すと保存済みの設定も置き換わります。秘匿機能はパターン検査ON・AI判定OFFに戻ります。\n\n" +
            "元の文書やフォルダ・AI・DB・MCPの接続設定は削除しません。適用後は再取り込みが必要です。\n\n" +
            "デフォルトに戻しますか？",
            "パイプライン設定の初期化", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        // Reset only the editor draft. The existing Save/Cancel path remains the persistence boundary.
        var defaults = ManagementSettings.CreateDefault();
        _refreshing = true;
        try
        {
            CategoryGrid.CancelEdit(DataGridEditingUnit.Cell); CategoryGrid.CancelEdit(DataGridEditingUnit.Row);
            RouteCategoryGrid.CancelEdit(DataGridEditingUnit.Cell); RouteCategoryGrid.CancelEdit(DataGridEditingUnit.Row);
            _selectedRoute = null;
            _routeRows = [];
            RouteCategoryGrid.ItemsSource = null;
            PromptEditor.DataContext = null;
            _prompts.Clear(); _categories.Clear(); _routes.Clear();
            foreach (var prompt in defaults.Prompts) _prompts.Add(new(prompt, true));
            foreach (var category in defaults.Categories) _categories.Add(new(category));
            foreach (var route in defaults.OrderedRoutes()) _routes.Add(new(route));
            Limits = new();
            _retrieval = defaults.Retrieval;
            DocumentLimitBox.Text = Limits.MaxDocumentCharacters.ToString();
            ChunkLimitBox.Text = Limits.MaxChunkCharacters.ToString();
            OutputLimitBox.Text = Limits.MaxOutputTokens.ToString();
            ParentChunksBox.IsChecked = Limits.ParentChunksEnabled;
            ParagraphChildrenBox.IsChecked = Limits.ParagraphChildren;
            ParentLevelBox.SelectedItem = Limits.ParentHeadingLevel;
            ConfidentialityBox.IsChecked = _retrieval.ConfidentialityEnabled;
            CodeResultLimitBox.Text = _retrieval.CodeResultMaxCharacters.ToString();
            LoadHierarchySearch();
            PatternsBox.IsChecked = _retrieval.PatternsEnabled;
            LlmBox.IsChecked = _retrieval.LlmEnabled;
            PatternTextBox.Text = string.Join(Environment.NewLine, _retrieval.Patterns);
            RefreshRetrieval(_retrieval.ConfidentialityPromptId);
            PromptList.SelectedIndex = 0;
            RouteList.SelectedIndex = 0;
            _selectedRoute = _routes[0];
        }
        finally { _refreshing = false; }
        PopulateRoute(_selectedRoute);
        ManagerStatus.Text = "編集内容をデフォルトに戻しました。「保存して適用」で確定します。画面の「キャンセル」で保存済みの設定を維持できます。";
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || !ReferenceEquals(e.Source, ManagerTabs)) return;
        CommitGrids(); CaptureRoute();
        if (_selectedRoute is not null) PopulateRoute(_selectedRoute);
        RefreshRetrieval(Selection(ConfidentialityPromptBox));
    }
    private void Apply(object sender, RoutedEventArgs e)
    {
        CommitGrids(); CaptureRoute();
        if (!int.TryParse(DocumentLimitBox.Text, out var document) || !int.TryParse(ChunkLimitBox.Text, out var chunk) || !int.TryParse(OutputLimitBox.Text, out var output))
        { ManagerStatus.Text = "文書・チャンク・AI応答の上限を整数で入力してください。"; return; }
        if (!int.TryParse(CodeResultLimitBox.Text, out var codeResultLimit))
        { ManagerStatus.Text = "ソースコード検索の返却上限を整数で入力してください。"; return; }
        if (!double.TryParse(HierarchySelfBox.Text, out var selfWeight) || !double.TryParse(HierarchyParentBox.Text, out var parentWeight) || !double.TryParse(HierarchyChildBox.Text, out var childWeight))
        { ManagerStatus.Text = "構造検索の重みを数値で入力してください。"; return; }
        var management = new ManagementSettings
        {
            UserPrompts = _prompts.Where(p => !p.IsSystem).Select(p => p.Build()).ToList(), Categories = _categories.Select(c => c.Build()).ToList(),
            Version = 2, Routes = _routes.Select(r => r.Value).ToList(),
            Retrieval = _retrieval with { CodeResultMaxCharacters = codeResultLimit, ConfidentialityPromptId = Selection(ConfidentialityPromptBox),
                HierarchySearchEnabled = HierarchySearchBox.IsChecked == true, HierarchySelfWeight = selfWeight,
                HierarchyParentWeight = parentWeight, HierarchyChildWeight = childWeight,
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
