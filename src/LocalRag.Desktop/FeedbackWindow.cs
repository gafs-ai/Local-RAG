using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

internal sealed class FeedbackWindow : Window
{
    private sealed record Category(string Id, string Label);
    private readonly FeedbackSender? _sender;
    private readonly Func<CancellationToken, Task<UsageStatisticsReport>> _snapshot;
    private readonly CancellationTokenSource _lifetime;
    private readonly ComboBox _category = new() { DisplayMemberPath = nameof(Category.Label), Margin = new(0, 4, 0, 12) };
    private readonly TextBox _message = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxLength = FeedbackSender.MaximumMessageCharacters, MinHeight = 140, Margin = new(0, 4, 0, 4)
    };
    private readonly CheckBox _includeUsage = new()
    {
        Content = "利用統計を送信する（バグ報告時には送信をお願いします）", IsChecked = true, Margin = new(0, 12, 0, 6)
    };
    private readonly TextBlock _characters = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly Button _preview = new() { Content = "添付する利用統計を確認", Margin = new(0, 0, 8, 0) };
    private readonly Button _send = new() { Content = "送信する", Margin = new(0, 0, 8, 0), IsDefault = true };
    private readonly Button _close = new() { Content = "閉じる", Margin = new(0) };
    private FeedbackBatch? _pending;
    private Task _operation = Task.CompletedTask;
    private Task _closing = Task.CompletedTask;
    private bool _sent;
    private bool _closingRequested;
    private bool _allowClose;
    private bool _closed;

    public FeedbackWindow(FeedbackSender? sender, Func<CancellationToken, Task<UsageStatisticsReport>> snapshot,
        CancellationToken applicationToken)
    {
        _sender = sender;
        _snapshot = snapshot;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        Title = "Local RAGへフィードバック";
        Width = 760; Height = 740; MinWidth = 640; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _category.ItemsSource = new Category[]
        {
            new("improvement", "改善要望"), new("bug", "バグまたはバグの疑いの報告"), new("other", "その他")
        };
        _category.SelectedIndex = 0;
        AutomationProperties.SetName(_category, "フィードバックの種類");
        AutomationProperties.SetName(_message, "フィードバックの内容");
        AutomationProperties.SetName(_status, "フィードバックの送信状態");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var layout = new Grid { Margin = new(24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var introduction = new StackPanel();
        introduction.Children.Add(new TextBlock { Text = "ご意見・不具合をお知らせください", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        introduction.Children.Add(new TextBlock
        {
            Text = "入力したメッセージは開発者に送られます。個人情報・機密情報は記入しないでください。文書やログを自動で添付することはありません。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 14)
        });
        layout.Children.Add(introduction);
        var categoryPanel = new StackPanel();
        categoryPanel.Children.Add(new Label { Content = "種類", Target = _category, Padding = new(0) });
        categoryPanel.Children.Add(_category);
        Grid.SetRow(categoryPanel, 1); layout.Children.Add(categoryPanel);
        var messagePanel = new DockPanel();
        var messageLabel = new Label { Content = "内容", Target = _message, Padding = new(0) };
        DockPanel.SetDock(messageLabel, Dock.Top); messagePanel.Children.Add(messageLabel);
        DockPanel.SetDock(_characters, Dock.Bottom); messagePanel.Children.Add(_characters);
        messagePanel.Children.Add(_message);
        Grid.SetRow(messagePanel, 2); layout.Children.Add(messagePanel);
        var attachment = new StackPanel();
        attachment.Children.Add(_includeUsage);
        attachment.Children.Add(new TextBlock
        {
            Text = "チェックすると、この報告に機材・モデル・設定の変更有無・収集済みの処理件数と時間を添付します。自動送信がOFFでも今回だけ添付でき、その場合は処理実績を含みません。自動送信の設定は変わりません。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4)
        });
        attachment.Children.Add(new TextBlock
        {
            Text = "本文・ファイル名・パス・プロンプト本文・ユーザー名・端末IDは自動取得しません。NPUはWindowsから取得したデバイス名をそのまま添付します。",
            TextWrapping = TextWrapping.Wrap
        });
        attachment.Children.Add(_status);
        Grid.SetRow(attachment, 3); layout.Children.Add(attachment);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_preview); buttons.Children.Add(_send); buttons.Children.Add(_close);
        Grid.SetRow(buttons, 4); layout.Children.Add(buttons);
        Content = layout;

        _message.TextChanged += (_, _) => DraftChanged();
        _category.SelectionChanged += (_, _) => DraftChanged();
        _includeUsage.Checked += (_, _) => DraftChanged();
        _includeUsage.Unchecked += (_, _) => DraftChanged();
        _preview.Click += (_, _) => StartOperation(PreviewAsync);
        _send.Click += (_, _) => StartOperation(SendAsync);
        _close.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
        };
        DraftChanged();
        if (_sender is null) _status.Text = "このビルドには送信先が設定されていないため、フィードバックを送信できません。";
    }

    private void DraftChanged()
    {
        if (_sent || _closed) return;
        if (_pending is not null) _status.Text = "内容を変更しました。送信すると、新しい報告として登録します。";
        _pending = null;
        _characters.Text = $"{_message.Text.Length:N0} / {FeedbackSender.MaximumMessageCharacters:N0}文字";
        RefreshEnabled();
    }

    private void RefreshEnabled()
    {
        if (_closed) return;
        var editable = _operation.IsCompleted && !_sent && !_closingRequested && !_lifetime.IsCancellationRequested;
        _category.IsEnabled = _message.IsEnabled = _includeUsage.IsEnabled = editable;
        _send.IsEnabled = editable && _sender is not null && !string.IsNullOrWhiteSpace(_message.Text);
        _preview.IsEnabled = editable && _includeUsage.IsChecked == true;
        _close.Content = _operation.IsCompleted ? "閉じる" : "中止して閉じる";
    }

    private void StartOperation(Func<Task> action)
    {
        if (!_operation.IsCompleted || _sent || _closingRequested || _lifetime.IsCancellationRequested) return;
        _operation = RunOperationAsync(action);
        RefreshEnabled();
    }

    private async Task RunOperationAsync(Func<Task> action)
    {
        // Yield once so the task is registered before controls can trigger closing.
        await Task.Yield();
        try { await action(); }
        catch (OperationCanceledException) { if (!_closingRequested) _status.Text = "送信または準備を中止しました。"; }
        catch (ArgumentException) { _status.Text = "種類と内容を確認してください。内容は8,000文字以内で入力します。"; }
        catch { _status.Text = "送信の準備に失敗しました。設定を確認するか、利用統計のチェックを外してお試しください。"; }
        finally
        {
            // Completion is observed after this method returns, so update on the next dispatcher turn.
            if (!_closed) _ = Dispatcher.BeginInvoke(new Action(RefreshEnabled));
        }
    }

    private async Task SendAsync()
    {
        if (_sender is null || _category.SelectedItem is not Category category) return;
        if (_pending is null || _sender.IsExpired(_pending))
        {
            _status.Text = _includeUsage.IsChecked == true ? "添付する利用統計を準備しています…" : "送信を準備しています…";
            var usage = _includeUsage.IsChecked == true ? await _snapshot(_lifetime.Token) : null;
            _lifetime.Token.ThrowIfCancellationRequested();
            _pending = FeedbackBatch.Create(category.Id, _message.Text, usage);
        }
        _status.Text = "フィードバックを送信しています…";
        var accepted = await _sender.SendAsync(_pending, _lifetime.Token);
        if (accepted)
        {
            _sent = true;
            _pending = null;
            _send.Content = "送信済み";
            _status.Text = "フィードバックを送信しました。開発へのご協力ありがとうございます。";
        }
        else _status.Text = "送信完了を確認できませんでした。同じ内容で再送できます。既に受信済みの場合も重複登録しません。この画面を閉じると入力内容は保存されません。";
    }

    private async Task PreviewAsync()
    {
        _status.Text = "利用統計を確認しています…";
        var usage = _pending?.Report.Usage ?? await _snapshot(_lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        var text = JsonSerializer.Serialize(usage, new JsonSerializerOptions(UsageStatisticsPrivacy.JsonOptions) { WriteIndented = true });
        var preview = new Window
        {
            Owner = this, Title = "利用統計のプレビュー", Width = 720, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(16) }
        };
        preview.ShowDialog();
        _status.Text = _pending is null ? "送信時点の利用統計を添付します。" : "再送では、最初の送信時と同じ利用統計を添付します。";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && !_operation.IsCompleted)
        {
            e.Cancel = true;
            _ = RequestCloseAsync();
        }
        base.OnClosing(e);
    }

    public Task RequestCloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (_closingRequested) return _closing;
        _closingRequested = true;
        _lifetime.Cancel();
        _status.Text = "処理を中止しています…";
        foreach (Window child in OwnedWindows.Cast<Window>().ToArray()) child.Close();
        _closing = FinishClosingAsync();
        return _closing;
    }

    private async Task FinishClosingAsync()
    {
        try { await _operation; } catch { }
        if (_closed) return;
        _allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _pending = null;
        _message.Clear();
        _lifetime.Cancel();
        if (_operation.IsCompleted) DisposeResources();
        else _ = DisposeAfterOperationAsync();
        base.OnClosed(e);
    }

    private async Task DisposeAfterOperationAsync()
    {
        try { await _operation.ConfigureAwait(false); } catch { }
        DisposeResources();
    }

    private void DisposeResources()
    {
        _sender?.Dispose();
        _lifetime.Dispose();
    }
}
