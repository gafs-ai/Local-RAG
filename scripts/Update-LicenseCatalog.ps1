# Rebuild the embedded inventory from reviewed notices. New dependencies require license review.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo 'ThirdPartyNotices'
$entries = [Collections.Generic.List[object]]::new()
function Add-Entry($name,$version,$license,$scope,$description,$source,$files) {
    foreach($file in $files) { if(!(Test-Path -LiteralPath (Join-Path $root $file))) { throw "Missing reviewed notice: $file" } }
    $entries.Add([ordered]@{Name=$name;Version=$version;License=$license;Scope=$scope;Description=$description;Source=$source;Files=@($files)})
}
Add-Entry 'Local RAG' '' 'GPL-3.0-or-later' '本体' 'Copyright (C) 2026 GAFS Inc. GPL第3版またはそれ以降を選択できます。' 'https://github.com/gafs-ai/Local-RAG' @('LocalRAG-GPL-3.0.txt')
foreach($package in Get-Content (Join-Path $root 'packages.json') -Raw | ConvertFrom-Json) {
    $name,$version=$package.Package.Split('/')
    $license=$package.License
    $files=@(Get-ChildItem $root -File | Where-Object {$_.Name -like "$name-$version-*" -and $_.Extension -ne '.nuspec'} | Sort-Object Name | Select-Object -ExpandProperty Name)
    if($name -eq 'SurrealDb.Net') { $license='Apache-2.0' }
    if($name -eq 'UTF.Unknown') { $license='GPL-2.0-or-later（GPLv3を適用）' }
    if($license -eq 'Apache-2.0' -and !$files.Count) { $files=@('Apache-2.0.txt') }
    if(!$files.Count) { throw "License text missing for $name" }
    $source=if($package.Source){$package.Source}else{"https://www.nuget.org/packages/$name/$version"}
    $scope=if($name -eq 'Microsoft.CodeAnalysis.Analyzers'){'ビルド時'}else{'同梱ライブラリ'}
    Add-Entry $name $version $license $scope ($package.Copyright + "`nAuthors: " + $package.Authors) $source $files
}
foreach($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $files=@(Get-ChildItem $root -File -Filter "$pack-10.0.14-*" | Sort-Object Name | Select-Object -ExpandProperty Name)
    if(!$files.Count){throw "Missing runtime notices: $pack"}
    Add-Entry $pack '10.0.14' 'MIT / 同梱第三者通知' '自己完結型配布に同梱' '.NET Foundation / Microsoftおよび各依存ソフトウェアの権利者。ライセンス原文と第三者通知を参照してください。' 'https://github.com/dotnet/dotnet' $files
}
Add-Entry 'WiX UI' '5.0.2' 'MS-RL' 'インストーラに組み込み' 'アプリ本体とは別のライセンスです。MSIには対応ソースも同梱します。' 'https://github.com/wixtoolset/wix/tree/aa65968c419420d32e3e1b647aea0082f5ca5b78' @('WiX-5.0.2-LICENSE.txt','WiX-5.0.2-NOTICE.txt')
Add-Entry 'SurrealDB Server' '3.3.5' 'Business Source License 1.1' '初回に別途ダウンロード・別プロセス' 'SurrealDb.NetクライアントのApache-2.0とは異なります。追加利用許諾・制限・Change Dateは原文を確認してください。本体インストーラにDB実行ファイルは含めません。' 'https://github.com/surrealdb/surrealdb/blob/v3.3.5/LICENSE' @('SurrealDB-3.3.5-LICENSE.txt')
Add-Entry 'Python' '3.14.10' 'PSF-2.0 / 同梱第三者通知' '必要時に別途ダウンロード' 'Pythonおよび構文解析用の実行環境。取得アーカイブにもライセンスを保持します。' 'https://www.python.org/downloads/release/python-31410/' @('Python-3.14.10-LICENSE.txt')
foreach($package in Get-Content (Join-Path $repo 'src/LocalRag.Infrastructure/TreeSitterPackages.json') -Raw | ConvertFrom-Json) {
    Add-Entry $package.Name $package.Version 'MIT' '必要時に別途ダウンロード' '構文解析と各言語の文法。ダウンロードするwheelにも元のライセンスが含まれます。' ("https://pypi.org/project/"+$package.Name+"/"+$package.Version+"/") @("$($package.Name)-$($package.Version)-LICENSE.txt")
}
Add-Entry 'Lemonade Server' '' 'Apache-2.0 / 同梱第三者通知' '利用者が別途インストール' '本体には同梱しません。表示する原文は2026-11-06時点の上流LICENSEです。実際に利用する版・推論バックエンドの通知も確認してください。' 'https://github.com/lemonade-sdk/lemonade/blob/main/LICENSE' @('Lemonade-LICENSE.txt')
Add-Entry 'Gemma 3 / nomic / その他のモデル' '' 'モデル別の利用条件' '利用者が別途ダウンロード' 'モデルのライセンスはアプリ本体とは別です。Gemma 3はGemma利用規約、nomic v2原モデルはApache-2.0です。選択した量子化・変換配布物の条件も確認してください。' 'https://ai.google.dev/gemma/terms/' @('Models-NOTICE.txt')
$entries.ToArray() | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'index.json') -Encoding utf8
Write-Output "$($entries.Count) license entries."
