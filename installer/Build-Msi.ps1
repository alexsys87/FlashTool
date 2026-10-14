# Builds a self-contained x64 MSI from a published FlashTool directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('8', '10')][string]$DotNetMajor,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ProductVersion = '0.1.0',
    [string]$WixPath = 'wix',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = [version]$ProductVersion
if ($version.Major -gt 255 -or $version.Minor -gt 255 -or $version.Build -gt 65535) {
    throw 'MSI version limits: major/minor <= 255, build <= 65535.'
}
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'FlashTool.exe') -PathType Leaf)) {
    throw 'The publish directory must contain FlashTool.exe.'
}
if (-not $OutputPath) { $OutputPath = "artifacts/FlashTool-net$DotNetMajor-win-x64.msi" }
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$wxsPath = Join-Path $outputDirectory "FlashTool-net$DotNetMajor.wxs"
$upgradeCodes = @{
    '8' = '{0F2AF425-B1EC-43B1-80CA-BC0055A7BAA8}'
    '10' = '{C20489E1-942B-4717-8793-59DF9562B680}'
}
$ns = 'http://wixtoolset.org/schemas/v4/wxs'
$uiNs = 'http://wixtoolset.org/schemas/v4/wxs/ui'
$doc = [xml]"<Wix xmlns='$ns' xmlns:ui='$uiNs'/>"
function Add-Element($Parent, [string]$Name, [hashtable]$Attributes = @{}) {
    $node = $doc.CreateElement($Name, $ns)
    foreach ($key in $Attributes.Keys) { $node.SetAttribute($key, [string]$Attributes[$key]) }
    [void]$Parent.AppendChild($node)
    return $node
}
function Get-StableId([string]$Prefix, [string]$RelativePath) {
    $bytes = [Text.Encoding]::UTF8.GetBytes("net${DotNetMajor}/$($RelativePath.ToLowerInvariant())")
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    return "$Prefix$hash"
}
$package = Add-Element $doc.DocumentElement 'Package' @{
    Name = "FlashTool (.NET $DotNetMajor)"; Manufacturer = 'FlashTool'; Version = $ProductVersion
    UpgradeCode = $upgradeCodes[$DotNetMajor]; Scope = 'perMachine'; Language = '1033'
}
[void](Add-Element $package 'MajorUpgrade' @{ DowngradeErrorMessage = 'A newer version of this FlashTool variant is already installed.' })
[void](Add-Element $package 'MediaTemplate' @{ EmbedCab = 'yes' })
[void](Add-Element $package 'Icon' @{ Id = 'FlashTool.exe'; SourceFile = (Join-Path $PSScriptRoot '../src/FlashTool.App/Assets/app.ico') })
[void](Add-Element $package 'Property' @{ Id = 'ARPPRODUCTICON'; Value = 'FlashTool.exe' })
$wixUi = $doc.CreateElement('ui', 'WixUI', $uiNs)
$wixUi.SetAttribute('Id', 'WixUI_InstallDir')
$wixUi.SetAttribute('InstallDirectory', 'INSTALLFOLDER')
[void]$package.AppendChild($wixUi)
[void](Add-Element $package 'UIRef' @{ Id = 'WixUI_ErrorProgressText' })
# The repository has no product EULA. Skip WiX's placeholder license page.
# For NewDialog events Windows Installer uses the last matching event.
$ui = Add-Element $package 'UI'
[void](Add-Element $ui 'Publish' @{
    Dialog = 'WelcomeDlg'; Control = 'Next'; Event = 'NewDialog'; Value = 'InstallDirDlg'
    Order = '3'; Condition = 'NOT Installed'
})
[void](Add-Element $ui 'Publish' @{
    Dialog = 'InstallDirDlg'; Control = 'Back'; Event = 'NewDialog'; Value = 'WelcomeDlg'
    Order = '3'; Condition = '1'
})
$programFiles = Add-Element $package 'StandardDirectory' @{ Id = 'ProgramFiles64Folder' }
$install = Add-Element $programFiles 'Directory' @{ Id = 'INSTALLFOLDER'; Name = "FlashTool-net$DotNetMajor" }
$menu = Add-Element $package 'StandardDirectory' @{ Id = 'ProgramMenuFolder' }
$appMenu = Add-Element $menu 'Directory' @{ Id = 'ApplicationProgramsFolder'; Name = "FlashTool (.NET $DotNetMajor)" }
$feature = Add-Element $package 'Feature' @{ Id = 'MainFeature'; Title = 'FlashTool'; Level = '1' }
$directories = @{ '' = $install }
function Get-DirectoryNode([string]$RelativePath) {
    if ($directories.ContainsKey($RelativePath)) { return $directories[$RelativePath] }
    $parentPath = [IO.Path]::GetDirectoryName($RelativePath)
    if (-not $parentPath) { $parentPath = '' }
    $parentNode = Get-DirectoryNode $parentPath
    $node = Add-Element $parentNode 'Directory' @{
        Id = (Get-StableId 'D' $RelativePath); Name = [IO.Path]::GetFileName($RelativePath)
    }
    $directories[$RelativePath] = $node
    return $node
}
foreach ($file in (Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
    $parentPath = [IO.Path]::GetDirectoryName($relative)
    if (-not $parentPath) { $parentPath = '' }
    $parentNode = Get-DirectoryNode $parentPath
    $componentId = Get-StableId 'C' $relative
    $component = Add-Element $parentNode 'Component' @{ Id = $componentId; Guid = '*'; Bitness = 'always64' }
    $fileNode = Add-Element $component 'File' @{
        Id = (Get-StableId 'F' $relative); Source = $file.FullName; KeyPath = 'yes'
    }
    # The Start menu shortcut is authored below with an explicit exe target and icon index.
    [void](Add-Element $feature 'ComponentRef' @{ Id = $componentId })
}
$menuComponent = Add-Element $appMenu 'Component' @{ Id = 'MenuCleanup'; Guid = '*'; Bitness = 'always64' }
[void](Add-Element $menuComponent 'Shortcut' @{
    Id = 'StartMenuShortcut'; Name = "FlashTool (.NET $DotNetMajor)"
    Target = '[INSTALLFOLDER]FlashTool.exe'; WorkingDirectory = 'INSTALLFOLDER'
    Icon = 'FlashTool.exe'; IconIndex = '0'; Advertise = 'no'
})
[void](Add-Element $menuComponent 'RemoveFolder' @{ Id = 'RemoveAppMenu'; On = 'uninstall' })
[void](Add-Element $menuComponent 'RegistryValue' @{
    Root = 'HKCU'; Key = "Software\FlashTool\Installer\net$DotNetMajor"; Name = 'Installed'
    Type = 'integer'; Value = '1'; KeyPath = 'yes'
})
[void](Add-Element $feature 'ComponentRef' @{ Id = 'MenuCleanup' })
$doc.Save($wxsPath)
& $WixPath build $wxsPath -arch x64 -ext WixToolset.UI.wixext -culture en-US -o $output
if ($LASTEXITCODE -ne 0) { throw "WiX build failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'WiX did not create an MSI.' }
Write-Host "Created $output"
