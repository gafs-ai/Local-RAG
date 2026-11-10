# Copyright (C) 2026 GAFS Inc. SPDX-License-Identifier: GPL-3.0-or-later
# File conversion only. Never attaches to a user's existing Word session or enables macros.
$ErrorActionPreference = 'Stop'
$previousIds = @(Get-Process WINWORD -ErrorAction SilentlyContinue | ForEach-Object Id)
$word = $null; $document = $null; $identityDocument = $null; $owned = $false; $exitCode = 11
try {
    try { $word = New-Object -ComObject Word.Application } catch { exit 10 }
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class WordWindowOwner { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId); }'
    $identityDocument = $word.Documents.Add()
    [uint32]$wordProcessId = 0
    $null = [WordWindowOwner]::GetWindowThreadProcessId([IntPtr]$word.ActiveWindow.Hwnd, [ref]$wordProcessId)
    if (!$wordProcessId -or $previousIds -contains $wordProcessId) { throw 'No isolated Word process' }
    $process = Get-Process -Id $wordProcessId
    if ($process.ProcessName -ne 'WINWORD') { throw 'Unexpected process' }
    @{ Id = $wordProcessId; StartUtcTicks = $process.StartTime.ToUniversalTime().Ticks } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'owned.json') -Encoding UTF8
    $owned = $true
    # Keep the blank identity document open until conversion finishes; closing the last
    # document can let Word hand subsequent opens to another existing instance.
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $word.AutomationSecurity = 3
    $word.Options.UpdateLinksAtOpen = $false
    $word.Options.SaveNormalPrompt = $false
    'open' | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    # Supply an intentionally invalid password to fail encrypted input without a modal password prompt.
    $password = [Guid]::NewGuid().ToString('N')
    $document = $word.Documents.Open((Join-Path $PSScriptRoot 'input.doc'), $false, $true, $false, $password, $password,
        $false, $password, $password, 0, 0, $false, $false, 0, $true)
    'export' | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    $xml = $document.WordOpenXML
    if ($xml.Length -gt 67108864) { throw 'Export too large' }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'output.xml'), $xml, [Text.UTF8Encoding]::new($false))
    $exitCode = 0
} catch { $exitCode = 11 }
finally {
    'close' | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    if ($null -ne $document) { try { $document.Close(0) } catch {} ; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document) }
    if ($null -ne $identityDocument) { try { $identityDocument.Close(0) } catch {}; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($identityDocument) }
    if ($null -ne $word) { if ($owned) { try { $word.Quit(0) } catch {} }; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word) }
}
exit $exitCode
