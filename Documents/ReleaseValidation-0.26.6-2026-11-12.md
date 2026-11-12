# 0.26.6 公開前検証（2026-11-12）

## 変更範囲と確認方法

利用統計、機材情報の取得、手動フィードバックを追加した。初回案内とオプトアウト、定期送信の24時間制限、モデル・処理設定の変更時の集計分離、任意の統計添付、NPU名称と未搭載・取得失敗の区別を確認した。

公開対象に受信サイトの構築ソース・ホスティング設定・管理用秘密情報を含めないことを確認。配布アプリ用の送信設定は外部ファイルからビルド時に組み込み、設定ファイル自体をGitへ登録しない。配布版の送信用鍵は解析可能であり、改造されていないアプリを証明する仕組みではない。

設定画面の説明を、CPU・GPU・AIモデルは既知の名称に分類し、NPUはWindowsから取得した名称をそのまま扱う仕様に合わせて修正した。利用者向けの版表記・ダウンロードリンク・同梱文書を更新し、文書リンクと依存ライセンス34件の照合を確認した。

## 全体回帰試験

**707件成功・失敗0件・1件スキップ、合計708件（14分26秒）**。ログは `tests/LocalRag.Tests/TestResults/release-0266-serial-user.trx`。スキップは実Lemonadeの起動・終了を伴う別途有効化が必要な試験1件。

通常のWindowsユーザー権限で、試験専用の一時DBと合成文書を使い、順次実行した。通常利用の文書・索引・設定は操作しない。前版で並列実行時に試験用DBの接続が不安定になったため、同じ順次実行条件を使用する。

```powershell
$env:LOCALRAG_TEST_TOOLS=(Resolve-Path .local/manager-validation/data/tools).Path
$env:LOCALRAG_TEST_EXCEL='1'
$env:LOCALRAG_TEST_LEMONADE=$null
dotnet test tests/LocalRag.Tests -c Release --logger 'trx;LogFileName=release-0266-serial-user.trx' -- xUnit.ParallelizeTestCollections=false
```

実Excelの試験を有効にし、実Lemonadeの起動・終了を伴う試験は有効にしていない。AI応答や統計・報告の通信は試験用の応答を使用する。

## 既に実施した関連検証

- 統計・報告・機材取得の関連104件が成功。詳細は[利用統計の検証](UsageStatisticsValidation-2026-11-12.md)。
- 受信側の関連53件、型検査、ビルドが成功。実際の送信クライアントと合成報告による受信・重複防止・アクセス制限は[フィードバックの検証](FeedbackValidation-2026-11-12.md)を参照。
- 同版の内部評価用MSIをこのPCに更新インストールし、設定の維持、配置ファイルの一致、起動とMCPの未認証要求への拒否を確認した。[更新確認](DistributionValidation-0.26.6-2026-11-12.md)。

今回はComputer Useによる画面操作を行っていない。新しい画面の見た目、本人のログイン状態でのフィードバック一覧・詳細画面は自動試験と区別する。公開用配布物は、この検証を含むコミットをmainへ取り込んだ後、未コミット変更のないソースから作成する。
