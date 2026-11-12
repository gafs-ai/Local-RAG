using System.Windows;
using System.Windows.Controls;

namespace LocalRag.Desktop;

internal sealed class UsageStatisticsNoticeWindow : Window
{
    private readonly CheckBox _enabled = new() { IsChecked = true, Content = "開発のために利用統計を送信する", Margin = new(0, 16, 0, 16) };
    public bool StatisticsEnabled { get; private set; }

    public UsageStatisticsNoticeWindow()
    {
        Title = "Local RAGの利用統計について";
        Width = 620; SizeToContent = SizeToContent.Height; MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = "Local RAGの開発・改善にご協力ください", FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Margin = new(0, 16, 0, 8), TextWrapping = TextWrapping.Wrap,
            Text = "機器の種類、使用モデル、アプリのバージョン、拡張子別の処理件数・平均時間、プロンプトやパイプラインの変更有無を、開発のために送信します。環境・モデルと処理時間を組み合わせて集計します。"
        });
        body.Children.Add(new TextBlock
        {
            Margin = new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
            Text = "処理した文書の本文、ファイル名、パス、フォルダ設定、氏名、Windowsユーザー名、端末ID、プロンプト本文は自動取得しません。CPU・GPU・AIモデルは既知の種類や「その他」にまとめます。NPUはWindowsから取得したデバイス名をそのまま送信します。"
        });
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "通信先には接続元IPが技術的に伝わりますが、統計データには記録しません。機器構成などの組み合わせが珍しい場合があり、完全な匿名性を保証するものではありません。"
        });
        body.Children.Add(_enabled);
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "希望しない場合はチェックを外してください。後から「設定」でもOFFにできます。OFFにすると収集・送信を停止し、未送信分を削除します。この案内を閉じた場合もOFFになります。"
        });
        var continueButton = new Button { Content = "この設定で続ける", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0), Padding = new(16, 8, 16, 8) };
        continueButton.Click += (_, _) => { StatisticsEnabled = _enabled.IsChecked == true; DialogResult = true; };
        body.Children.Add(continueButton);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
