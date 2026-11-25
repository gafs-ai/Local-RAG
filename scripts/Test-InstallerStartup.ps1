param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Installer).Path
$engine = New-Object -ComObject WindowsInstaller.Installer
$db = $engine.OpenDatabase($path, 0)
function ReadField([string]$query, [int]$column = 1) {
    $view = $db.OpenView($query)
    try {
        $null = $view.Execute()
        $row = $view.Fetch()
        if ($null -ne $row) { $row.StringData($column) }
    } finally { $null = $view.Close() }
}
function Expect($actual, $expected, [string]$label) {
    if ($actual -cne $expected) { throw "$label expected '$expected', got '$actual'." }
}

$name = ReadField 'SELECT `Value` FROM `Property` WHERE `Property`=''ProductName'''
$rowQuery = 'SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry` WHERE `Registry`=''LogonRunCommand'''
if ($name -eq 'Local RAG-Test') {
    Expect (ReadField $rowQuery) $null 'Isolated test installer must omit logon registration'
    return
}
Expect $name 'Local RAG' 'Product'
Expect (ReadField $rowQuery 1) '1' 'Registry root (HKCU)'
Expect (ReadField $rowQuery 3) 'LocalRAG' 'Logon value name'
Expect (ReadField $rowQuery 5) 'LogonRegistration' 'Component owner'
Expect (ReadField 'SELECT `Condition` FROM `Component` WHERE `Component`=''LogonRegistration''') '' 'Must be installed even when opted out'
Expect (ReadField 'SELECT `KeyPath` FROM `Component` WHERE `Component`=''LogonRegistration''') 'LogonComponentMarker' 'Stable key path'
$locator = 'SELECT `Root`,`Key`,`Name`,`Type` FROM `RegLocator` WHERE `Signature_`=''FindLogonOptOut'''
Expect (ReadField $locator 1) '1' 'Preference hive (HKCU)'
Expect (ReadField $locator 2) 'Software\GAFS\LocalRAG\Preferences' 'Preference key'
Expect (ReadField $locator 3) 'StartAtLogonDisabled' 'Preference name'
Expect (ReadField $locator 4) '18' '64-bit raw DWORD registry search'
Expect (ReadField 'SELECT `Property` FROM `AppSearch` WHERE `Signature_`=''FindLogonOptOut''') 'LOCALRAG_LOGON_DISABLED' 'Opt-out search'
Expect (ReadField 'SELECT `Type` FROM `CustomAction` WHERE `Action`=''HonorLogonOptOut''') '51' 'Only a session property assignment can be tested'
$rule = ReadField 'SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action`=''HonorLogonOptOut'''
function Sequence([string]$action) { [int](ReadField "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='$action'") }
if ((Sequence 'AppSearch') -ge (Sequence 'HonorLogonOptOut') -or (Sequence 'InstallValidate') -ge (Sequence 'HonorLogonOptOut') -or
    (Sequence 'HonorLogonOptOut') -ge (Sequence 'RemoveRegistryValues') -or (Sequence 'RemoveRegistryValues') -ge (Sequence 'WriteRegistryValues')) {
    throw 'MSI must resolve the preference and the uninstall state before planning registry writes/removals.'
}

# Run only the MSI condition evaluator and property formatter. No installation or registry actions.
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$privateKey = 'Software\GAFS\LocalRAG\Installer\SkippedLogon'
$rawKey = ReadField $rowQuery 2
$rawCommand = ReadField $rowQuery 4
$session = $engine.OpenPackage($path, 1)
$defaultKey = $session.Property('LogonRegistryKey')
$session.Property('INSTALLFOLDER') = 'C:\Path With Spaces\'
foreach ($case in @(
    @{Disabled=''; Remove=''; Key=$runKey},
    @{Disabled='#1'; Remove=''; Key=$privateKey},
    @{Disabled='#1'; Remove='ALL'; Key=$runKey},
    @{Disabled=''; Remove='ALL'; Key=$runKey}
)) {
    $session.Property('LogonRegistryKey') = $defaultKey
    $session.Property('LOCALRAG_LOGON_DISABLED') = $case.Disabled
    $session.Property('REMOVE') = $case.Remove
    if ($session.EvaluateCondition($rule) -eq 1) {
        Expect ($session.DoAction('HonorLogonOptOut')) 1 'Set registry destination'
    }
    $record = $engine.CreateRecord(0)
    $record.StringData(0) = $rawKey
    Expect ($session.FormatRecord($record)) $case.Key 'Registry destination'
    $record.StringData(0) = $rawCommand
    Expect ($session.FormatRecord($record)) '"C:\Path With Spaces\LocalRag.Desktop.exe" --startup-tray' 'Tray launch command'
}
Expect (ReadField 'SELECT `Key` FROM `Registry` WHERE `Registry`=''RemoveUnusedLogonKey''') $privateKey 'Uninstall private key'
Expect (ReadField 'SELECT `Name` FROM `Registry` WHERE `Registry`=''RemoveUnusedLogonKey''') '-' 'Remove private key on uninstall'
Write-Output 'Verified startup registration, opt-out preservation and uninstall ownership in MSI.'
