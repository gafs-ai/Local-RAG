# Local RAG System

Windows 11 x64向けのローカル文書検索システム。C# / .NET 10、Lemonade Server、別プロセスのSurrealDB、DiskANNを使用します。

Copyright (C) 2026 GAFS Inc. — **GPL-3.0-or-later**

- [設計書](Documents/LocalRAGDesign.md)
- [実装計画・進捗](Documents/ImplementationPlan.md)
- [技術検証結果と既知の制約](Documents/TechnicalValidation.md)

## 現在の実装範囲

**テキストRAGの開発版です。UTF-8の.txt/.mdの取り込み・検索に対応しました。Word・PDF・音声・MCP連携は未実装です。**

実装済み:

- WPF管理画面とトレイ常駐、同じ保存先での二重起動防止。
- フォルダ・監視間隔・AI接続・用途別モデル設定の保存と復元。
- Lemonadeモデル一覧取得、ローカル実行を確認できないモデルの推論拒否、短いチャット接続診断。
- SurrealDBへの処理チェックポイント保存と再読み込み。
- SurrealDB 3.3.5の自動ダウンロード・SHA-256検証・起動。終了時にDBを残すか停止するか選択可能。
- DBポート設定とDPAPIによる認証情報の保存。既存サーバーは終了対象にしません。
- Embedding APIアダプター。Qwen3-Embedding-0.6B-GGUFで1,024次元の実応答とDiskANNとの結合診断に成功。
- フォルダ探索、SHA-256、ローカルLLMによる分類・整形・段落範囲の分割、Embedding、DiskANN検索。
- 各工程とチャンク単位の進捗をDBに保存し、未完了部分から再開。
- 処理完了後15分を既定とする自動監視、一時停止、手動実行。
- 原文と返却候補に対するパターン・実LLM機密検査。拒否・検査失敗時は文書IDだけを返し、本文・パスを返さない。
- ローカル検索・全文取得、分類追加・工程省略・分類別除外・プロンプト編集（詳細設定のJSON）。
- DB・AI診断用CLI、自動テスト。

現在の主要な制約:

1. 対象はUTF-8（BOM可）の.txt/.md。1ファイル256KiB、既定6,000文字・1チャンク1,200文字まで。さらにモデルの実行時入力上限をUTF-8バイト数から保守的に検査するため、実際の上限は低くなり得ます。超過時は保留し、切り捨てません。
2. 整形はLLMが指定した不要段落の除去まで。本文の書き換え、大きな段落の再分割、CP932、Markdown画像の抽出は未対応です。
3. Gemma-3-4b-it-mm-NPU + nomic-embed-text-v2-moe-GGUFで小さな日本語文書の一連処理が成功しました。旧Qwen構成の成功とは別に記録しています。広い検索精度・機密検出精度の保証ではなく、Gemmaでも不正応答の保留・再試行が起こり得ます。
4. 設定保存・不正入力拒否・取り込み・検索・全文取得・監視開始/一時停止・再起動・終了をComputer Useで確認しました。トレイからの再表示はユーザーの協力を受けて確認しました。詳細は技術検証記録を参照してください。
5. MCP等の外部返却エンドポイントはまだ起動しません。検索・取得は管理画面と診断からのローカル処理です。

nomic-embed-text-v2-moeは検索語と文書の専用接頭辞を自動適用します。Lemonadeが8,192を表示していてもモデルの公称512トークンを上限として検査します。現在はUTF-8バイト数に256の余裕分を加える保守的な制限のため、日本語の実用入力長はかなり短くなります。長文対応は後続工程です。同じモデルでも今回の入力形式変更前の索引は再取り込みが必要です。

## 開発

```powershell
git clone https://github.com/gafs-ai/Local-RAG.git
cd Local-RAG
```

.NET SDKは`global.json`で10.0.403を指定しています。NuGetパッケージは作業フォルダの`.packages`に復元されます。Windows x64を対象にビルドしてください。

```powershell
dotnet restore LocalRAG.slnx --locked-mode
dotnet build LocalRAG.slnx --configuration Release --no-restore
dotnet test tests/LocalRag.Tests --configuration Release --no-build --no-restore
```

実DBを使う7件のテストは、検証済みSurrealDB実行ファイルがあるtoolsフォルダを指定して実行します。未指定の場合はその7件をスキップし、DB統合の成功とは扱いません。

```powershell
$env:LOCALRAG_TEST_TOOLS = Join-Path $PWD '.local/tools'
dotnet test tests/LocalRag.Tests -c Release --no-build --logger 'trx;LogFileName=text-rag.trx' --results-directory TestResults
```

フォルダには`surrealdb/3.3.5/surreal.exe`を配置します。後述の`db`診断を実行した場合は、その保存先の`tools`も指定できます。DBテストは一時保存先と空きポートを使います。

## 管理画面

あらかじめLemonade Serverを起動し、使用するローカルモデルを導入してください。現在の検証構成はチャットが`Gemma-3-4b-it-mm-NPU`、Embeddingが`nomic-embed-text-v2-moe-GGUF`、API接続先が`http://localhost:13305/api/v1/`です。

```powershell
dotnet run --project src/LocalRag.Desktop --configuration Release --no-build
```

標準保存先は`%LOCALAPPDATA%\LocalRAG`です。テスト用の保存先を指定できます。

```powershell
dotnet run --project src/LocalRag.Desktop --configuration Release --no-build -- --data-dir .local/manual-test
```

管理画面を閉じるとトレイに残ります。「アプリを終了」またはトレイの「終了」で停止します。フォルダとチャット・Embeddingモデルを設定して保存すると、標準では自動監視を開始します。自動監視をOFFにして保存すると「今すぐ1回実行」で取り込めます。取り込み・検索タブから検索語を入力でき、結果を選んで全文を取得できます。

取り込みと検索のAI呼び出しは直列です。この段階では検索は実行中の取り込み完了を待ちます。設定変更時は処理を中断し、新しい設定で再開します。機密検査を変更すると既存の検索表示も消去します。

起動時にSurrealDBへ接続します。未起動・未導入なら公式3.3.5を専用フォルダへ自動ダウンロードします（初回約307MiB、ネット接続が必要）。標準DBポートは8000です。使用中で接続できない場合は、設定画面で別ポートを保存してアプリを再起動するか、既存DBの認証情報を保存して診断を再試行してください。

「アプリ終了時に、このアプリが起動したSurrealDBも終了する」は標準でONです。OFFを保存するとDBを残します。再起動時は同じ保存先の管理情報から接続と所有権を回復します。独立に起動された既存DBは停止しません。終了に時間がかかる場合も強制終了しません。旧組み込みDBの試験ファイルは自動移行・削除しません。

## 診断

```powershell
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- db .local/db-probe
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- models
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- chat Phi-4-mini-instruct-NPU
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- embed Qwen3-Embedding-0.6B-GGUF .local/embedding-probe .local/tools
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- text-smoke Qwen3-4B-Hybrid Qwen3-Embedding-0.6B-GGUF .local/text-smoke .local/tools
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- text-smoke Gemma-3-4b-it-mm-NPU nomic-embed-text-v2-moe-GGUF .local/gemma-nomic-smoke .local/tools
```

`chat`は選んだローカルモデルに固定の短い接続確認文を送信します。モデルのダウンロードは行いません。モデル一覧にあり、ローカル実行のメタデータを確認できるチャットモデルに限ります。

`db [保存先] [toolsフォルダ]`は検証用ポート18500、`embed <モデルID> [保存先] [toolsフォルダ]`は18501を使います。toolsを省略すると保存先配下に自動導入します。`embed`で保存先を省略した場合はEmbedding応答だけを確認します。モデル自体のダウンロードは行いません。

`text-smoke`は指定先に毎回新しいフォルダを作り、日本語の合成文書3件で一連処理を検証します。ポート18503を使用し、入力・設定・DB・合成入力に対するモデル応答を診断先に保持します。通常のアプリ処理はモデル応答本文をログに記録しません。診断はユーザーの文書を取り込みません。

診断CLIの終了値は0が成功、1が実行失敗/引数不正、2が受入条件の未達です。`db`と`embed`のDB診断は一意な検証用DBを作り、そのDBだけを終了時に削除します。サーバーの永続ファイルは保存先の`server/data`に残します。診断が起動したサーバーは診断終了時に通常終了します。

## プロジェクト

| プロジェクト | 責務 |
|---|---|
| LocalRag.Core | 設定、工程チェックポイント、スケジューラー、返却制御 |
| LocalRag.Infrastructure | 設定ファイル、SurrealDB、Lemonade、診断 |
| LocalRag.Desktop | WPF・トレイ・管理画面 |
| LocalRag.Diagnostics | 開発時の外部依存検証 |
| LocalRag.ServerControl | 所有DBの専用コンソールへの通常終了通知 |
| LocalRag.Tests | 状態保存・監視・機密返却・AI接続の自動テスト |

パッケージのロックファイルは保存対象です。`.local`、`.packages`、`TestResults`、`bin`、`obj`には実行時データが入るため、ソース管理対象にしません。

## ライセンス

本プロジェクトのソースコードと文書の権利者は **GAFS Inc.** です。[GNU General Public License version 3](LICENSE)、または利用者の選択によりそれ以降の版（**GPL-3.0-or-later**）で提供します。適用通知は[COPYRIGHT](COPYRIGHT)を参照してください。

Lemonade Server、SurrealDB、NuGet依存パッケージ、AIモデルには各提供元のライセンス・利用条件が適用されます。このリポジトリにはモデル重み、DBバイナリ、Microsoft Office、ユーザー文書を同梱しません。GPLの指定によって外部ソフトウェアのライセンスが変更されるものではありません。

開発・変更提出の手順は[CONTRIBUTING.md](CONTRIBUTING.md)を参照してください。
