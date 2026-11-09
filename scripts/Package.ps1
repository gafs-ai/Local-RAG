param([ValidatePattern('^\d+\.\d{2}\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d{2}\.\d+$') { throw 'Invalid VERSION: expected major.year.release.' }
$releaseNotes = "Release-$Version.md"
if (!(Test-Path -LiteralPath (Join-Path $repo "Documents/$releaseNotes"))) { throw "Release notes missing: $releaseNotes" }
$semanticVersion = ([version]$Version).ToString()
Push-Location $repo
try {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outputRoot = Join-Path $repo "artifacts/$Version-$stamp"
    $stage = Join-Path $outputRoot "LocalRAG-$Version-win-x64"
    New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
    $buildOutput = Join-Path $repo '.local/package-build/'
    dotnet restore src/LocalRag.Desktop -r win-x64 --locked-mode --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & (Join-Path $PSScriptRoot 'Test-LicenseInventory.ps1')
    foreach ($project in @('src/LocalRag.Desktop','src/LocalRag.Mcp','src/LocalRag.ServerControl')) {
        dotnet publish $project -c Release -r win-x64 --no-restore --self-contained true "-p:Version=$semanticVersion" "-p:InformationalVersion=$Version" "-p:BaseOutputPath=$buildOutput" -o $stage
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    foreach ($application in @('LocalRag.Desktop','LocalRag.Mcp','LocalRag.ServerControl')) {
        $runtime = Get-Content (Join-Path $stage "$application.runtimeconfig.json") -Raw | ConvertFrom-Json
        if (!$runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) {
            throw "Application must use the bundled runtime: $application"
        }
    }
    Copy-Item LICENSE,COPYRIGHT $stage
    Copy-Item installer/README.md (Join-Path $stage 'README.md')
    $userDocs = New-Item -ItemType Directory -Path (Join-Path $stage 'Documents')
    foreach ($document in @('QuickStart.md','ThirdPartyLicenses.md',$releaseNotes,'DistributionValidation-2026-11-09.md','SettingsGuide.md','HelpTopics.json','MCPConnection.md','DataSources.md','ConfidentialityPatternScopes.md','IncrementalReprocessing.md','IngestionRetry-2026-11-09.md')) {
        Copy-Item (Join-Path 'Documents' $document) $userDocs.FullName
    }
    Copy-Item ThirdPartyNotices (Join-Path $stage 'ThirdPartyNotices') -Recurse
    & (Join-Path $PSScriptRoot 'Test-LicenseInventory.ps1') -DistributionDirectory $stage
    $revision = git -c "safe.directory=$repo" rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
    $dirty = @(git -c "safe.directory=$repo" status --porcelain).Count -gt 0
    [pscustomobject]@{Version=$Version;SemanticVersion=$semanticVersion;SourceRevision=$revision;DirtySource=$dirty;BuiltAtUtc=[DateTime]::UtcNow.ToString('O');Runtime='Windows Desktop + ASP.NET Core 10.0 win-x64 (self-contained)'} |
        ConvertTo-Json | Set-Content (Join-Path $stage 'build-info.json') -Encoding utf8
    $zip = "$stage.zip"
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    if (!$dirty) {
        git -c "safe.directory=$repo" archive --format=zip --output="$outputRoot/LocalRAG-$Version-source.zip" HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
    }
    Get-ChildItem $outputRoot -Filter *.zip | Get-FileHash -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path $_.Path -Leaf)" } |
        Set-Content (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Package: $zip"
    if ($dirty) { Write-Warning 'Working changes included: internal validation package only; no corresponding source archive generated.' }
} finally { Pop-Location }
