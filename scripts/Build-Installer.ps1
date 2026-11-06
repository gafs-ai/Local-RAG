param(
    [Parameter(Mandatory)][string]$DistributionDirectory,
    [string]$WixPath = 'C:/Program Files/WiX Toolset v5.0/bin/wix.exe',
    [string]$UiExtension,
    [switch]$TestPackage
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$distribution = (Resolve-Path -LiteralPath $DistributionDirectory).Path
$build = Get-Content (Join-Path $distribution 'build-info.json') -Raw | ConvertFrom-Json
foreach ($required in @('LocalRag.Desktop.exe','LocalRag.Mcp.exe','coreclr.dll','hostfxr.dll','LICENSE','Documents/QuickStart.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $distribution $required))) { throw "Self-contained distribution missing: $required" }
}
if (!$UiExtension) { $UiExtension = Join-Path $repo '.local/.wix/extensions/WixToolset.UI.wixext/5.0.2/wixext5/WixToolset.UI.wixext.dll' }
if (!(Test-Path -LiteralPath $UiExtension)) { throw 'Install WixToolset.UI.wixext/5.0.2 in .local first (see Documents/Distribution.md).' }
$wixSource = Join-Path $repo '.local/installer-tools/WiX-5.0.2-source.zip'
if (!(Test-Path -LiteralPath $wixSource)) {
    New-Item -ItemType Directory -Force (Split-Path $wixSource -Parent) | Out-Null
    Invoke-WebRequest -Uri 'https://codeload.github.com/wixtoolset/wix/zip/aa65968c419420d32e3e1b647aea0082f5ca5b78' -OutFile $wixSource
}
if ((Get-FileHash -LiteralPath $wixSource -Algorithm SHA256).Hash -ne 'AC593BF21864D9AD4B44720A702DAE3651A51D199CFD5AEE080AAA1286AB72F1') { throw 'WiX source checksum mismatch.' }
$suffix = if ($TestPackage) { '-Test' } else { '' }
$product = "Local RAG$suffix"
$folder = "LocalRAG$suffix"
$upgradeCode = if ($TestPackage) { '9D379613-0BED-47BE-87BF-64796E08F8A0' } else { '7EA5C66F-14A3-4B18-8705-85A45E80B6D3' }
$out = Split-Path $distribution -Parent
$work = Join-Path $out "installer$suffix"
New-Item -ItemType Directory -Force $work | Out-Null
$msi = Join-Path $out "LocalRAG-$($build.Version)$suffix-win-x64.msi"
$version = ([version]($build.Version -split '-')[0]).ToString()
function EscapeXml([string]$value) { [Security.SecurityElement]::Escape($value) }
function StableId([string]$value) { 'id' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).Substring(0,32) }
function ComponentGuid([string]$value) { [guid]::new([byte[]]([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$upgradeCode/$value")))[0..15]).ToString('D') }
$directories = [Collections.Generic.List[string]]::new()
$components = [Collections.Generic.List[string]]::new()
$directoryIds = @{''='INSTALLFOLDER'}
foreach ($dir in Get-ChildItem -LiteralPath $distribution -Directory -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($distribution,$dir.FullName)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $id = StableId "directory:$relative"
    $directoryIds[$relative] = $id
    $directories.Add("<DirectoryRef Id=`"$($directoryIds[$parent])`"><Directory Id=`"$id`" Name=`"$(EscapeXml $dir.Name)`" /></DirectoryRef>")
}
foreach ($file in Get-ChildItem -LiteralPath $distribution -File -Recurse | Sort-Object FullName) {
    if ($file.Extension -in '.pdb','.wixpdb') { continue }
    $relative = [IO.Path]::GetRelativePath($distribution,$file.FullName)
    $id = StableId "file:$relative"
    $dirId = $directoryIds[[IO.Path]::GetDirectoryName($relative)]
    $components.Add("<Component Id=`"$id`" Directory=`"$dirId`" Guid=`"$(ComponentGuid $relative)`"><RegistryValue Root=`"HKCU`" Key=`"Software\GAFS\$folder\Installer`" Name=`"$id`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`"/><File Id=`"f$id`" Source=`"$(EscapeXml $file.FullName)`" /></Component>")
}
# WixUI embeds native code in the MSI. Ship its unchanged corresponding source and license.
foreach ($sourceFile in @($wixSource,(Join-Path $repo 'installer/WiX-5.0.2-LICENSE.txt'),(Join-Path $repo 'installer/WiX-5.0.2-NOTICE.txt'))) {
    $relative = 'ThirdPartyNotices\' + [IO.Path]::GetFileName($sourceFile)
    if (Test-Path -LiteralPath (Join-Path $distribution $relative)) { continue }
    $id = StableId "installer:$relative"
    $components.Add("<Component Id=`"$id`" Directory=`"$($directoryIds['ThirdPartyNotices'])`" Guid=`"$(ComponentGuid $relative)`"><RegistryValue Root=`"HKCU`" Key=`"Software\GAFS\$folder\Installer`" Name=`"$id`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`"/><File Id=`"f$id`" Source=`"$(EscapeXml $sourceFile)`" /></Component>")
}
# Per-user components need HKCU key paths and explicit removal of empty directories.
foreach ($entry in $directoryIds.GetEnumerator()) {
    $id = StableId "cleanup:$($entry.Key)"
    $components.Add("<Component Id=`"$id`" Directory=`"$($entry.Value)`" Guid=`"*`"><RegistryValue Root=`"HKCU`" Key=`"Software\GAFS\$folder\Installer`" Name=`"$id`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`"/><RemoveFolder Id=`"r$id`" On=`"uninstall`" /></Component>")
}
$rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0 Courier New;}}\f0\fs16 ' + ((Get-Content (Join-Path $distribution 'LICENSE') -Raw).Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')) + '}'
$license = Join-Path $work 'License.rtf'
[IO.File]::WriteAllText($license,$rtf,[Text.Encoding]::ASCII)
$icon = EscapeXml (Join-Path $repo 'Assets/Brand/local-rag.ico')
$source = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">
 <Package Name="$product" Manufacturer="GAFS Inc." Version="$version" UpgradeCode="$upgradeCode" Scope="perUser" Language="1041" Codepage="932">
  <SummaryInformation Description="$product $($build.Version)" />
  <MajorUpgrade AllowSameVersionUpgrades="yes" DowngradeErrorMessage="新しいバージョンが既にインストールされています。" />
  <MediaTemplate EmbedCab="yes" CompressionLevel="mszip" />
  <Property Id="ARPCOMMENTS" Value="PC内の文書を検索するLocal RAG。アンインストールしても設定・索引・モデル・元文書は残ります。" />
  <Property Id="ARPURLINFOABOUT" Value="https://github.com/gafs-ai/Local-RAG" />
  <Property Id="ARPPRODUCTICON" Value="AppIcon" />
  <Property Id="MSIRESTARTMANAGERCONTROL" Value="Disable" />
  <Property Id="INSTALLFOLDER" Secure="yes"><RegistrySearch Id="ReadInstallFolder" Root="HKCU" Key="Software\GAFS\$folder\Installer" Name="InstallDirectory" Type="raw" Bitness="always64" /></Property>
  <Property Id="WINDOWSBUILD"><RegistrySearch Id="ReadWindowsBuild" Root="HKLM" Key="SOFTWARE\Microsoft\Windows NT\CurrentVersion" Name="CurrentBuildNumber" Type="raw" Bitness="always64" /></Property>
  <Launch Condition="Installed OR (VersionNT64 AND WINDOWSBUILD &gt;= 22000)" Message="Windows 11（64ビット）が必要です。" />
  <Icon Id="AppIcon" SourceFile="$icon" />
  <StandardDirectory Id="LocalAppDataFolder"><Directory Id="ProgramsFolder" Name="Programs"><Directory Id="INSTALLFOLDER" Name="$folder" /></Directory></StandardDirectory>
  <StandardDirectory Id="ProgramMenuFolder"><Directory Id="MenuFolder" Name="$product" /></StandardDirectory>
  <Component Id="StartMenu" Directory="MenuFolder" Guid="*">
   <RegistryValue Root="HKCU" Key="Software\GAFS\$folder\Installer" Name="StartMenu" Type="integer" Value="1" KeyPath="yes" />
   <RegistryValue Root="HKCU" Key="Software\GAFS\$folder\Installer" Name="InstallDirectory" Type="string" Value="[INSTALLFOLDER]" />
   <Shortcut Id="AppShortcut" Name="$product" Target="[INSTALLFOLDER]LocalRag.Desktop.exe" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />
   <RemoveFolder Id="RemoveMenu" On="uninstall" />
  </Component>
  <Feature Id="MainFeature" Title="$product" Level="1"><ComponentRef Id="StartMenu"/><ComponentGroupRef Id="Payload" /></Feature>
  <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />
  <WixVariable Id="WixUILicenseRtf" Value="$(EscapeXml $license)" />
 </Package>
 <Fragment>$($directories -join "`n")<ComponentGroup Id="Payload">$($components -join "`n")</ComponentGroup></Fragment>
</Wix>
"@
$wxs = Join-Path $work 'LocalRAG.wxs'
[IO.File]::WriteAllText($wxs,$source,[Text.UTF8Encoding]::new($false))
& $WixPath build $wxs -arch x64 -culture ja-JP -ext $UiExtension -out $msi
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Get-FileHash -LiteralPath $msi -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path $_.Path -Leaf)" } | Set-Content "$msi.sha256" -Encoding ascii
Write-Output "Installer: $msi"
