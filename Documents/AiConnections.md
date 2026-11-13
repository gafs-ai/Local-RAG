# AIモデルサーバーの選び方・接続設定（0.26.7）

Lemonade Server、OpenVINO Model Server（OVMS）、LM Studioを接続先として登録し、言語モデルとEmbeddingモデルにそれぞれ接続先を指定する。同じサーバーを両方に使うことも、別のサーバーを使うこともできる。

## サーバーを選ぶ

| 選択肢 | 使い方 |
| --- | --- |
| **Lemonade Server（推奨）** | 初めて使う場合の出発点。従来の設定を引き継げる |
| **OpenVINO Model Server** | IntelのNPUを使いたい場合に利用できる。対応ドライバーとOpenVINO形式の対応モデルを準備する |
| **LM Studio** | LM Studioで用意した言語モデル・Embeddingモデルを利用できる |

複数のサーバーを併用してよい。例えば、Intel NPUで動かす言語モデルをOpenVINOに、EmbeddingモデルをLemonadeやLM Studioに割り当てる。同じ種類のサーバーを複数登録することもできる。モデルのダウンロードやCPU・GPU・NPUの選択は、それぞれのサーバー側で行う。

## サーバーを準備する

### Lemonade Server

[クイックスタート](QuickStart.md)に従ってインストールし、言語モデルとEmbeddingモデルをダウンロードする。初期設定の接続先は `http://localhost:13305/api/v1/`。既存のLemonade設定はそのまま利用できる。

### Intel NPUを使う：OpenVINO Model Server

1. [OpenVINO Model Serverの公式案内](https://docs.openvino.ai/2026/model-server/ovms_what_is_openvino_model_server.html)からサーバーを準備する。
2. 対応するIntel NPUドライバーと、NPUで動作するモデルを準備する。モデルサーバー側で使用デバイスをNPUに設定する。[デバイスとドライバーの案内](https://docs.openvino.ai/2026/model-server/ovms_docs_target_devices.html)を参照する。
3. サーバーのREST APIを有効にして起動し、そのURLをLocal RAGへ登録する。例：`http://localhost:8001/v3/`。8001はLocal RAGの既定DBポート8000との競合を避けた例。

モデルごとに対応デバイス・形状・入力長の制約がある。Local RAGへ接続先を追加するだけでモデルが自動的にNPU用へ変換されるわけではない。Intel NPUでの本アプリとの組み合わせは実機未検証。0.26.7ではDocker版OVMSのCPU実行で取り込み・検索を確認している。

### LM Studio

1. [LM Studio](https://lmstudio.ai/)をインストールし、使いたいモデルをダウンロードする。
2. LM StudioのDeveloper画面で「Start server」を有効にする。[公式のサーバー起動手順](https://lmstudio.ai/docs/developer/core/server)を参照する。
3. Local RAGで種類をLM Studio、URLを通常は `http://localhost:1234/v1/` として登録する。

## Local RAGへ登録する

1. 「設定」→「AI接続先」の「追加」を押し、名前・サーバーの種類・URLを入力する。認証が必要な場合だけAPIキーを入力する。
2. 必要に応じて自動起動・終了連動を選ぶ。まずはサーバー側で起動を確認した状態で接続できる。
3. 「言語モデルの接続先」と「Embeddingモデルの接続先」を選ぶ。
4. 「接続状態」→「接続確認・モデル一覧更新」で接続を確認し、「設定」で使用するモデルをそれぞれ選んで保存する。

併用する場合は接続先を複数追加し、手順3で別々の接続先を選ぶ。別の接続先・Embeddingモデルへ変更すると、対応する取り込み工程・埋め込みを再処理する。複数のEmbeddingモデルのベクトルを混在させて検索する機能ではない。

## 接続とモデル

接続先には名前、種類、APIのベースURL、必要に応じてAPIキーを設定する。例はLemonadeの`http://localhost:13305/api/v1/`、OVMSの`http://localhost:8001/v3/`、LM Studioの`http://localhost:1234/v1/`。外部サーバーのHTTP/HTTPS URLも指定できる。OVMSの例は既定のSurrealDBポート8000との競合を避けている。

外部接続を選ぶと、分類・整形・説明生成・埋め込みのために文書内容や検索文が指定先へ送られる。取得結果の秘匿検査は、これらの入力送信を防止する機能ではない。自動リダイレクトは追跡しない。APIキーはWindowsのユーザー単位の暗号化で保存し、接続先の種類・URLに関連付ける。URLを変更した場合に以前のキーを別の接続先へ転送しない。

実行中のモデルの入力上限を取得できる場合は利用する。取得できない場合に備えて、接続先に言語モデル・Embeddingモデルの入力上限を指定できるようにする。モデルの理論上限を実行時上限と同一視しない。JSON応答は引き続きコードで検証する。

LM Studioで未ロードのモデルを読み込む場合、明示した入力上限をロード要求にも渡す。ロード済みのモデルは再ロードせず、実行時上限と設定値の小さい方を使う。

新規のOVMS接続はJSON形式の強制指定を既定でOFFにする。OVMS 2026.4.1とQwen3 0.6Bで、項目未指定の汎用JSONスキーマを指定すると空のオブジェクトになる事例を実機確認したため。プロンプトによるJSON要求とコード側の形式・内容検証は常に行う。保存済みの明示設定は維持するため、同様の症状が出る場合は接続先編集でJSON指定をOFFにする。

Docker版OVMSは、コンテナ側でモデルとポート公開を設定して起動し、API URLを登録する。Local RAGのOVMS自動起動はWindows版の`ovms.exe`用で、Dockerコンテナの起動終了は対象外。CPUを指定する場合はコンテナ側で`--target_device CPU`を使う。

## 起動・終了

接続先ごとに自動起動と連鎖終了を選ぶ。ローカルURLだけを起動管理の対象にする。外部URLでは接続確認のみを行い、サーバー側で起動・終了を管理する。接続失敗をすべて未起動とみなさず、認証失敗・TLSエラー・タイムアウト・使用中ポートでは重複起動しない。

本アプリが起動したサービスだけを終了する。接続先やポートを変更しても既存プロセスを無条件に終了・再利用しない。既に起動していたサーバーは終了対象にしない。OVMSは実行ファイルとモデル用設定ファイル、LM Studioはlms CLIを使用する。

LM Studioの連鎖終了は今回開始したHTTPサーバーの停止までとし、共有のGUI／デーモンは残す。別ポートで起動済みのLM Studioは設定変更しない。OVMSは専用の非表示コンソールから起動し、所有するプロセスへ通常終了通知を送る。GPU/NPUの選択とモデル変換・配置は各サーバーで行う。

## 移行と再処理

従来の単一Lemonade設定を既定接続先として表示し、モデル選択・起動設定を引き継ぐ。表示名・起動終了設定・APIキーの変更だけでは索引を作り直さない。

複数接続を保存した設定はSchemaVersion=3とする。旧アプリが新しい接続先指定を無視して旧URLへ送信することを避け、旧版では読み込みを拒否する。MCP中継はMCP接続設定だけを読むため継続利用できる。

APIキー変更時には秘密情報と無関係なランダムな変更番号を更新し、失敗の再試行判定だけに反映する。認証失敗の保留は解除するが、成功済みの取り込み結果の工程識別子やベクトル空間は変えない。

言語モデル側の接続先変更はLLM工程、Embedding側の変更は埋め込み工程に反映する。Embeddingのサーバー種別・URL・モデルを検索空間の識別に含め、別条件のベクトルを混在させない。従来Lemonade設定を複数接続形式へ移行しただけでは既存の工程識別子・検索空間を変えない。

同一URL・同一モデルIDのままサーバー側でモデル実体を差し替えたことは自動検出できない。その場合はEmbeddingRevisionを更新するか、取り込みデータを初期化して再作成する。

利用統計にはURL・APIキー・接続先の表示名・外部サーバーの機材情報を送信しない。外部推論であれば、このPCの機材情報を推論サーバーの性能情報と解釈できないため、自動の処理速度統計の対象外とする。

## 検証範囲

API・設定移行・工程の再利用・起動終了の所有権は自動試験で確認する。Intel NPUとNVIDIA GPUでの実モデル検証は、それぞれの対応機材が必要。接続APIの実装とハードウェア上の動作確認を区別する。

参考: [OVMS](https://docs.openvino.ai/2026/model-server/ovms_docs_clients_genai.html)、[LM Studio API](https://lmstudio.ai/docs/developer/openai-compat)、[LM Studioモデル情報](https://lmstudio.ai/docs/developer/rest/list)、[lms起動](https://lmstudio.ai/docs/cli/serve/server-start)。
