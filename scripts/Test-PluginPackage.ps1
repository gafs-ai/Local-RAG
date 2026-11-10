param([Parameter(Mandatory)][string]$PluginDirectory)
$ErrorActionPreference = 'Stop'
$plugin = (Resolve-Path -LiteralPath $PluginDirectory).Path
$manifestPath = Join-Path $plugin '.codex-plugin/plugin.json'
$mcpPath = Join-Path $plugin '.mcp.json'
if (!(Test-Path -LiteralPath $manifestPath)) { $manifestPath = Join-Path $plugin 'plugin.json'; $mcpPath = Join-Path $plugin 'mcp.json' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.description -notmatch '単独では動作しません' -or $manifest.homepage -ne 'https://github.com/gafs-ai/Local-RAG/blob/main/Documents/QuickStart.md') { throw 'Missing standalone requirement or quick-start link.' }
$config = (Get-Content -LiteralPath $mcpPath -Raw | ConvertFrom-Json).mcpServers.localrag
if ($config.cwd -notmatch '^(\./|\$\{PLUGIN_(ROOT|DATA)\}($|/))') {
    throw 'MCP cwd must use a host-supported plugin-relative path (for example ./); a bare dot is rejected.'
}
$exe = $config.command.Replace('${PLUGIN_ROOT}',$plugin)
if (![IO.Path]::IsPathFullyQualified($exe)) { $exe = Join-Path $plugin $exe }
$repo = Split-Path $PSScriptRoot -Parent
$data = Join-Path $repo '.local/plugin-package-test-data'
New-Item -ItemType Directory -Force $data | Out-Null
'{"Mcp":{"Enabled":false}}' | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
$info = [Diagnostics.ProcessStartInfo]::new($exe)
$info.WorkingDirectory = $plugin
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
$info.Environment['DOTNET_ROOT'] = Join-Path $data 'no-installed-runtime'
$info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.ArgumentList.Add('--data-dir'); $info.ArgumentList.Add($data)
$probe = [Diagnostics.Process]::Start($info)
$stderr = $probe.StandardError.ReadToEndAsync()
function Request($payload) {
    $probe.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 12 -Compress)); $probe.StandardInput.Flush()
    if (!$payload.ContainsKey('id')) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $read = $probe.StandardOutput.ReadLineAsync()
        if (!$read.Wait([TimeSpan]::FromSeconds(20)) -or !$read.Result) { throw 'MCP response timeout or process exit.' }
        $response = $read.Result | ConvertFrom-Json
        if ($response.id -eq $payload.id) {
            if ($response.error) { throw 'MCP protocol error.' }
            return $response.result
        }
    }
    throw 'MCP response deadline exceeded.'
}
try {
    $null = Request @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='distribution-test';version='1.0'}}}
    Request @{jsonrpc='2.0';method='notifications/initialized'}
    $tools = (Request @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}).tools.name | Sort-Object
    if (($tools -join ',') -ne 'fetch,search') { throw 'Unexpected tool list.' }
    $result = Request @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='search';arguments=@{query='distribution test';limit=1}}}
    if (($result | ConvertTo-Json -Depth 10) -notmatch 'retrieval_unavailable') { throw 'Unavailable local app should not be reported as empty results.' }
    [pscustomobject]@{Version=$manifest.version;Tools=$tools;AppUnavailableHandled=$true;SelfContainedLaunch=$true} | ConvertTo-Json
} finally {
    $probe.StandardInput.Close()
    if (!$probe.WaitForExit(5000)) { $probe.Kill($true); $probe.WaitForExit(5000) | Out-Null }
    $probe.Dispose()
}
