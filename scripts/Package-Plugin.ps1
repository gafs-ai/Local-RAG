param(
    [Parameter(Mandatory)][string]$DistributionDirectory,
    [string]$OutputDirectory,
    [string]$DataDirectory,
    [ValidateSet('Desktop','Portable')][string]$Format = 'Desktop'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$distribution = (Resolve-Path -LiteralPath $DistributionDirectory).Path
$build = Get-Content (Join-Path $distribution 'build-info.json') -Raw | ConvertFrom-Json
if (!(Test-Path -LiteralPath (Join-Path $distribution 'LocalRag.Mcp.exe'))) { throw 'MCP executable is missing.' }
if ($DataDirectory -and ![IO.Path]::IsPathFullyQualified($DataDirectory)) { throw 'DataDirectory must be absolute.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/plugin-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$plugin = Join-Path $output 'local-rag'
if (Test-Path -LiteralPath $plugin) { throw 'Choose a fresh output directory.' }
$server = New-Item -ItemType Directory -Path (Join-Path $plugin 'server')
# Copy only published runtime files. Never include the application's data directory.
Get-ChildItem -LiteralPath $distribution -File | Where-Object {
    ($_.Extension -eq '.dll' -and $_.Name -notlike 'LocalRag.Desktop*') -or
    $_.Name -in 'LocalRag.Mcp.exe','LocalRag.Mcp.deps.json','LocalRag.Mcp.runtimeconfig.json','build-info.json'
} | Copy-Item -Destination $server.FullName
Copy-Item -LiteralPath (Join-Path $distribution 'ThirdPartyNotices') -Destination $plugin -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'COPYRIGHT') -Destination $plugin
$assets = New-Item -ItemType Directory -Path (Join-Path $plugin 'assets')
Copy-Item -LiteralPath (Join-Path $repo 'Assets/Brand/local-rag-512.png'),(Join-Path $repo 'Assets/Brand/local-rag-128.png') -Destination $assets.FullName
$manifest = [ordered]@{
    '$schema' = 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json'
    name = 'local-rag'; version = $(if ($build.SemanticVersion) { $build.SemanticVersion } else { $build.Version })
    description = 'このプラグイン単独では動作しません。Windows版Local RAG本体とLemonade Serverを準備し、文書を取り込んで本体を起動したまま利用します。導入ガイド: https://github.com/gafs-ai/Local-RAG/blob/main/Documents/QuickStart.md'
    homepage = 'https://github.com/gafs-ai/Local-RAG/blob/main/Documents/QuickStart.md'
    author = @{name='GAFS Inc.'}; license = 'GPL-3.0-or-later'
    extensions = @{'com.openai'=@{
        requires_local_executor=$true
        interface=@{displayName='Local RAG';shortDescription='ローカル文書検索（本体アプリが必要）';longDescription='プラグイン単独では動作しません。Windows 11用Local RAG本体をインストールし、起動したまま利用してください。[クイックスタートガイド](https://github.com/gafs-ai/Local-RAG/blob/main/Documents/QuickStart.md)';developerName='GAFS Inc.';capabilities=@('Read');logo='./assets/local-rag-512.png';composerIcon='./assets/local-rag-128.png'}
    }}
}
if ($Format -eq 'Portable') {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $plugin 'plugin.json') -Encoding utf8
} else {
    # The installed desktop runtime discovers bundled MCP servers through the compatibility
    # manifest. Do not add a portable root that shadows this declaration on that runtime.
    $compatibility = New-Item -ItemType Directory -Path (Join-Path $plugin '.codex-plugin')
    @{
        name=$manifest.name;version=$manifest.version;description=$manifest.description
        author=$manifest.author;license=$manifest.license;homepage=$manifest.homepage
        requires_local_executor=$true;skills='./skills/';mcpServers='./.mcp.json'
        interface=$manifest.extensions.'com.openai'.interface
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $compatibility.FullName 'plugin.json') -Encoding utf8
}
$bridgeArgs = @()
if ($DataDirectory) { $bridgeArgs = @('--data-dir',$DataDirectory) }
$mcp = @{
    mcpServers=@{localrag=@{type='stdio';command='${PLUGIN_ROOT}/server/LocalRag.Mcp.exe';args=$bridgeArgs;cwd='./'}}
}
if ($Format -eq 'Portable') {
    $mcp['$schema']='https://agent-plugins.org/schemas/1.0.0/mcp.schema.json'
    $mcpFile = 'mcp.json'
} else {
    $mcpFile = '.mcp.json'
    # This desktop runtime resolves cwd relative to the plugin root but does not
    # expand PLUGIN_ROOT here. Use the bundled apphost with a plugin-relative cwd.
    $mcp.mcpServers.localrag.command = './server/LocalRag.Mcp.exe'
    $mcp.mcpServers.localrag.args = $bridgeArgs
    $mcp.mcpServers.localrag.cwd = './'
    $mcp.mcpServers.localrag.tool_timeout_sec = 120
}
$mcp | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $plugin $mcpFile) -Encoding utf8
$skill = New-Item -ItemType Directory -Path (Join-Path $plugin 'skills/search-local-documents')
Copy-Item -LiteralPath (Join-Path $repo 'plugins/local-rag/skills/search-local-documents/SKILL.md') -Destination $skill.FullName
@'
# Local RAG desktop plugin

このプラグイン単独では動作しません。Windows 11 x64版Local RAG本体とLemonade Serverを先に準備してください。

[クイックスタートガイド](https://github.com/gafs-ai/Local-RAG/blob/main/Documents/QuickStart.md)

Start the Local RAG desktop application and complete ingestion before using this plugin. Both processes must run as the same Windows user. The self-contained distribution includes the .NET runtime.

The plugin uses a local stdio bridge and does not require an OpenAI API key or API Platform tunnel. Allowed document content is returned to ChatGPT. Browser-only and mobile execution are not supported by this package. Workspace policy and desktop plugin availability must be checked in the target ChatGPT Business workspace.

The package contains runtime dependencies and licenses, but no models, indexed documents, credentials, or database. See the application distribution Documents/MCPConnection.md for setup and limitations.
'@ | Set-Content (Join-Path $plugin 'README.md') -Encoding utf8
$marketplace = New-Item -ItemType Directory -Path (Join-Path $output '.agents/plugins')
@{name='local-rag-local';interface=@{displayName='Local RAG local packages'};plugins=@(@{name='local-rag';source=@{source='local';path='./local-rag'}})} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $marketplace.FullName 'marketplace.json') -Encoding utf8
Compress-Archive -Path $plugin -DestinationPath (Join-Path $output 'local-rag.zip')
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'local-rag.zip'))
try {
    $requiredManifest = if ($Format -eq 'Desktop') { 'local-rag/.codex-plugin/plugin.json' } else { 'local-rag/plugin.json' }
    foreach ($required in @($requiredManifest,"local-rag/$mcpFile",'local-rag/skills/search-local-documents/SKILL.md','local-rag/server/LocalRag.Mcp.exe','local-rag/assets/local-rag-512.png','local-rag/assets/local-rag-128.png')) {
        if (!$archive.GetEntry($required)) { throw "Required plugin file missing from archive: $required" }
    }
} finally { $archive.Dispose() }
Get-FileHash (Join-Path $output 'local-rag.zip') -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  local-rag.zip" } |
    Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Plugin: $plugin"
Write-Output "Marketplace: $output"
if ($DataDirectory) { Write-Warning 'This package targets a custom data directory and must not be distributed to other users.' }
