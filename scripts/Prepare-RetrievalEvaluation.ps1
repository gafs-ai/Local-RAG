param([string]$OutputDirectory = '.local/retrieval-evaluation', [string]$CachedCorpus = '.local/default-evaluation-20261104/corpus')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repository)
if (Test-Path -LiteralPath $output) { throw '新しい評価フォルダを指定してください。' }
$manifestPath = Join-Path $repository 'Documents/RetrievalEvaluationManifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$corpus = Join-Path $output 'corpus'
New-Item -ItemType Directory -Path $corpus -Force | Out-Null
foreach ($source in $manifest.Sources) {
    if ([IO.Path]::GetFileName($source.File) -ne $source.File) { throw '試料名が不正です。' }
    $destination = Join-Path $corpus $source.File
    $cached = Join-Path ([IO.Path]::GetFullPath($CachedCorpus, $repository)) $source.File
    if ((Test-Path -LiteralPath $cached) -and (Get-FileHash -LiteralPath $cached).Hash -eq $source.Sha256) {
        Copy-Item -LiteralPath $cached -Destination $destination
    } elseif ($source.File -eq 'privacy-canary.txt') {
        [IO.File]::WriteAllText($destination, "社外秘`n`n試験専用の機密ラベルです。実在する個人情報や認証情報はありません。", [Text.UTF8Encoding]::new($false))
    } elseif ($source.Source.StartsWith('https://', [StringComparison]::Ordinal)) {
        Invoke-WebRequest -Uri $source.Source -OutFile $destination
    } else {
        if ($source.Revision -notmatch '^[0-9a-f]{7,40}$') { throw '試料の版が不正です。' }
        $lines = & git -c "safe.directory=$repository" -C $repository show "$($source.Revision):$($source.Source)"
        if ($LASTEXITCODE -ne 0) { throw "Git試料を取得できません: $($source.File)" }
        [IO.File]::WriteAllText($destination, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    }
    $text = [IO.File]::ReadAllText($destination).Replace("`r`n", "`n")
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
    if ($hash -ne $source.NormalizedSha256) { throw "試料が記録時と異なります: $($source.File)。内容と出典を確認してください。" }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'manifest.json')
Write-Output "評価試料の準備と改行正規化後のハッシュ確認が完了しました: $output"
