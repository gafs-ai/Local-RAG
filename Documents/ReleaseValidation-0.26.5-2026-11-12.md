# 0.26.5 公開前検証（2026-11-12）

## 利用者確認と既存の検証

利用者が実際のExcel・CSVファイルで正常な取り込みを確認し、GitHubへのpushと0.26.5の公開を依頼した。実Excelによる.xls・.xlsm・.xlsbの検証および172件の関連回帰試験は[追加形式の検証](ExcelFormatsValidation-2026-11-12.md)、このPCへの更新は[更新確認](DistributionValidation-0.26.5-excel-formats-2026-11-12.md)を参照。

## 全体回帰試験

実SurrealDBと実Excelを有効化した並列実行では603件中596件成功・6件失敗・1件スキップだった。失敗は試験用DBの接続時のCBOR解析4件、構造検索試験のデータ書き込み2件。Excel・CSVの関連試験に新たな失敗はなかった。ログは `tests/LocalRag.Tests/TestResults/release-0265.trx`。

並列実行の影響を切り分けるため、xUnitの `ParallelizeTestCollections=false` を指定し、同じ全体試験を順次実行した。制限付きユーザー環境でWindowsの資格情報保護（DPAPI）を利用できなかった試行は中断し、通常のユーザー権限で実行し直した。通常利用のアプリやDBを停止・初期化していない。

通常ユーザー権限での順次実行は **602件成功・失敗0・1件スキップ、合計603件（約16分）**。ログは `tests/LocalRag.Tests/TestResults/release-0265-serial-user.trx`。スキップは実Lemonadeの起動・終了を伴う別途有効化が必要な試験1件。実Excel試験は有効にして実行した。

最初の6件の失敗は順次実行では再現しなかった。並列実行時の試験用DBの不安定さは残る課題であり、原因の特定・製品側の修正を行ったという意味ではない。公開前の全体回帰確認は順次実行の成功結果に基づく。

実行条件:

```powershell
$env:LOCALRAG_TEST_TOOLS=(Resolve-Path .local/manager-validation/data/tools).Path
$env:LOCALRAG_TEST_EXCEL='1'
dotnet test tests/LocalRag.Tests -c Release --no-build --no-restore --logger 'trx;LogFileName=release-0265-serial-user.trx' -- xUnit.ParallelizeTestCollections=false
```

xUnitの順次実行の設定は[公式RunSettings仕様](https://xunit.net/docs/config-runsettings)による。テスト結果ファイルや通常利用のデータはGitHub配布物に含めない。
