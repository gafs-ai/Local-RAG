param([ValidatePattern('^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$')][string]$Version = '0.0.0-rc.3')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outputRoot = Join-Path $repo "artifacts/$Version-$stamp"
    $stage = Join-Path $outputRoot "LocalRAG-$Version-win-x64"
    New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
    dotnet restore LocalRAG.slnx --locked-mode --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build LocalRAG.slnx -c Release --no-restore "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($project in @('src/LocalRag.Desktop','src/LocalRag.Mcp')) {
        dotnet publish $project -c Release --no-restore --self-contained false "-p:Version=$Version" -o $stage
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    Copy-Item README.md,LICENSE,COPYRIGHT $stage
    Copy-Item Documents (Join-Path $stage 'Documents') -Recurse
    # Runtime dependencies only. No models, indexes, credentials, settings or test fixtures.
    $notices = Join-Path $stage 'ThirdPartyNotices'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Copy-Item .packages/surrealdb.net/1.0.0/LICENSE (Join-Path $notices 'Apache-2.0.txt')
    $inventory = @()
    $assets = Get-Content src/LocalRag.Desktop/obj/project.assets.json -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $key = $library.Name
        $packageDir = Join-Path .packages $key
        $spec = Get-ChildItem $packageDir -Filter *.nuspec | Select-Object -First 1
        [xml]$metadata = Get-Content $spec.FullName
        $license = $metadata.package.metadata.license
        $copyright = [string]$metadata.package.metadata.copyright
        $name = $key.Replace('/','-')
        Copy-Item $spec.FullName (Join-Path $notices "$name.nuspec")
        if ($license.type -eq 'file') {
            Copy-Item (Join-Path $packageDir $license.InnerText) (Join-Path $notices "$name-LICENSE.txt")
        } elseif ($license.InnerText -eq 'MIT') {
            @"
MIT License
$copyright
Authors: $($metadata.package.metadata.authors)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@ | Set-Content (Join-Path $notices "$name-LICENSE.txt") -Encoding utf8
        } elseif ($key -eq 'UTF.Unknown/2.7.0' -and $license.InnerText -eq 'MPL-1.1') {
            # This pinned package's README explicitly offers GPL-2.0-or-later as
            # an alternative. Preserve that notice and include the GPLv3 text.
            $readme = Join-Path $packageDir 'README.md'
            if ((Get-Content $readme -Raw) -notmatch 'GNU General Public License Version 2 or later') {
                throw 'UTF.Unknown alternative license notice changed; review before packaging.'
            }
            Copy-Item $readme (Join-Path $notices "$name-README.md")
            Copy-Item LICENSE (Join-Path $notices "$name-GPL-3.0.txt")
            @"
UTF.Unknown 2.7.0 is distributed here under its GPL-2.0-or-later alternative,
using GPL version 3. The NuGet metadata reports MPL-1.1; see the included
upstream README for the alternative license grant.
Upstream source: https://github.com/CharsetDetector/UTF-unknown/tree/fd4c6fab634ef3783f8b5cb88e9ae9b2cc295ad5
The library has not been modified by Local RAG.
"@ | Set-Content (Join-Path $notices "$name-NOTICE.txt") -Encoding utf8
        } elseif ($license.InnerText -ne 'Apache-2.0') { throw "Unhandled dependency license: $key" }
        $inventory += [pscustomobject]@{Package=$key;License=$license.InnerText;Authors=[string]$metadata.package.metadata.authors;Copyright=$copyright;Source=[string]$metadata.package.metadata.repository.url}
    }
    $inventory | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $notices 'packages.json') -Encoding utf8
    $revision = git -c "safe.directory=$repo" rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
    $dirty = @(git -c "safe.directory=$repo" status --porcelain).Count -gt 0
    [pscustomobject]@{Version=$Version;SourceRevision=$revision;DirtySource=$dirty;BuiltAtUtc=[DateTime]::UtcNow.ToString('O');Runtime='Windows Desktop + ASP.NET Core 10.0 x64 (framework-dependent)'} |
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
