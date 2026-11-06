param(
    [Parameter(Mandatory)][string]$Installer,
    [ValidateSet('Install','Upgrade','Uninstall')][string]$Action = 'Install',
    [switch]$AllowPreviousDocumentLayout
)
$ErrorActionPreference = 'Stop'
if ($AllowPreviousDocumentLayout -and $Action -ne 'Install') { throw 'Previous layout is permitted only for the initial upgrade fixture.' }
$repo = Split-Path $PSScriptRoot -Parent
$msi = (Resolve-Path -LiteralPath $Installer).Path
$com = New-Object -ComObject WindowsInstaller.Installer
$db = $com.OpenDatabase($msi,0)
function ReadProperty([string]$name) {
    $view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'")
    $null = $view.Execute(); $record = $view.Fetch(); $value = $record.StringData(1); $null = $view.Close(); return $value
}
if ((ReadProperty 'UpgradeCode') -ne '{9D379613-0BED-47BE-87BF-64796E08F8A0}') { throw 'Only isolated TestPackage installers may be tested with this script.' }
if ((ReadProperty 'ProductName') -ne 'Local RAG-Test') { throw 'Unexpected product name.' }
if ((ReadProperty 'ProductLanguage') -ne '1041') { throw 'Expected Japanese installer.' }
$productCode = ReadProperty 'ProductCode'
$work = Join-Path $repo '.local/installer-test'
$install = Join-Path $work 'application'
$data = Join-Path $work 'data'
New-Item -ItemType Directory -Force $data | Out-Null
$sentinel = Join-Path $data 'preserve.txt'
if (!(Test-Path -LiteralPath $sentinel)) { [IO.File]::WriteAllText($sentinel,'preserve-user-data') }
$before = (Get-FileHash -LiteralPath $sentinel).Hash
$operation = if ($Action -eq 'Uninstall') { '/x' } else { '/i' }
$log = Join-Path $work ($Action.ToLowerInvariant() + '-' + (Get-Date -Format yyyyMMdd-HHmmss) + '.log')
$location = if ($Action -eq 'Install') { " INSTALLFOLDER=`"$install`"" } else { '' }
$arguments = "$operation `"$msi`" /qn /norestart$location /l*v `"$log`""
$process = Start-Process msiexec.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "MSI $Action failed with $($process.ExitCode). See $log" }
if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $before) { throw 'User data changed.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Local RAG-Test/Local RAG-Test.lnk'
$related = $com.RelatedProducts('{9D379613-0BED-47BE-87BF-64796E08F8A0}')
$registrations = @(for ($i=0; $i -lt $related.Count; $i++) { $related.Item($i) })
if ($Action -eq 'Uninstall') {
    if (Test-Path -LiteralPath (Join-Path $install 'LocalRag.Desktop.exe')) { throw 'Application remains after uninstall.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Shortcut remains after uninstall.' }
    if ($registrations.Count -ne 0) { throw 'Product registration remains after uninstall.' }
} else {
    foreach ($file in @('LocalRag.Desktop.exe','LocalRag.Mcp.exe','coreclr.dll','hostfxr.dll','Documents/QuickStart.md','ThirdPartyNotices/WiX-5.0.2-source.zip','ThirdPartyNotices/WiX-5.0.2-LICENSE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $install $file))) { throw "Missing installed file: $file" }
    }
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut missing.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $install 'LocalRag.Desktop.exe')) { throw 'Incorrect shortcut target.' }
    if ($registrations.Count -ne 1 -or $registrations[0] -ne $productCode) { throw 'Upgrade left an unexpected product registration.' }
    if (!$AllowPreviousDocumentLayout -and (Test-Path -LiteralPath (Join-Path $install 'Documents/QueryEvaluationResults-2026-11-06.json'))) { throw 'Evaluation data must not be installed.' }
}
[pscustomobject]@{Action=$Action;ExitCode=$process.ExitCode;ProductCode=$productCode;UserDataPreserved=$true;InstallDirectory=$install;Log=$log} | ConvertTo-Json
