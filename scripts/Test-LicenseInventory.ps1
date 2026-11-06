param([string]$AssetsFile = 'src/LocalRag.Desktop/obj/project.assets.json', [string]$DistributionDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$assets = Get-Content (Join-Path $repo $AssetsFile) -Raw | ConvertFrom-Json
$actual = @($assets.libraries.PSObject.Properties | Where-Object {$_.Value.type -eq 'package'} | Select-Object -ExpandProperty Name | Sort-Object)
$reviewed = @(Get-Content (Join-Path $repo 'ThirdPartyNotices/packages.json') -Raw | ConvertFrom-Json | Select-Object -ExpandProperty Package | Sort-Object)
if (Compare-Object $actual $reviewed) { throw 'Dependency versions changed. Review ThirdPartyNotices and regenerate the license catalog before packaging.' }
$catalog = Get-Content (Join-Path $repo 'ThirdPartyNotices/index.json') -Raw | ConvertFrom-Json
foreach($entry in $catalog) {
    foreach($file in $entry.Files) { if(!(Test-Path -LiteralPath (Join-Path $repo ('ThirdPartyNotices/'+$file)))) { throw "Missing license: $file" } }
}
Write-Output "License inventory matches $($actual.Count) NuGet dependencies."
if ($DistributionDirectory) {
    $runtime = Get-Content (Join-Path $DistributionDirectory 'LocalRag.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach($framework in $runtime.runtimeOptions.includedFrameworks) {
        $package=$framework.name.ToLowerInvariant()+'.runtime.win-x64'
        if(!@($catalog | Where-Object {$_.Name -eq $package -and $_.Version -eq $framework.version}).Count) { throw "Runtime license inventory is stale: $package/$($framework.version)" }
    }
}
