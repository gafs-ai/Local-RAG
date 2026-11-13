# Local RAG 0.26.7

Lemonade Serverに加え、OpenVINO Model ServerとLM Studioへの接続に対応しました。複数のモデルサーバーを登録し、言語モデルとEmbeddingモデルに別々の接続先を指定できます。

## モデルサーバーの選択

- **Lemonade Serverを推奨**します。初めての方は[クイックスタート](QuickStart.md)から始めてください。
- **IntelのNPUを使用したい場合はOpenVINO Model Serverを利用できます。** 対応ドライバー・モデルとサーバー側の設定が必要です。
- **LM Studioも利用できます。** モデルを用意し、APIサーバーを有効にしてください。
- **複数サーバーを併用できます。** 例えば、言語モデルをOpenVINO、EmbeddingモデルをLemonadeに割り当てられます。

具体的な準備・設定方法は[AIモデルサーバーの設定ガイド](AiConnections.md)を参照してください。

## 主な変更

- 接続先ごとのモデル一覧、自動起動・終了連動、入力上限設定。
- 外部HTTP/HTTPSサーバーとAPIキーへの対応。APIキーをWindowsユーザー単位で暗号化して保存。
- LM Studioの実行時入力上限の取得と、モデルロード時への指定値の反映。
- 新規OpenVINO接続では、空のJSONになる事例を避けるため構造化JSON指定を既定でOFF。コード側でのJSON検証・修正・再試行は継続。
- 従来のLemonade設定の引き継ぎ。設定形式の移行だけでは索引を再作成せず、モデル・推論先を変えた工程を再処理。
- ユーザー向けガイドとアプリ内ヘルプを更新。

## 更新と注意点

更新前に「アプリを完全終了」でLocal RAGを終了し、`LocalRAG-0.26.7-win-x64.msi`を実行してください。設定・索引・元文書は保持されます。

複数接続の設定を保存した後は、旧版ではその設定を読めません。旧版へ戻す可能性がある場合は、保存前に設定を保管してください。

外部AIへ接続すると、取り込み時の文書本文・説明や検索語が指定サーバーへ送信されます。取得時の秘匿検査はこの入力送信を防止しません。外部サーバーは接続のみで、起動停止はサーバー側で管理します。

Docker版OpenVINOの起動停止はコンテナ側で行います。Local RAGのOpenVINO自動起動設定はWindows版`ovms.exe`向けです。LM Studioの終了連動は本アプリが開始したHTTPサーバーを停止し、共有のGUI・デーモンは残します。

## 確認範囲

AMD CPUのPCにおいて、LM StudioとDocker版OpenVINOの4通りの組み合わせで、合成文書の取り込み、MCP検索、全文取得、秘匿チェックに成功しました。Intel NPU・NVIDIA GPUとの組み合わせ、Windows版OpenVINOの連鎖起動終了は実機未検証です。

検証の詳細：[実モデル試験](AiConnectionsLiveValidation-2026-11-13.md)、[開発時の自動試験](AiConnectionsValidation-2026-11-12.md)、[公開前検証](ReleaseValidation-0.26.7-2026-11-13.md)。
