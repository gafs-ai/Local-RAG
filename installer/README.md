# Local RAG バージョン0.26.7

PC内のExcel・CSV・PDF・Word・テキスト・Markdown・ソースコードを検索するWindows 11 x64向けアプリです。

まずは[クイックスタート](Documents/QuickStart.md)に従って、モデルサーバーとモデルを準備してください。**Lemonade Serverを推奨**します。Intel NPUを使う場合はOpenVINO Model Serverを利用でき、LM Studioにも対応しています。複数サーバーの併用も可能です。Local RAGでは、監視フォルダ、言語モデルと接続先、Embeddingモデルと接続先を設定すれば始められます。.NETランタイムは同梱しています。

- [詳しい設定方法](Documents/SettingsGuide.md) — アプリの「ヘルプ」や各設定項目のF1でも読めます。
- [AI接続先の設定](Documents/AiConnections.md) — Lemonade Server・OpenVINO Model Server・LM Studioを登録し、言語モデルとEmbeddingモデルに別々の接続先を選べます。
- [ChatGPT・MCPからの利用](Documents/MCPConnection.md) — プラグイン単独では動作しません。本体アプリを起動しておきます。
- [利用統計](Documents/UsageStatistics.md) — 初回案内で確認でき、後から設定で停止できます。
- [フィードバック](Documents/Feedback.md) — 画面下部から改善要望・不具合等を送信できます。入力したメッセージはそのまま送られます。
- [プロジェクト・ソースコード](https://github.com/gafs-ai/Local-RAG)

更新・アンインストール前には「アプリを完全終了」を選んでください。アンインストールしても設定・索引・元文書・モデルは残ります。

Copyright (C) 2026 GAFS Inc. [GPL-3.0-or-later](LICENSE)。依存ソフトウェアの一覧と原文は、アプリ上部の「ライセンス」または[ライセンス一覧](Documents/ThirdPartyLicenses.md)から確認できます。

PDFの取り込みには[PDF用の処理設定](Documents/PdfIngestion.md)を追加してください。既存の設定は自動変更しません。画像・図表の情報は不完全になり得るため、詳細は原本で確認してください。

Excel・CSVは「設定」→「テキスト処理」で処理設定を追加してください。`.xlsx`・`.xlsm`は直接読み取り、`.xls`・`.xlsb`は導入済みMicrosoft Excelで変換します。詳しくは[Excel・CSVの取り込み](Documents/SpreadsheetIngestion.md)を参照してください。
