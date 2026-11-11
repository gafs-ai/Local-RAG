# 依存ソフトウェアのライセンス（バージョン0.26.3）

アプリ上部の「ライセンス」を開くと、名前で検索し、ライセンス原文・著作権表示・参照先をオフラインで確認できます。通常のビルドにも一覧と原文を埋め込み、配布フォルダの `ThirdPartyNotices` にも同梱します。本体はGAFS Inc.のGPL-3.0-or-laterです。依存ソフトウェアの条件は本体と同一とは限りません。

SurrealDB **サーバー**はBSL 1.1です（[公式説明](https://surrealdb.com/license)）。本体とは別プロセスで必要時に取得します。接続に使う **SurrealDb.Netクライアント**のApache-2.0とは区別してください。Pythonとtree-sitterは対象言語の解析に必要な場合に取得します。

Lemonade Serverとモデルは別途導入します。Lemonadeのライセンスは2026-11-06取得の参考原文を収録していますが、実際に導入する版の表示も確認してください。モデルは同梱しません。開発に使用したGemma 3系列は[Gemma利用規約](https://ai.google.dev/gemma/terms/)、nomic-embed-text-v2-moeの元モデルは[モデルカード](https://huggingface.co/nomic-ai/nomic-embed-text-v2-moe)のApache-2.0です。GGUF変換版・NPU向け変換版についても各配布元の条件が適用されます。

UTF.Unknownは配布元が示す選択肢のうちGPL第3版を適用し、元のライセンス表示も残しています。WiXのMSI内コードに対応するソースZIPとMS-RL原文はMSIに同梱します。Local RAGの対応ソースは同じGitタグと、クリーンなビルドから生成されるソースZIPで提供します。

以下はこの版でレビューした一覧です。ランタイムやNuGet依存の版が変わった場合、配布スクリプトは更新漏れを検出します。

旧形式`.doc`の変換には、別途導入済みのMicrosoft Wordを使用します。Word本体・Office相互運用アセンブリは同梱しません。Wordの利用条件は導入済み製品・契約に従い、[Microsoftのライセンス条項](https://www.microsoft.com/useterms)からも確認できます。アプリのライセンス一覧には[外部依存の通知](../ThirdPartyNotices/Microsoft-Word-NOTICE.txt)を収録しています。

| ソフトウェア | 版 | ライセンス | 利用形態 | 原文 |
|---|---|---|---|---|
| [Local RAG](https://github.com/gafs-ai/Local-RAG) |  | GPL-3.0-or-later | 本体 | [原文](../ThirdPartyNotices/LocalRAG-GPL-3.0.txt) |
| [ConcurrentHashSet](https://github.com/i3arnon/ConcurrentHashSet.git) | 1.3.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/ConcurrentHashSet-1.3.0-LICENSE.txt) |
| [Dahomey.Cbor](https://github.com/dahomey-technologies/Dahomey.Cbor.git) | 1.26.3 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Dahomey.Cbor-1.26.3-LICENSE.txt) |
| [Microsoft.AspNetCore.JsonPatch.SystemTextJson](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.AspNetCore.JsonPatch.SystemTextJson-10.0.7-LICENSE.txt) |
| [Microsoft.CodeAnalysis.Analyzers](https://github.com/dotnet/roslyn) | 5.9.0-1.26328.17 | MIT | ビルド時 | [原文](../ThirdPartyNotices/Microsoft.CodeAnalysis.Analyzers-5.9.0-1.26328.17-LICENSE.txt) |
| [Microsoft.CodeAnalysis.Common](https://github.com/dotnet/roslyn) | 5.9.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.CodeAnalysis.Common-5.9.0-LICENSE.txt) |
| [Microsoft.CodeAnalysis.CSharp](https://github.com/dotnet/roslyn) | 5.9.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.CodeAnalysis.CSharp-5.9.0-LICENSE.txt) |
| [Microsoft.Extensions.AI.Abstractions](https://github.com/dotnet/extensions) | 10.11.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.AI.Abstractions-10.11.0-LICENSE.txt) |
| [Microsoft.Extensions.Caching.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Caching.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Configuration](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Configuration-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.Configuration.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Configuration.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Configuration.Binder](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Configuration.Binder-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.DependencyInjection-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.DependencyInjection.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Diagnostics](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Diagnostics-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.Diagnostics.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Diagnostics.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.FileProviders.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.FileProviders.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Hosting.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Hosting.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Http](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Http-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.Logging](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Logging-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Logging.Abstractions-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Options](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Options-10.0.13-LICENSE.txt) |
| [Microsoft.Extensions.Options.ConfigurationExtensions](https://github.com/dotnet/dotnet) | 10.0.7 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Options.ConfigurationExtensions-10.0.7-LICENSE.txt) |
| [Microsoft.Extensions.Primitives](https://github.com/dotnet/dotnet) | 10.0.13 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Extensions.Primitives-10.0.13-LICENSE.txt) |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/Microsoft/Microsoft.IO.RecyclableMemoryStream) | 3.0.1 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.IO.RecyclableMemoryStream-3.0.1-LICENSE.txt) |
| [Microsoft.Spatial](https://www.nuget.org/packages/Microsoft.Spatial/7.22.0) | 7.22.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Microsoft.Spatial-7.22.0-LICENSE.txt) |
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) | 2.3.0 | Apache-2.0 | 同梱ライブラリ | [原文](../ThirdPartyNotices/Apache-2.0.txt) |
| [ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol/csharp-sdk) | 2.3.0 | Apache-2.0 | 同梱ライブラリ | [原文](../ThirdPartyNotices/Apache-2.0.txt) |
| [ModelContextProtocol.Core](https://github.com/modelcontextprotocol/csharp-sdk) | 2.3.0 | Apache-2.0 | 同梱ライブラリ | [原文](../ThirdPartyNotices/Apache-2.0.txt) |
| [Semver](https://github.com/WalkerCodeRanger/semver) | 3.0.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Semver-3.0.0-LICENSE.txt) |
| [SurrealDb.Net](https://github.com/surrealdb/surrealdb.net) | 1.0.0 | Apache-2.0 | 同梱ライブラリ | [原文](../ThirdPartyNotices/SurrealDb.Net-1.0.0-LICENSE.txt) |
| [System.Reactive](https://github.com/dotnet/reactive) | 6.0.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/System.Reactive-6.0.0-LICENSE.txt) |
| [UTF.Unknown](https://github.com/CharsetDetector/UTF-unknown) | 2.7.0 | GPL-2.0-or-later（GPLv3を適用） | 同梱ライブラリ | [原文](../ThirdPartyNotices/UTF.Unknown-2.7.0-GPL-3.0.txt) / [原文](../ThirdPartyNotices/UTF.Unknown-2.7.0-NOTICE.txt) / [原文](../ThirdPartyNotices/UTF.Unknown-2.7.0-README.md) |
| [Websocket.Client](https://github.com/Marfusios/websocket-client) | 5.3.0 | MIT | 同梱ライブラリ | [原文](../ThirdPartyNotices/Websocket.Client-5.3.0-LICENSE.txt) |
| [microsoft.netcore.app.runtime.win-x64](https://github.com/dotnet/dotnet) | 10.0.14 | MIT / 同梱第三者通知 | 自己完結型配布に同梱 | [原文](../ThirdPartyNotices/microsoft.netcore.app.runtime.win-x64-10.0.14-LICENSE.TXT) / [原文](../ThirdPartyNotices/microsoft.netcore.app.runtime.win-x64-10.0.14-THIRD-PARTY-NOTICES.TXT) |
| [microsoft.aspnetcore.app.runtime.win-x64](https://github.com/dotnet/dotnet) | 10.0.14 | MIT / 同梱第三者通知 | 自己完結型配布に同梱 | [原文](../ThirdPartyNotices/microsoft.aspnetcore.app.runtime.win-x64-10.0.14-LICENSE.txt) / [原文](../ThirdPartyNotices/microsoft.aspnetcore.app.runtime.win-x64-10.0.14-THIRD-PARTY-NOTICES.TXT) |
| [microsoft.windowsdesktop.app.runtime.win-x64](https://github.com/dotnet/dotnet) | 10.0.14 | MIT / 同梱第三者通知 | 自己完結型配布に同梱 | [原文](../ThirdPartyNotices/microsoft.windowsdesktop.app.runtime.win-x64-10.0.14-LICENSE) |
| [WiX UI](https://github.com/wixtoolset/wix/tree/aa65968c419420d32e3e1b647aea0082f5ca5b78) | 5.0.2 | MS-RL | インストーラに組み込み | [原文](../ThirdPartyNotices/WiX-5.0.2-LICENSE.txt) / [原文](../ThirdPartyNotices/WiX-5.0.2-NOTICE.txt) |
| [SurrealDB Server](https://github.com/surrealdb/surrealdb/blob/v3.3.5/LICENSE) | 3.3.5 | Business Source License 1.1 | 初回に別途ダウンロード・別プロセス | [原文](../ThirdPartyNotices/SurrealDB-3.3.5-LICENSE.txt) |
| [Python](https://www.python.org/downloads/release/python-31410/) | 3.14.10 | PSF-2.0 / 同梱第三者通知 | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/Python-3.14.10-LICENSE.txt) |
| [tree-sitter](https://pypi.org/project/tree-sitter/0.26.0/) | 0.26.0 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-0.26.0-LICENSE.txt) |
| [tree-sitter-typescript](https://pypi.org/project/tree-sitter-typescript/0.23.2/) | 0.23.2 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-typescript-0.23.2-LICENSE.txt) |
| [tree-sitter-javascript](https://pypi.org/project/tree-sitter-javascript/0.25.0/) | 0.25.0 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-javascript-0.25.0-LICENSE.txt) |
| [tree-sitter-cpp](https://pypi.org/project/tree-sitter-cpp/0.23.4/) | 0.23.4 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-cpp-0.23.4-LICENSE.txt) |
| [tree-sitter-c](https://pypi.org/project/tree-sitter-c/0.24.2/) | 0.24.2 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-c-0.24.2-LICENSE.txt) |
| [tree-sitter-rust](https://pypi.org/project/tree-sitter-rust/0.24.2/) | 0.24.2 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-rust-0.24.2-LICENSE.txt) |
| [tree-sitter-java](https://pypi.org/project/tree-sitter-java/0.23.5/) | 0.23.5 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-java-0.23.5-LICENSE.txt) |
| [tree-sitter-kotlin](https://pypi.org/project/tree-sitter-kotlin/1.1.0/) | 1.1.0 | MIT | 必要時に別途ダウンロード | [原文](../ThirdPartyNotices/tree-sitter-kotlin-1.1.0-LICENSE.txt) |
| [Lemonade Server](https://github.com/lemonade-sdk/lemonade/blob/main/LICENSE) |  | Apache-2.0 / 同梱第三者通知 | 利用者が別途インストール | [原文](../ThirdPartyNotices/Lemonade-LICENSE.txt) |
| [Gemma 3 / nomic / その他のモデル](https://ai.google.dev/gemma/terms/) |  | モデル別の利用条件 | 利用者が別途ダウンロード | [原文](../ThirdPartyNotices/Models-NOTICE.txt) |

PDFの読み取りにはPdfPig 0.1.16（Apache-2.0）を同梱します。上流の該当コミットの[LICENSEと第三者通知](../ThirdPartyNotices/PdfPig-0.1.16-LICENSE.txt)を全文収録し、アプリ内の一覧にも追加しています。
