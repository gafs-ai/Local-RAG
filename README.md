# Local RAG System

> 0.26.4の追加機能：文字主体の[PDF取り込み](Documents/PdfIngestion.md)。既存設定ではPDF用の処理を追加してください。Word・PDFとも画像・図表等の情報は欠落し得るため、重要な内容は原本を確認してください。既存設定は自動変更しません。

Windows 11 x64向けのローカル文書検索システム。C# / .NET 10、Lemonade Server、別プロセスのSurrealDB、DiskANNを使用します。

**初めて使う方は[クイックスタート](Documents/QuickStart.md)から始めてください。** 必要なインストール、メモリ・NPUに合わせたモデル選択、最小限の設定を説明しています。設定の詳細は、アプリ内の「ヘルプ」または各設定項目でF1を押すとオフラインで読めます。

**バージョン0.26.4**（更新検証用・GitHub未公開）。配布物は.NET同梱のWindows用MSIとZIPです。[リリース一覧](https://github.com/gafs-ai/Local-RAG/releases)／[0.26.4リリースノート](Documents/Release-0.26.4.md)／[配布手順](Documents/Distribution.md)。

推奨モデルは、言語モデルが **Gemma**、Embeddingモデルが **nomic** です。Lemonade Serverに用意したモデルを、管理画面の「設定」で選択してください。

Copyright (C) 2026 GAFS Inc. — **GPL-3.0-or-later**。依存ソフトウェアのライセンス原文は、アプリ上部の「ライセンス」からオフラインで確認できます。

- [設計書](Documents/LocalRAGDesign.md)
- [実装計画・進捗](Documents/ImplementationPlan.md)
- [技術検証結果と既知の制約](Documents/TechnicalValidation.md)
- [実文書による初期設定の評価](Documents/PipelineEvaluation-2026-11-04.md)
- [構造検索の実モデル評価と調整](Documents/RetrievalEvaluation-2026-11-06.md)
- [検索クエリの作り方の実測評価](Documents/QueryEvaluation-2026-11-06.md)
- [設定変更と工程単位の再処理](Documents/IncrementalReprocessing.md)
- [MCP・ChatGPT接続手順](Documents/MCPConnection.md)
- [依存ソフトウェアのライセンス](Documents/ThirdPartyLicenses.md)

## 現在の実装範囲

**0.26.4では文字主体のPDF取り込みを追加しています。音声は未対応です。**

PDF・Word・テキスト・Markdown・ソースコードの取り込みと検索に対応しています。音声は後続工程です。`main`はリリース用、`dev`は開発用とします。版番号は「メジャーバージョン.年.年内のリリース回数」です。

実装済み:

- [Word文書の取り込み](Documents/WordIngestion.md)：見出しを保持したMarkdown変換、構造補正、機密ラベル保全。既存設定にはWord用を追加してください。

- WPF管理画面とトレイ常駐、同じ保存先での二重起動防止。
- フォルダ・監視間隔・AI接続・用途別モデル設定の保存と復元。
- Lemonadeモデル一覧取得、ローカル実行を確認できないモデルの推論拒否、短いチャット接続診断。
- SurrealDBへの処理チェックポイント保存と再読み込み。
- SurrealDB 3.3.5の自動ダウンロード・SHA-256検証・起動。終了時にDBを残すか停止するか選択可能。
- DBポート設定とDPAPIによる認証情報の保存。既存サーバーは終了対象にしません。
- Embedding APIアダプター。Qwen3-Embedding-0.6B-GGUFで1,024次元の実応答とDiskANNとの結合診断に成功。
- UTF-8、Shift-JIS、EUC-JP、ISO-2022-JP、中国語・韓国語の従来の文字コード、BOM付きUnicodeの自動判別。ルールごとの文字コード指定、低確度・不正入力の保留。
- フォルダ探索、SHA-256、ローカルLLMによる分類・整形、2段階のチャンク処理、Embedding、DiskANN検索。
- 子の「チャンクの結合」と親の「チャンクをまとめる」を個別設定。Markdownは既定で章立てに従って機械的に処理。
- C#・Python・TypeScript・JavaScript・C++・C・Rust・Java・Kotlinの説明文を埋め込み、説明と原文コードを返却。優先順の設定で対象拡張子と言語を指定。必要な解析環境は初回に自動取得。
- 親子チャンクと[構造検索](Documents/StructuralSearch.md)：初期ヒットの親・子も評価し、総合スコアで返却単位と順位を決定。関連度に応じて章・段落、クラス・関数を選択。取得設定でON/OFFと重みを変更可能。MCPの`fetch`では原文全文を取得し、検索・全文取得とも返却前の秘匿検査を適用。
- 各工程とチャンク単位の進捗をDBに保存し、未完了部分から再開。
- 既定5分の定期チェックと処理待ち行列。フォルダ優先度・更新日時の新しい順で処理し、更新後10分間は既定で対象外。実行中のファイルを中断せず、待ち順を更新。一時停止・手動実行にも対応。
- 「.」で始まるファイル・フォルダを既定で除外（設定で解除可能）。該当フォルダ配下と取り込み済み文書の検索・全文取得にも適用。
- フォルダ別の `.gitignore` / `.dockerignore` / `.npmignore` / `.ragignore` と、管理画面の全体除外パターンに対応。いずれかの `!` 例外を最優先し、ドット始まりの文書も個別に復帰可能。詳しくは[設定ガイド](Documents/SettingsGuide.md)。
- 起動時にLemonade接続とモデル一覧を自動更新。未起動ならインストール済みサーバーを自動起動し、このアプリが起動したものだけを終了する設定に対応（各設定の既定ON）。
- チェック開始時刻と進行状態をDBに保存し、再起動後は開始基準で次回を予定。未完了チェックは待たずに再開。
- 原文と返却候補に対するパターン・実LLM機密検査。拒否・検査失敗時は文書ID・判定・原本パスを返す。本文は返さず、ユーザー自身がパスから原本を開いて確認できる。
- ローカル検索・全文取得、プロンプト・カテゴリ・テキストパイプライン・取得パイプラインの管理画面。JSONの直接編集は不要です。
- DB・AI診断用CLI、自動テスト。
- 認証付きのローカルMCP HTTPとstdio中継。search/fetchを管理画面と同じ機密検査に通し、設定変更・終了時に進行中の呼び出しを停止。

現在の主要な制約:

1. 対象は設定した拡張子のテキスト・ソースコード。文字コードは自動判別または明示指定できます。自動判別には誤判別の可能性があり、短文などは明示指定が必要です。Wordは専用設定でMarkdownへ変換します。PDFは[PDF用の設定](Documents/PdfIngestion.md)でサンプリング判定後にMarkdownへ変換します。新規設定は文書262,144文字・1チャンク1,200文字まで。文書上限は最大1,048,576文字に設定でき、読み取りはその4倍＋4バイトまでです。既存の文書上限は維持するため、旧設定で6,000文字などになっている場合は管理画面で変更してください。
2. 分類・整形・分割・秘匿検査は実行時のコンテキスト上限に合わせて小分けに実行します。大きな段落も文字を失わずコードで分割します。整形は不要段落の除去までで、本文の書き換え、Markdown画像の抽出は未対応です。
3. Gemma-3-4b-it-mm-NPU + nomic-embed-text-v2-moe-GGUFで小さな日本語文書の一連処理が成功しました。旧Qwen構成の成功とは別に記録しています。広い検索精度・機密検出精度の保証ではなく、Gemmaでも不正応答の保留・再試行が起こり得ます。
4. 設定保存・不正入力拒否・取り込み・検索・全文取得・監視開始/一時停止・再起動・終了をComputer Useで確認しました。トレイからの再表示はユーザーの協力を受けて確認しました。詳細は技術検証記録を参照してください。
5. MCPは127.0.0.1限定・認証必須。ローカルクライアントは同梱stdio中継を利用します。ChatGPT Businessにはローカルプラグイン方式を用意し、新しい会話での短い合成文書の取得をユーザーが確認しました。API Platformは必須にしません。機密拒否・検査失敗のChatGPT上の確認、実文書での評価、ブラウザ対応は残っています。

nomic-embed-text-v2-moeは検索語と文書の専用接頭辞を自動適用します。Lemonadeが8,192を表示していても512トークンを上限として検査します。UTF-8バイト数+256という保守的な検査を維持し、文書チャンクを接頭辞込み256バイト以内へ自動分割するため、長文は多くの短いチャンクになります。検索語は引き続き上限以内で入力してください。処理版v5への更新後は再索引します。

2048/4096のコンテキストを再現した送信上限・本文保持のテストを追加しました。現在のGemma/nomic実環境では、以前保留だった説明書とC#コードの取り込みが成功しました。LLM判定ON時には2件ともWithheldでしたが、パターン検査ON・LLM判定OFFでは全文一致と検索取得を確認しました。入力長への対応と、分類・検索・秘匿判定の意味的な精度は区別します。

**LLMによる機密判断は試験的（experimental）で、既定はOFFです。** 秘匿パターンは「先頭・末尾の指定行」と「文書全体」の2系統で設定できます。新規設定では先頭5行・末尾0行の検査がON、全文検査はOFFです。保存済みのパターンは従来の全文検査として引き継ぎます。LLM判定を使う場合は取得処理で有効化してください。パターン検査のみの場合、指定範囲のパターンに一致しない内容の意味的な機密性は検査しません。詳細は[秘匿パターンの検査範囲](Documents/ConfidentialityPatternScopes.md)を参照してください。

## 開発

```powershell
git clone https://github.com/gafs-ai/Local-RAG.git
cd Local-RAG
git switch dev
```

.NET SDKは`global.json`で10.0.403を指定しています。NuGetパッケージは作業フォルダの`.packages`に復元されます。Windows x64を対象にビルドしてください。

```powershell
dotnet restore LocalRAG.slnx --locked-mode
dotnet build LocalRAG.slnx --configuration Release --no-restore
dotnet test tests/LocalRag.Tests --configuration Release --no-build --no-restore
```

実DBを使う12件のテストは、検証済みSurrealDB実行ファイルがあるtoolsフォルダを指定して実行します。未指定の場合はその12件をスキップし、DB統合の成功とは扱いません。

```powershell
$env:LOCALRAG_TEST_TOOLS = Join-Path $PWD '.local/tools'
dotnet test tests/LocalRag.Tests -c Release --no-build --logger 'trx;LogFileName=text-rag.trx' --results-directory TestResults
```

フォルダには`surrealdb/3.3.5/surreal.exe`を配置します。後述の`db`診断を実行した場合は、その保存先の`tools`も指定できます。DBテストは一時保存先と空きポートを使います。

## 管理画面

データソース名は初回に `Local-{Windowsユーザー名}-{ランダムな英数字4文字}` を生成して保存します。漢字名も利用でき、「設定」から変更できます。文書キー・検索・削除・監視・初期化をソースごとに分離するため、別ソースの同じファイルパスは別文書になります。改名は別ソースへの切り替えです。移行とDB共用の対応範囲は[データソースの設計](Documents/DataSources.md)を参照してください。

あらかじめLemonade Serverを起動し、使用するローカルモデルを導入してください。現在の検証構成はチャットが`Gemma-3-4b-it-mm-NPU`、Embeddingが`nomic-embed-text-v2-moe-GGUF`、API接続先が`http://localhost:13305/api/v1/`です。

```powershell
dotnet run --project src/LocalRag.Desktop --configuration Release --no-build
```

標準保存先は`%LOCALAPPDATA%\LocalRAG`です。テスト用の保存先を指定できます。

```powershell
dotnet run --project src/LocalRag.Desktop --configuration Release --no-build -- --data-dir .local/manual-test
```

管理画面を閉じるとトレイに残ります。「アプリを完全終了」またはトレイの「終了」で停止します。「起動時に監視を開始する」は次回起動時の動作を設定します。設定保存時は監視中／停止中を維持し、監視中なら新設定で直ちにファイルを確認します。「取り込み」タブの「監視再開」「監視停止」で切り替え、「今すぐ実行」で監視状態にかかわらずファイル一覧を更新できます。「検索」タブから検索し、選択した文書を取得できます。

取り込みと検索のAI呼び出しは直列です。この段階では検索は実行中の取り込み完了を待ちます。設定変更時は処理を中断し、新しい設定で再開します。機密検査を変更すると既存の検索表示も消去します。

「設定」の上部から4つの管理画面を開けます。

- **プロンプトマネージャー**：配布された標準プロンプトは閲覧専用です。「コピー」または「白紙から作成」で自分用のプロンプトを作り、分類・フォーマット・チャンク・検索・秘匿判定の用途を指定します。作成後はパイプラインで選択して適用します。
- **カテゴリマネージャー**：名前、任意の説明、検索対象外の指定を編集します。追加したカテゴリは各パイプラインで有効にします。
- **テキストパイプライン**：「適用する設定」を優先順に並べ、各設定に拡張子・入力の扱い・分類・チャンク処理を定義します。最初に一致する設定を適用し、共通設定も .txt などの拡張子を明示して下へ配置します。独立した対象拡張子の一覧は使いません。
- **取得パイプライン**：検索・全文取得に共通の秘匿ON/OFF、パターン（1行1件の正規表現）、ローカルAIの使用、秘匿判定用プロンプトを設定します。nomicでは専用の検索形式を使用するため、検索プロンプト選択は表示しません。

「保存して適用」で4画面の編集をまとめて反映します。「キャンセル」は保存しません。使用中のプロンプト・カテゴリは参照を外すまで削除できません。詳しい操作と移行の注意は[設定ガイド](Documents/SettingsGuide.md)を参照してください。旧設定は読込時に引き継ぎ、保存時に形式2へ移行します。旧版アプリでは形式2を開けないため、戻す予定がある場合は更新前の設定を保管してください。設定変更は文書ごとの工程情報で比較し、影響する工程だけを再処理します。Embeddingモデル変更では既存の説明等を再利用します。旧データに工程情報がなく旧設定とも一致しない場合は初回だけ再構築します。検索・秘匿設定だけの変更は再索引不要です。

取り込みの保留時は、失敗した工程と内容を含まない理由を文書一覧に表示します。単一段落の分割は結果が一意なためコードで処理します。複数段落の分割プロンプトには実際の段落ID・文字数上限に合う応答例を付けます。保存済みの旧標準プロンプトは実行時に補正し、独自に編集したプロンプトは維持します。長文処理版（v5）への更新後は旧版の索引を再取り込みします。

構造化応答では、整数文字列の段落ID（`"1"`→`1`）と削除IDの重複を、意味を変えずにコードで補正します。未定義の分類、存在しない段落、欠落・重複する範囲、過剰な削除、スキーマ不正は検証理由を添えてLLMへ1回差し戻します。整形・分割が再試行後も不正な場合はその応答を破棄し、原文を保持する整形省略／コードによる分割で継続し、文書一覧に代替処理を表示します。分類失敗は保留し、機密検査の失敗・拒否・保留を許可へ変更しません。

自動監視は処理完了を待たず、既定5分の間隔でチェックします。フォルダ優先度（0～100、大きいほど先）、最終更新日時の新しい順で待ち行列を更新し、実行中のファイルは中断しません。フォルダが重複する場合は高い優先度を採用して1回だけ処理します。

更新除外期間は既定10分です。チェック時点から10分以上前に更新されたファイルだけを候補とし、待機中の再更新も処理開始前に再確認します。0分で保護を無効化できます。手動の「今すぐ実行」にも適用されます。既存設定に保存済みの監視間隔は維持します。

同じ設定で再起動した場合は前回チェック開始から残りの間隔を待ち、次のチェックで待ち行列を再構築します。ファイルの工程チェックポイントは再利用します。チェック中断・期限超過・対象や優先度/保護期間の変更・旧監視形式からの移行時は直ちにチェックします。間隔のみの変更は前回チェック開始を基準に計算します。スリープ等で逃したチェックはまとめて1回だけ実行します。「今すぐ実行」は監視中・取り込み中でも使用でき、実行中のファイルを中断せず、待ち行列を更新します。「監視再開」と監視中の設定保存後には直ちに探索します。監視状態の保存・復元に失敗した場合は自動監視を停止して表示します。

起動時にSurrealDBへ接続します。自動起動がON（既定）で、ローカルDBが未起動・未導入なら公式3.3.5を専用フォルダへ自動ダウンロードします（初回約307MiB）。ローカルDBポートは8000で、変更は再起動後に反映します。外部DBを使う場合は接続先（ws:// または wss://）と認証情報を設定します。外部接続先を指定するとローカルDBは起動せず、外部DBも終了しません。詳細は[設定ガイド](Documents/SettingsGuide.md)を参照してください。

「アプリ終了時に、このアプリが起動したSurrealDBも終了する」は標準でONです。OFFを保存するとDBを残します。再起動時は同じ保存先の管理情報から接続と所有権を回復します。独立に起動された既存DBは停止しません。終了に時間がかかる場合も強制終了しません。旧組み込みDBの試験ファイルは自動移行・削除しません。

## 診断

管理画面で用意した試験専用設定を実モデルで検証する場合は、アプリを終了してから `dotnet run --project src/LocalRag.Diagnostics -c Release --no-build -- configured-scan <試験用保存先> <toolsフォルダ>` を実行できます。保存済み設定・明示した監視フォルダ・自動監視OFFが必要です。入力を短縮せず取り込み、内容を含まない工程別結果を保存先の `configured-scan-report.json` に記録します。通常運用の保存先と同時利用しないでください。

`configured-evaluate <試験用保存先> <検索語> <toolsフォルダ>`では、取り込み後に準備済み文書の全文取得と1件の検索も実行します。秘匿設定は保存されたものをそのまま使い、非開示を無理に許可へ変えません。終了値0でも取得がAllowedとは限らないため、レポートの判定を確認してください。

```powershell
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- db .local/db-probe
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- models
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- chat Phi-4-mini-instruct-NPU
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- embed Qwen3-Embedding-0.6B-GGUF .local/embedding-probe .local/tools
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- text-smoke Qwen3-4B-Hybrid Qwen3-Embedding-0.6B-GGUF .local/text-smoke .local/tools
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- text-smoke Gemma-3-4b-it-mm-NPU nomic-embed-text-v2-moe-GGUF .local/gemma-nomic-smoke .local/tools
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- chunk-probe Gemma-3-4b-it-mm-NPU .local/chunk-probe
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- chunk-repair-probe Gemma-3-4b-it-mm-NPU .local/chunk-repair-probe
dotnet run --project src/LocalRag.Diagnostics --configuration Release --no-build -- chunk-smoke Gemma-3-4b-it-mm-NPU nomic-embed-text-v2-moe-GGUF .local/chunk-smoke .local/tools
```

`chat`は選んだローカルモデルに固定の短い接続確認文を送信します。モデルのダウンロードは行いません。モデル一覧にあり、ローカル実行のメタデータを確認できるチャットモデルに限ります。

`db [保存先] [toolsフォルダ]`は検証用ポート18500、`embed <モデルID> [保存先] [toolsフォルダ]`は18501を使います。toolsを省略すると保存先配下に自動導入します。`embed`で保存先を省略した場合はEmbedding応答だけを確認します。モデル自体のダウンロードは行いません。

`text-smoke`は指定先に毎回新しいフォルダを作り、日本語の合成文書3件で一連処理を検証します。ポート18503を使用し、入力・設定・DB・合成入力に対するモデル応答を診断先に保持します。通常のアプリ処理はモデル応答本文をログに記録しません。診断はユーザーの文書を取り込みません。

`text-smoke`・`chunk-smoke`・`mcp-smoke`は試験的なLLM機密判定を明示的にONにして検証する診断です。通常の新規設定の既定OFFとは区別してください。`configured-scan`・`configured-evaluate`は保存されたON/OFFをそのまま使用します。

`chunk-probe`は固定の合成入力3例（段落IDが1、3、1と3）で分割を検証し、モデルを呼んだ場合のプロンプト・入力・応答を専用フォルダへ保存します。単一段落の2例では呼び出し0回となります。`chunk-repair-probe`は複数段落の初回応答へ意図的に不正な終端を注入し、実モデルへ検証理由を差し戻して再生成を確認します。注入した応答は実モデルの失敗とは区別して記録します。両診断ともDBは使いません。

`chunk-smoke`は単一段落の回帰文書を加えた4件を、保存済み旧標準プロンプト相当の設定で取り込み、全文取得・検索・機密検査を検証します。ポート18503は`text-smoke`と共用のため、両診断を同時実行しないでください。

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
