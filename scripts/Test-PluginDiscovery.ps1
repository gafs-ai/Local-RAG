param([Parameter(Mandatory)][string]$MarketplaceFile)
$ErrorActionPreference = 'Stop'
$marketplace = (Resolve-Path -LiteralPath $MarketplaceFile).Path
$info = [Diagnostics.ProcessStartInfo]::new((Get-Command codex -ErrorAction Stop).Source)
$info.ArgumentList.Add('app-server')
$info.ArgumentList.Add('--stdio')
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$probe = [Diagnostics.Process]::Start($info)
$stderr = $probe.StandardError.ReadToEndAsync()
function Send-Request($request) {
    $probe.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 10 -Compress))
    $probe.StandardInput.Flush()
}
function Read-Response($id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $read = $probe.StandardOutput.ReadLineAsync()
        if (!$read.Wait([TimeSpan]::FromSeconds(20))) { throw 'Diagnostic response timeout.' }
        if (!$read.Result) { throw 'Diagnostic connection closed.' }
        $message = $read.Result | ConvertFrom-Json
        if ($message.id -eq $id) {
            if ($message.error) { throw "Diagnostic request $id failed; no provider details printed." }
            return $message.result
        }
    }
    throw 'Diagnostic response deadline exceeded.'
}
try {
    # Read-only host diagnostics: no thread, model request, or document retrieval.
    Send-Request @{id=1;method='initialize';params=@{clientInfo=@{name='local_rag_diagnostic';version='1.0.0'};capabilities=@{experimentalApi=$true}}}
    $null = Read-Response 1
    Send-Request @{method='initialized';params=@{}}
    Send-Request @{id=2;method='plugin/read';params=@{pluginName='local-rag';marketplacePath=$marketplace}}
    $plugin = (Read-Response 2).plugin
    if (!$plugin.summary.enabled -or $plugin.mcpServers -notcontains 'localrag') { throw 'Local RAG MCP is not registered and enabled.' }
    Send-Request @{id=3;method='mcpServerStatus/list';params=@{serverName='localrag';detail='toolsAndAuthOnly'}}
    $servers = @((Read-Response 3).data)
    if ($servers.Count -ne 1 -or $servers[0].toolsError) { throw 'Local RAG MCP discovery failed.' }
    $names = @($servers[0].tools.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -ne 'fetch,search') { throw 'Unexpected MCP tool inventory.' }
    [pscustomobject]@{Version=$plugin.summary.localVersion;Enabled=$plugin.summary.enabled;Server='localrag';Tools=$names;ToolsError=$servers[0].toolsError} | ConvertTo-Json
} finally {
    # Only terminate the diagnostic process and children it created.
    if (!$probe.HasExited) { $probe.Kill($true); $probe.WaitForExit(5000) | Out-Null }
    $probe.Dispose()
}
