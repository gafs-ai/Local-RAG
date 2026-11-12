# Copyright (C) 2026 GAFS Inc. SPDX-License-Identifier: GPL-3.0-or-later
# Input has passed LegacyExcelPreflight. VBA forceDisable alone does NOT disable XLM macros.
$ErrorActionPreference = 'Stop'
$previousIds = @(Get-Process EXCEL -ErrorAction SilentlyContinue | ForEach-Object Id)
$excel = $null; $workbooks = $null; $workbook = $null; $identityWorkbook = $null
$owned = $false; $exitCode = 11; $phase = 'setup'
try {
    if ($null -eq [Type]::GetTypeFromProgID('Excel.Application')) { $exitCode = 10; throw 'Excel is not installed' }
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ExcelWindowOwner { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId); }'
    $phase = 'starting'; $phase | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    $excel = New-Object -ComObject Excel.Application
    $phase = 'ownership'
    [uint32]$excelProcessId = 0
    $null = [ExcelWindowOwner]::GetWindowThreadProcessId([IntPtr]$excel.Hwnd, [ref]$excelProcessId)
    if (!$excelProcessId -or $previousIds -contains $excelProcessId) { $exitCode = 12; throw 'No isolated Excel process' }
    $process = Get-Process -Id $excelProcessId
    if ($process.ProcessName -ne 'EXCEL') { $exitCode = 12; throw 'Unexpected process' }
    $owned = $true
    $identity = @{ Id = $excelProcessId; StartUtcTicks = $process.StartTime.ToUniversalTime().Ticks } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'owned.tmp'), $identity, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath (Join-Path $PSScriptRoot 'owned.tmp') -Destination (Join-Path $PSScriptRoot 'owned.json')
    $phase = 'setup'
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.EnableEvents = $false
    $excel.AutomationSecurity = 3
    $excel.AskToUpdateLinks = $false
    $workbooks = $excel.Workbooks
    # Use a plain built-in worksheet, never an installed template; retain it to keep the owned session stable.
    $identityWorkbook = $workbooks.Add(-4167)
    $excel.Calculation = -4135
    $excel.CalculateBeforeSave = $false
    # Keep the prompt-suppressing sentinel within the legacy file-password length limit.
    $password = [Guid]::NewGuid().ToString('N').Substring(0, 15)
    $phase = 'open'; $phase | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    # BEGIN INPUT OPEN
    # Omitted Notify/AddToMru/CorruptLoad default to false/false/xlNormalLoad.
    # Passing the complete optional argument list fails in some Excel COM versions.
    $workbook = $workbooks.Open((Join-Path $PSScriptRoot 'input.xls'), 0, $true, [Type]::Missing,
        $password, $password, $true)
    # END INPUT OPEN
    $excel.Calculation = -4135
    $excel.CalculateBeforeSave = $false
    $phase = 'export'; $phase | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')
    $output = Join-Path $PSScriptRoot 'output.xlsx'
    # 51 = macro-free Open XML workbook. No calculation, link refresh, or source-file overwrite.
    $workbook.SaveAs($output, 51, [Type]::Missing, [Type]::Missing, $false, $false, 1, 2, $false,
        [Type]::Missing, [Type]::Missing, $true)
    if ((Get-Item -LiteralPath $output).Length -gt 67108864) { $exitCode = 13; throw 'Export too large' }
    $exitCode = 0
} catch {
    if ($exitCode -notin @(10, 12, 13)) { $exitCode = 11 }
    # Diagnostic metadata only: never persist an Office message containing a path, password or document text.
    try {
        $errorLine = $_.InvocationInfo.ScriptLineNumber
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        @{ Stage = $phase; HResult = $failure.HResult; Line = $errorLine } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $PSScriptRoot 'failure.json') -Encoding UTF8
    } catch {}
}
finally {
    # Never touch or quit an application until its newly-created PID has been established.
    if ($owned) {
        if ($null -ne $workbook) { try { $workbook.Close($false) } catch {} }
        if ($null -ne $identityWorkbook) { try { $identityWorkbook.Close($false) } catch {} }
        if ($null -ne $excel) { try { $excel.Quit() } catch {} }
    }
    if ($null -ne $workbook) { try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($workbook) } catch {} }
    if ($null -ne $identityWorkbook) { try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($identityWorkbook) } catch {} }
    if ($null -ne $workbooks) { try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($workbooks) } catch {} }
    if ($null -ne $excel) { try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel) } catch {} }
}
exit $exitCode
