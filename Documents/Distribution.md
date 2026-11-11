# 配布物の作成と検証

利用者向けの案内は[クイックスタート](QuickStart.md)です。この文書は配布担当者向けです。現在の版は0.26.4です。`main`をリリース用、`dev`を開発用とします。公開用パッケージはmainのコミット済みソースから作成し、検証後に同じコミットへ版番号のタグを付けます。

## 配布内容

- Windows 11 x64用のMSI。ユーザー単位で `%LOCALAPPDATA%\Programs\LocalRAG` へインストールし、スタートメニューへ登録します。PCの管理ポリシーによっては管理者の許可が必要です。
- .NET 10の実行環境を同梱し、別途.NETをインストールする手順を不要にします。ZIPによる展開実行も可能です。
- Lemonade Serverとモデルは別途準備します。SurrealDBは本体の既存機能で初回起動時に取得します。
- 文書・設定・認証情報・DB・モデル・開発用の評価データは同梱しません。利用者向け文書とライセンスを同梱します。
- 更新・アンインストール前に、Local RAGを「アプリを完全終了」で終了してください。「×」では常駐が続きます。ユーザーの設定・索引・元文書・モデルはアンインストールでは削除しません。標準データ保存先はインストール先とは別の `%LOCALAPPDATA%\LocalRAG` です。

## 作成

Windows上で.NET SDK（`global.json`指定版）、PowerShell 7、WiX Toolset 5.0.2を使用します。日本語UI拡張も5.0.2に固定します。配布用ランタイムは検証・ライセンス確認済みの.NET 10.0.14に`Package.ps1`で固定し、SDKのパッチ更新による暗黙の切り替えを防ぎます。ランタイムを更新する場合は動作確認と第三者通知の更新を併せて行ってください。

```powershell
New-Item -ItemType Directory -Force .local | Out-Null
Push-Location .local
& 'C:/Program Files/WiX Toolset v5.0/bin/wix.exe' extension add WixToolset.UI.wixext/5.0.2
Pop-Location
./scripts/Package.ps1 # VERSIONから0.26.4を取得
# 表示された配布フォルダを指定
./scripts/Build-Installer.ps1 -DistributionDirectory '<配布フォルダ>'
./scripts/Package-Plugin.ps1 -DistributionDirectory '<配布フォルダ>'
```

WiXの場所を変更した場合は `-WixPath`、UI拡張は `-UiExtension` で指定できます。MSIとプラグインZIPにSHA-256ファイルを生成します。配布用のビルド出力は `.local/package-build` へ分離します。

MSI作成の初回にはWiX 5.0.2の固定コミットの対応ソースを取得し、SHA-256を照合します。MSI内のWiX UIコードに対応するソースとMS-RLライセンスは、インストール先の `ThirdPartyNotices` に同梱します。

表示・Gitタグ・配布物名・MSI・.NET・プラグインのバージョンは0.26.4です。版はルートの`VERSION`で指定します。ソースが未コミットなら内部評価用とし、対応ソースZIPは作成しません。クリーンなソースからは同じコミットの対応ソースZIPを生成します。依存関係とライセンス一覧の一致もパッケージ作成時に検査します。

## プラグイン

パッケージの説明に「単独では動作しない」旨とGitHubのクイックスタートURLを含めます。ローカル実行のstdio中継は.NET同梱の実行ファイルを使用します。既存ホスト向けのDesktop形式と、Portable形式は区別しています。

パッケージ作成は、インストール済みプラグインやChatGPT上の登録内容を自動更新しません。公開前にGitHubへガイドを反映し、利用ホストの更新手順で新しいパッケージを読み込ませ、説明リンクとツール接続を確認します。

## 公開前の確認

自動テスト、MSIのインストール・更新・削除、アプリ起動、ヘルプ表示を確認します。`Build-Installer.ps1 -TestPackage` は通常版と異なる製品名・UpgradeCodeを持つ専用パッケージを作り、実利用のインストールを更新せず検証できます。

生成直後のMSIはコード署名されていません。公開時の署名・SmartScreen表示と、.NET未導入のクリーンなWindows 11 PCでの初回導入、Lemonade／モデル導入を含む確認は別途必要です。GPLの対応ソースと依存ライセンスも併せて用意します。

WiXの仕様は[公式UIガイド](https://docs.firegiant.com/wix/tools/wixext/wixui/)を参照してください。
