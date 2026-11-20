# Local RAG 0.26.9

文書の取り込み、検索用MCP、設定の暗号化に使うライブラリと、Windows x64配布物に同梱する.NET 10のランタイムを更新しました。依存更新によるDB移行は不要です。

| 用途 | 0.26.8 | 0.26.9 |
|---|---|---|
| 同梱ランタイム（.NET / Windows Desktop / ASP.NET Core） | 10.0.14 | 10.0.15 |
| ビルド用.NET SDK | 10.0.403 | 10.0.404 |
| C#構文解析（Microsoft.CodeAnalysis.CSharp） | 5.9.0 | 5.12.0 |
| 構文解析（tree-sitter、必要時に取得） | 0.26.0 | 0.26.1 |
| 文字コード判定（UTF.Unknown） | 2.7.0 | 2.8.0 |
| 検索用MCP（ModelContextProtocol.AspNetCore） | 2.3.0 | 2.4.0 |
| DBクライアント（SurrealDb.Net） | 1.0.0 | 1.0.1 |
| Windowsのデータ保護（System.Security.Cryptography.ProtectedData） | 10.0.0 | 11.0.0 |
| テスト用SDK / xUnit用Visual Studioアダプター | 18.11.0 / 3.1.5 | 18.12.0 / 4.0.1 |

2026-11-18時点の安定版を対象に、直接参照するパッケージを照合しました。間接依存は上位パッケージが指定する組み合わせを採用し、解決結果をロックファイルに記録します。[同梱ライセンス](ThirdPartyLicenses.md)も更新しました。

- SurrealDBサーバーは3系列の3.3.5を維持します。4系列への更新は、既存データとの互換性を確認してから行います。バックアップからの復元、索引と旧版への切り戻しは別途検証します。
- PythonはWindows x64用tree-sitterのCPython 3.14専用wheelに合わせて3.14.10を維持します。各言語の文法パッケージ、PdfPig、xUnit v2本体の固定版も変更していません。
- MSI作成に使用するWiXとUI拡張は5.0.2を維持します。[後継メジャーでの利用条件の変更](https://docs.firegiant.com/wix/whatsnew/)と、MSIに含める対応ソースを確認してから移行します。利用者が導入するモデルサーバーとモデルはこの更新の対象外です。

## 既存環境から更新する場合

- このPC内の既定設定はそのまま使えます。暗号化しない接続を許可する宛先は `localhost`、`127.0.0.1`、`[::1]` のみです。別のPCは同じLAN内でもAIにHTTPS、DBにWSSまたはHTTPSを指定してください。旧版の外部HTTP・WS設定は送信前に拒否されます。[接続先の変更方法](SettingsGuide.md)
- AI接続先と外部DBの「証明書を検証する」は既定でオンです。Windowsが信頼する社内認証局も使えます。外部DBにリバースプロキシを使用している場合は、`GET /version` と `POST /rpc` を許可してください。
- 更新は「アプリを完全終了」でLocal RAGを終了してから、`LocalRAG-0.26.9-win-x64.msi` を実行します。元文書・設定・索引はインストール先とは別に保存されます。

[公開前の検証記録](ReleaseValidation-0.26.9-2026-11-20.md)
