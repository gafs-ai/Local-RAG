param([string]$OutputDirectory = '.local/pipeline-evaluation')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repository)
if (Test-Path -LiteralPath $output) { throw '新しい検証フォルダを指定してください。既存の評価結果は上書きしません。' }
$manifestPath = Join-Path $repository 'Documents/PipelineEvaluationManifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$corpus = Join-Path $output 'corpus'
New-Item -ItemType Directory -Path $corpus -Force | Out-Null
foreach ($source in $manifest.Sources) {
    if ([IO.Path]::GetFileName($source.File) -ne $source.File) { throw '試料名が不正です。' }
    $destination = Join-Path $corpus $source.File
    if ($source.File -eq 'privacy-canary.txt') {
        [IO.File]::WriteAllText($destination, "社外秘`n`n試験専用の機密ラベルです。実在する個人情報や認証情報はありません。", [Text.UTF8Encoding]::new($false))
    } elseif ($source.Source.StartsWith('https://', [StringComparison]::Ordinal)) {
        Invoke-WebRequest -Uri $source.Source -OutFile $destination
    } else {
        Copy-Item -LiteralPath (Join-Path $repository $source.Source) -Destination $destination
    }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $source.Sha256) {
        throw "試料 $($source.File) が記録時と異なります。出典・変更内容・改行形式を確認してから別の評価として実行してください。"
    }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'manifest.json')
Write-Output "試料の準備とハッシュ確認が完了しました: $output"
