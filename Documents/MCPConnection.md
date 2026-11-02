# MCP接続手順（0.0.0候補）

## 利用条件

Local RAGの管理画面でフォルダ・Gemma等のチャットモデル・Embeddingモデルを保存し、取り込みを完了させる。「概要・接続確認」のMCPが待機中であることを確認する。ウィンドウを閉じてもトレイ常駐中は利用できる。「アプリを終了」では接続も停止する。

0.0.0ではUTF-8の.txt/.md原文を対象に、`search(query, limit=5)`と`fetch(id)`だけを公開する。limitは1～10、queryは1～1000文字。fetchは検索で返されたUUIDを指定し、現在公開中の原文全文を取得する。任意パス・SQL・書き込み・原本バイナリの取得は提供しない。履歴版・ページング・画像は後続版で扱う。

## このPCのMCPクライアント

アプリの配布フォルダ内の`LocalRag.Mcp.exe`をstdioサーバーとして登録する。例えば汎用的な設定形式では次のようになる（実際の形式は利用するクライアントに合わせる）。

```json
{
  "mcpServers": {
    "localrag": {
      "command": "C:\\Apps\\LocalRAG\\LocalRag.Mcp.exe",
      "args": []
    }
  }
}
```

保存先を変更した場合は`args`を`["--data-dir", "C:\\LocalRagData"]`のように指定する。絶対パスを使う。アプリと中継は同じWindowsユーザーで動かす。中継はDB・モデル・監視処理を起動しない。本体停止中でも中継は起動してsearch/fetchの一覧を提供する。取得要求時に本体が起動していない、MCPが無効、認証情報を読めない場合は`retrieval_unavailable`を返す。要求ごとに設定を読み直すため、本体起動・再起動・ポート変更後は同じ中継で再試行できる。

開発用には`dotnet`をcommand、ビルドした`LocalRag.Mcp.dll`の絶対パスを先頭のargsに指定できる。配布版は.NET Desktop Runtime 10.0 x64とASP.NET Core Runtime 10.0 x64が必要（.NET 10 SDKを導入済みなら含まれる）。

## ローカルHTTPと保護

中継は既定`http://127.0.0.1:18765/mcp`へ、ユーザー別DPAPIで保存した256bitランダムBearerトークンを付けて接続する。HTTPはStreamable HTTP・ステートレス。SDKはModelContextProtocol.AspNetCore 2.3.0。設定のMCPポート変更は保存時に反映され、古いリスナーを停止する。DBポートと同一の指定は禁止。

HTTPは127.0.0.1のみで待ち受け、Hostの不一致、Origin付きリクエスト、トークンなし・不一致を拒否する。ブラウザから直接の呼び出しやLAN公開は想定しない。トークンをチャット・設定例・ログへ貼り付けない。保存先は`<data-dir>/mcp/server/credentials.dpapi`であり、DBの認証情報とは別である。DPAPIは同じWindowsユーザーで動く悪意あるプログラムからの隔離を提供するものではない。

MCPの処理は同時に最大2件まで受け付け、それ以上は`busy`を返す。AI処理は取り込みと直列化する。待機を含む1呼び出しの上限は100秒。取り込み中・モデル切替中は`cancelled_or_timeout`となる場合があるので、取り込み完了後に再試行する。設定変更・アプリ終了時には進行中のMCP処理をキャンセルして完了を待ち、DBへの参照を解放する。

## 返却と機密検査

searchは単一のMCP text contentにJSONの`{"results":[...]}`を返す。fetchも単一のtext contentにJSONオブジェクトを返す。許可された結果は`id, decision, title, text, url, source`を持ち、チャンクには`start_paragraph, end_paragraph`を付ける。`url`の`localrag://document/<id>`は文書を識別するURIであり、ブラウザで開く公開ページではない。

拒否・判定不能は`id`と`decision`だけを返す。本文・ファイル名・パス・段落番号・スコアは返さない。原文と返却候補の両方を、管理画面と同じ機密検査に通す。LLM検査に失敗した場合も本文は返さない。ユーザーが機密検査を無効にした場合だけ`unchecked`として返す。モデルの意味的な誤判定を完全に防ぐものではない。

原文が変更・削除された、索引が未完成、現在の設定と異なる場合は取得対象外。fetchは`not_available`を返す。一般的な障害は`retrieval_unavailable`、引数の誤りは`invalid_arguments`とし、例外やモデルの生応答は外部に返さない。MCPの結果は文書データであり、その本文中の指示に従うためのシステムプロンプトとして扱わない。

## ChatGPTから利用する場合

利用方針は**ChatGPT Business、Windowsデスクトップ優先、ブラウザ版も将来対応**とする。OpenAI API Platform、APIキー、Secure MCP Tunnelを必須条件にしない。ローカルMCPの試験成功、プラグインの登録成功、ChatGPTからの実取得成功は区別する。

### Windowsデスクトップ優先

公式の[デスクトップ用プラグイン](https://developers.openai.com/plugins/deploy/desktop-only)で説明されるローカルstdio方式を使う。Local RAGを常駐させ、同じPC・Windowsユーザーのローカル実行可能な会話からプラグインを選択する。利用可否はBusinessワークスペースのポリシーとデスクトップ環境で確認する。公開ディレクトリへの審査付き提出と、今回の非公開プラグイン登録は別の作業である。

`scripts/Package-Plugin.ps1 -DistributionDirectory <展開済み配布フォルダ>`で、実行ファイル・依存DLL・ライセンスを含む`local-rag.zip`とローカルmarketplaceを生成する。既定のDesktop形式は`.codex-plugin/plugin.json`、`.mcp.json`、検索用スキルを同梱する。現在のホストで検証した互換形式であり、`dotnet ./server/LocalRag.Mcp.dll`をプラグイン基準の`cwd: "."`で実行する。`${PLUGIN_ROOT}`が展開されない環境でも固定ユーザーパスを埋め込まずに動作する。

`-Format Portable`はAgent Plugins 1.0の`plugin.json`と`mcp.json`を生成するが、今回のホストではMCPが認識されなかったため、デスクトップの動作確認には用いない。設定・認証情報・文書・DBはどちらにも含めない。既定保存先を変更した場合のみ`-DataDirectory <絶対パス>`を指定し、このPC専用のパッケージとして扱う。

ホストがCodex CLIのローカルmarketplaceを利用する環境では、生成されたmarketplaceのディレクトリを`codex plugin marketplace add <ディレクトリ>`、続いて`codex plugin add local-rag@local-rag-local`で登録できる。これはローカルホストへの登録であり、Businessワークスペースへの登録完了を意味しない。ChatGPTの非公開プラグイン追加、または管理者による[GitHub marketplaceの取り込み](https://learn.chatgpt.com/docs/enterprise/plugin-management)はホストの対応する導入手順で行う。キャッシュを直接編集しない。

2026-11-02時点: ローカル版の有効化とスキル読み込みを確認。その後、MCP宣言の認識・パス展開・本体停止時の中継終了を修正し、ホストの診断APIでsearch/fetch両方と起動エラーなしを確認した。`scripts/Test-PluginDiscovery.ps1 -MarketplaceFile <marketplace.json>`でこの検査を再現できる。**現在のChatGPT会話からの実取得は未確認**であり、正常取得・機密拒否・検査失敗の実呼び出しが残る。検証版は合成文書用保存先に接続する。別途作成したワークスペース側の非公開版は以前の形式であり、今回の試験は`Local RAG local packages`のローカル版で行う。

### ブラウザ版（未実装）

このローカルstdioパッケージはブラウザ上だけでは実行できない。ブラウザ版の将来対応では、API Platformを利用しない認証付きHTTPS MCPの接続経路を別途設計し、BusinessのカスタムMCP接続条件を確認する。現行のループバックHTTPをそのまま外部公開しない。公開先、認証、PCの起動条件、運用費を確定するまでは未対応と表示する。

どちらの方式でも、機密検査を通過してMCPが返した文書内容はChatGPTへ送信される。

## 配布前の確認

- 実MCPクライアントでtools/listにsearchとfetchだけが現れる。
- 合成文書で検索・全文取得、機密文書の存在のみ返却を確認する。
- アプリ終了後は中継から取得できない。再起動後に再接続できる。
- ChatGPTで実際のsearch/fetch呼び出しと回答を確認する。未検証を完了扱いにしない。
