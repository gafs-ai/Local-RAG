# 0.26.7 AI接続の実機試験（2026-11-13）

通常利用のアプリ・設定・索引を使わず、診断プログラムから製品のAI接続・取り込み・検索・MCP実装を呼び出した。専用フォルダ、合成文書、専用SurrealDBで実施。Computer Use・画面操作・通常アプリの起動は行っていない。

## 機材とモデル

- CPU: AMD Ryzen AI 5 PRO 340、内蔵GPU: Radeon 840M。
- LM Studio: 0.4.28.0、CLI commit `43d4b26`。既に起動していたAPIを使用。
- LM Studioの言語モデル: 許可を得て取得した `Qwen/Qwen2.5-0.5B-Instruct-GGUF`（Q8_0、約676 MB）。入力上限4096。
- LM StudioのEmbedding: 導入済み `text-embedding-nomic-embed-text-v1.5`（Q4_K_M）、実行時入力上限2048、768次元。
- OVMS: `openvino/model_server:latest`、Model Server 2026.4.1.cfc9ac721、OpenVINO backend 2026.4.1。イメージIDは `sha256:148e3fe802261ed0b5b634de3bbccdefe7176a218ec34a88e382b40704c34a1c`。
- OVMSの言語モデル: `OpenVINO/Qwen3-0.6B-int4-ov`、試験用ID `qwen3-small`。
- OVMSのEmbedding: `OpenVINO/bge-base-en-v1.5-fp16-ov`、試験用ID `bge-test`、768次元。
- OVMSはDockerの専用コンテナ2個で `--target_device CPU` を明示。ホストの待受は127.0.0.1の18001／18002だけ。CPU数・メモリに上限を設定した。

このAMD CPU上でOVMSの実推論が動作した。Intel NPU・Intel GPU・AMD GPUでのOVMS推論を確認したものではない。LM Studioには最初のロード時にGPU最大使用を要求したが、この試験ではデバイス使用率を計測していない。

## 結果

| 言語処理 | 埋め込み | 結果 | 全体所要時間 | 取り込み＋MCP検査 |
| --- | --- | --- | ---: | ---: |
| LM Studio / Qwen2.5 | LM Studio / nomic | 成功 | 33.41秒 | 31.14秒 |
| OVMS / Qwen3 | LM Studio / nomic | 成功 | 15.05秒 | 14.50秒 |
| OVMS / Qwen3 | OVMS / BGE | 成功 | 12.78秒 | 12.35秒 |
| LM Studio / Qwen2.5 | OVMS / BGE | 成功 | 23.65秒 | 21.48秒 |

各構成で以下を確認した。

1. 製品のクライアントによるモデル一覧・JSON生成・2件のEmbedding。
2. 庭の水やり、鉄道旅行、架空の機密文書の3件を分類・整形・チャンク化・埋め込み。全件ready、failed=0。
3. MCP stdio中継→認証付きHTTP経由で `search` と `fetch` が利用可能。
4. 日本語の水やり質問で、想定した `guide.txt` が第1位。
5. 公開文書の全文取得、機密文書の本文を返さず原本パスを返すこと。秘匿は全文パターンの試験設定で検査し、実験的LLM判定はOFF。
6. 取得前に原本を書き換えた場合、古い索引内容の全文を返さないこと。

最後の構成では、既存サーバーをアプリ所有としないこと、終了連動をONにして終了処理を呼んでも既存サーバーを停止しないことも確認した。検証用Qwenだけを一度アンロードし、製品のロード処理後にLM Studioが返す実行時入力上限が4096であることを確認した。

所要時間は初回ロード・キャッシュ・異なるモデルの影響を含む小規模な疎通試験で、サーバーやGPUの性能順位を示すベンチマークではない。Qwen2.5は3件ともspecification、Qwen3は3件ともgeneralに分類した。処理完了と検索疎通の成功は、分類精度や実文書の検索品質を保証しない。BGEの試験モデルも英語向けであり、日本語の一般的な検索品質を推奨する根拠にはしない。

## 発見した問題と修正

### OVMSの汎用JSONスキーマ

`{"type":"object"}`だけのスキーマを指定すると、要求した `{"status":"ok"}` に対して空の `{}` が返った。項目を明示したスキーマなら正しく返り、スキーマ指定なしでもJSONを含む応答を取得できた。単純な接続失敗やAMD CPUでの実行不能ではない。

新規OVMS接続のJSON指定を既定でOFFに変更。プロンプトによる形式要求と、コード側でのJSON修復・形式・内容の検証は維持する。保存済みの設定は自動変更しない。現在のモデル呼出しインターフェースは工程別の詳細スキーマを渡さないため、詳細スキーマの適用は今後の改善。

### LM Studioのロード時入力上限

入力予算の制限だけでなく、未ロードのモデルをロードするAPIにも明示した `context_length` を渡すようにした。0（自動取得）なら指定を省略し、ロード済みモデルを強制的に再ロードすることはない。[公式ロードAPI](https://lmstudio.ai/docs/developer/rest/load)の指定に従う。

関連する設定移行・認証キー保護・API・サーバー管理・ヘルプの試験は最終版で **71件成功、失敗0**。最初の制限環境での実行ではWindowsのユーザー暗号化に関する1件が実行環境由来で失敗し、通常ユーザー環境で再実行して成功した。最終結果は `tests/LocalRag.Tests/TestResults/ai-live-final.trx`。

検証用Desktopのビルドは警告0・エラー0。修正は `.local/ai-live-20261113/desktop-build/` に出力し、通常のインストール先には適用していない。

## 隔離と後片付け

- 設定・合成文書・DB・試験ログは `.local/ai-live-20261113/` 以下。診断コマンドは `endpoint-smoke <AI-connections.json> <new-run-directory> [tools-directory]`。既存の実行ディレクトリは受け付けず、毎回新しい合成文書を生成する。
- 新しい診断設定では監視と利用統計をOFF。通常利用の文書は読み取らず、通常の索引には接続しない。
- 通常の設定ファイルは試験前後でSHA-256が一致。通常アプリは終了した状態を維持。
- 専用SurrealDBを終了し、ポート18503が閉じたことを確認。
- 検証用OVMSコンテナ `localrag-0267-ovms-chat` / `localrag-0267-ovms-embed` は停止。再現用に停止済みコンテナと取得済みモデルを保持。
- 検証用Qwenはメモリから解除。元から起動していたLM Studioサーバーとnomicモデルは維持。ダウンロード済みQwenは再検証用にLM Studioへ残した。
- 試験結果は各 `*-run-01/endpoint-smoke-report.json`、JSON比較は `ovms-json-comparison.json`、後片付け結果は `cleanup.json` に保存。

Windows版OVMSの自動起動・終了、既存LM Studioを止めた状態からの自動起動、認証必須の実リモートサーバー、管理画面、長文・Word・PDF・Excelの実機試験は今回の対象外。Docker版OVMSの起動停止はLocal RAGのWindows版実行ファイルの起動設定には含まれない。正式リリース・GitHubへのpush・MSIの更新は行っていない。

参考: [OVMS埋め込みモデルの例](https://github.com/openvinotoolkit/model_server/blob/main/demos/embeddings/README.md)、[OVMSの構造化応答](https://docs.openvino.ai/nightly/model-server/ovms_structured_output.html)。
