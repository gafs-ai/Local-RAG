param([string]$CorpusDirectory = '.local/structural-evaluation-20261106/corpus', [string]$OutputDirectory = '.local/query-evaluation-20261106')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseline = Get-Content (Join-Path $repository 'Documents/RetrievalEvaluationManifest.json') -Raw | ConvertFrom-Json
$corpus = [IO.Path]::GetFullPath($CorpusDirectory, $repository)
$output = [IO.Path]::GetFullPath($OutputDirectory, $repository)
foreach ($source in $baseline.Sources) {
    $text = [IO.File]::ReadAllText((Join-Path $corpus $source.File)).Replace("`r`n", "`n")
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
    if ($hash -ne $source.NormalizedSha256) { throw "試料の版が異なります: $($source.File)" }
}
# Fixed before measuring. Do not insert answer terms that the original question did not supply.
# Columns: keywords | topic description | English question | intentionally underspecified query.
$variants = @'
HTTP 404 410 違い|HTTPの404と410のステータスの意味の違いについての説明|What is the difference between HTTP 404 and 410?|HTTPエラー
HTTP リクエスト 繰り返し 同じ効果 性質|HTTPで同じリクエストを繰り返しても効果が変わらない性質についての説明|What is the property that repeating an HTTP request has the same effect?|繰り返し
HTTP メソッド 冪等性 保証されない|HTTPメソッドのうち冪等であることが保証されないものについての説明|Which HTTP methods are not guaranteed to be idempotent?|HTTPメソッド
HTTP リソース 永久削除 ステータス|永久に削除されたWebリソースに対して返すHTTPステータスについての説明|Which HTTP status should be used when a web resource has been permanently removed?|削除
Local RAG 起動 必要 .NET Runtime|Local RAGの起動に必要な.NETランタイムの種類についての説明|Which .NET runtimes are required to launch Local RAG?|起動
Local RAG セットアップ モデル 取り込みフォルダ 設定|Local RAGの初期設定で指定するAIモデルと取り込みフォルダについての説明|Which models and ingestion folders should be configured when setting up Local RAG?|設定
文字列 サロゲートペア 保持 モデル上限 分割|サロゲートペアを壊さずに文字列をモデルの入力上限内へ分割するコード|Which code splits a string within the model input limit without breaking surrogate pairs?|文字列分割
文字列 末尾 UTF-8 バイト数 上限 取得|UTF-8バイト数の上限を守って文字列の末尾を取得するコード|Which code gets the tail of a string within a UTF-8 byte limit?|文字列末尾
監視 スリープ 時刻超過 元の周期 次回実行 計算 コード|スリープ中に監視時刻を過ぎても元の周期に沿って次回の実行時刻を計算するコード|Which code calculates the next monitoring time after sleep while preserving the original interval schedule?|監視
監視スケジュール 間隔 ゼロ 負数 例外|監視スケジュールでゼロまたは負の間隔が指定された場合に投げる例外についての説明|Which exception is thrown when the monitoring interval is zero or negative?|例外
LLM クラス コード全体 入力上限 超過 説明生成 材料|クラス全体のコードがLLMの入力上限に収まらない場合に説明生成へ渡す材料についての説明|What input is used to describe a class when its full code exceeds the LLM context window?|クラス説明
クラス 説明生成失敗 埋め込み省略 下位関数 検索|クラスの説明生成に失敗してクラスの埋め込みを省略した場合の下位関数の検索についての説明|Can child functions still be searched when class description generation fails and its embedding is skipped?|関数検索
章全体 Embedding 入力上限 超過 親 検索用説明 生成|章全体がEmbeddingの入力上限を超える場合の親の検索用説明の作成方法|How is a parent search description generated when a whole chapter exceeds the embedding input limit?|親説明
文書 親チャンク 説明生成 中断 完了した子 再処理|文書の親説明生成中に停止した後、完了済みの子の処理をやり直すかどうかの説明|When document parent description generation is interrupted, are completed child operations repeated?|再開
テキスト処理 設定画面 Markdown 親 最小 最大文字数 表示条件|テキスト処理画面でMarkdown親チャンクの最小・最大文字数の設定が表示される条件|When are the minimum and maximum Markdown parent chunk lengths shown in text processing settings?|Markdown設定
処理方式 切り替え 設定欄 非表示 以前の入力値 消去|処理方式を切り替えて非表示になった設定項目の既存の値の扱いについての説明|Are existing values erased when switching processing modes hides their settings fields?|非表示
ソースコード 検索 コード本文 説明文 ベクトル化 対象|ソースコード検索でコード本文と説明文のどちらをベクトル化するかについての説明|Does source code search embed the code itself or its description?|コード検索
ソースファイル 全体 説明生成 説明コメントなし 入力|ソースファイルにファイルレベルの説明コメントがない場合のファイル全体の説明生成に使う入力|What input is used to generate a file description when no file-level documentation comment exists?|ファイル説明
親チャンク 説明 圧縮 繰り返し 短くならない 対処|長い親チャンクの説明を繰り返し圧縮しても短くならない場合の扱いについての説明|What happens when repeatedly compressing a long parent chunk description does not make it shorter?|圧縮
文書 親説明 検索ヒット 返却 要約 原文|文書の親説明が検索にヒットした場合に要約と原文のどちらを返すかについての説明|When a document matches through its parent description, is the summary or original text returned?|検索結果
'@ -split '\r?\n'
if ($variants.Count -ne $baseline.Queries.Count) { throw '質問数が一致しません。' }
$queries = @()
for ($i=0; $i -lt $variants.Count; $i++) {
    $q = $baseline.Queries[$i]
    $question = $q.Text
    if ($i -eq 2) { $question = '冪等であることが保証されないHTTPメソッドはどれですか' }
    if ($i -eq 3) { $question = 'Webリソースが永久に削除された場合、サーバーはどのHTTPステータスを返しますか' }
    $v = $variants[$i] -split '\|'
    if ($v.Count -ne 4) { throw '列数が不正です。' }
    $texts = [ordered]@{
        question = $question; keywords = $v[0]; description = $v[1]; english = $v[2]
        verbose = "私はこのシステムを利用しており、仕組みを理解したいと思っています。関連する資料を検索して、次の質問に丁寧に回答してください。$question 回答は日本語で、要点を箇条書きにし、必要に応じて具体例も添えてわかりやすく説明してください。"
        vague = $v[3]
    }
    foreach ($style in $texts.Keys) {
        $queries += [ordered]@{ Id=('q{0:d2}' -f ($i+1)); Style=$style; Text=$texts[$style]; File=$q.File; Evidence=$q.Evidence; Split=$q.Split }
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
[ordered]@{ Corpus=$corpus; Queries=$queries; Sources=$baseline.Sources } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'manifest.json') -Encoding utf8
$shortInstructions = @($queries | Where-Object Style -eq 'question' | ForEach-Object {
    [ordered]@{ Id=$_.Id; Style='short_instruction'; Text=($_.Text + ' 要点を日本語で教えてください。'); File=$_.File; Evidence=$_.Evidence; Split=$_.Split }
})
[ordered]@{ Corpus=$corpus; Queries=$shortInstructions; Sources=$baseline.Sources } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'short-instruction-manifest.json') -Encoding utf8
Write-Output "120 queries prepared before evaluation: $output"
