# 開発とリリース

通常の開発は `dev` ブランチで行います。`main` はリリース時のソースだけを保存します。変更は `dev` にまとめ、リリース時にテストと配布物の検証を行って `main` に反映し、そのコミットに版番号のタグを付けます。タグの上書きは行いません。

版はルートの `VERSION` で指定します。形式は `{メジャーバージョン.年.年内のリリース回数}`、例は `0.26.01` です。表示・タグと、先頭ゼロを除いた.NET/MSI/プラグイン用の数値表現を区別します。

ビルドとテストは[README](README.md)、配布物の作成は[配布手順](Documents/Distribution.md)を参照してください。公開用パッケージはコミット済みのクリーンなソースから作成します。

依存ソフトウェアを更新した場合は、その版のライセンス原文と表示情報を `ThirdPartyNotices` に反映し、`scripts/Update-LicenseCatalog.ps1` で一覧を再生成します。`scripts/Test-LicenseInventory.ps1` と回帰テストで、実際の依存関係との一致を確認してください。ランタイム、自動取得する解析ツール・DB、インストーラの依存物も対象です。

## 開発環境

Windows 11 x64と`global.json`で指定した.NET SDKを使用します。

```powershell
dotnet restore LocalRAG.slnx --locked-mode --configfile NuGet.Config
dotnet build LocalRAG.slnx -c Release --no-restore
dotnet test tests/LocalRag.Tests -c Release --no-build --no-restore
```

実DBテストには`LOCALRAG_TEST_TOOLS`を設定します。詳細は[README](README.md)を参照してください。実DBテストのスキップや、合成データ・テストダブルの成功を実モデルの受入成功として扱わないでください。

## 変更の提出

- 変更の目的、利用者から見た挙動、検証結果、未確認の範囲を説明してください。
- 文書本文、AI応答、パスワード、APIキー、実行時DB、個人の設定ファイルをコミットしないでください。試験は合成データと専用保存先で行ってください。
- 読み取り・保存・再開・機密返却に関わる変更には、失敗時の挙動も含めて適切なテストを行ってください。
- 機密検査のエラーを許可扱いにしたり、入力超過を無通知で切り捨てたりしないでください。
- 依存関係の変更時にはpackages.lock.jsonと必要な説明も更新してください。

投稿する変更は、このプロジェクトのGPL-3.0-or-laterライセンスで提供できるものに限ります。第三者のコードを追加するときは、そのライセンスと出典を明記してください。
