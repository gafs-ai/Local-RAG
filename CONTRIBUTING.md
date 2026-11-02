# 開発への参加

このプロジェクトは開発中です。実装範囲と未完了事項は[実装計画](Documents/ImplementationPlan.md)を参照してください。

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
