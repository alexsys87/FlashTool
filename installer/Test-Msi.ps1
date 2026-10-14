# Checks MSI UI metadata and installed shortcut without opening the application.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [string]$LogDirectory = 'artifacts/msi-logs'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$install = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'Custom install path'
$logs = [IO.Path]::GetFullPath($LogDirectory)
if (Test-Path -LiteralPath $install) { throw 'Use a new, empty installation test directory.' }
New-Item -ItemType Directory -Force -Path $logs | Out-Null

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msi, 0)
function Get-MsiValue([string]$Query) {
    $view = $database.OpenView($Query)
    try {
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { return $null }
        try { return $record.StringData(1) }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
    } finally {
        $view.Close()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}
try {
    $productName = Get-MsiValue 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductName'''
    foreach ($dialog in @('WelcomeDlg', 'InstallDirDlg', 'BrowseDlg', 'VerifyReadyDlg', 'ExitDialog')) {
        if (-not (Get-MsiValue "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog`` = '$dialog'")) {
            throw "Missing MSI wizard dialog: $dialog"
        }
    }
    if ((Get-MsiValue 'SELECT `Value` FROM `Property` WHERE `Property` = ''WIXUI_INSTALLDIR''') -ne 'INSTALLFOLDER') {
        throw 'The directory selection dialog is not bound to INSTALLFOLDER.'
    }
    if ((Get-MsiValue 'SELECT `Argument` FROM `ControlEvent` WHERE `Dialog_` = ''WelcomeDlg'' AND `Control_` = ''Next'' AND `Event` = ''NewDialog'' AND `Condition` = ''NOT Installed'' ORDER BY `Ordering` DESC') -ne 'InstallDirDlg') {
        throw 'Welcome must lead directly to directory selection.'
    }
    if ((Get-MsiValue 'SELECT `Argument` FROM `ControlEvent` WHERE `Dialog_` = ''InstallDirDlg'' AND `Control_` = ''ChangeFolder'' AND `Event` = ''SpawnDialog''') -ne 'BrowseDlg') {
        throw 'The folder browse button is not connected to BrowseDlg.'
    }
    if ((Get-MsiValue 'SELECT `Icon_` FROM `Shortcut` WHERE `Shortcut` = ''StartMenuShortcut''') -ne 'FlashTool.exe') {
        throw 'The shortcut must reference the embedded icon with an exe extension.'
    }
    if ((Get-MsiValue 'SELECT `IconIndex` FROM `Shortcut` WHERE `Shortcut` = ''StartMenuShortcut''') -ne '0') {
        throw 'The shortcut icon index must be zero.'
    }
    if (-not (Get-MsiValue 'SELECT `Name` FROM `Icon` WHERE `Name` = ''FlashTool.exe''')) {
        throw 'The MSI does not contain the shortcut icon.'
    }
    Write-Host 'MSI wizard metadata verified: destination folder, browse dialog, navigation and embedded shortcut icon.'
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

function Invoke-Msi([string[]]$Arguments) {
    $process = Start-Process msiexec.exe -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "msiexec failed with exit code $($process.ExitCode). See $logs." }
}
$shortcutPath = $null
try {
    Invoke-Msi @('/i', "`"$msi`"", '/qn', '/norestart', "INSTALLFOLDER=`"$install`"", '/L*v', "`"$logs/install.log`"")
    $sourceFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    $installedFiles = @(Get-ChildItem -LiteralPath $install -File -Recurse)
    if ($sourceFiles.Count -ne $installedFiles.Count) { throw 'Installed file count does not match the publish directory.' }
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
        $destination = Join-Path $install $relative
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Missing installed file: $relative" }
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            throw "Installed file hash mismatch: $relative"
        }
    }
    Write-Host "MSI installation verified: $($sourceFiles.Count) files, SHA-256 matches; custom path with spaces."
    foreach ($folder in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs'))) {
        $candidate = Join-Path $folder "$productName/$productName.lnk"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $shortcutPath = $candidate; break }
    }
    if (-not $shortcutPath) { throw 'The installed Start menu shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    try {
        if ($shortcut.TargetPath -ne (Join-Path $install 'FlashTool.exe')) { throw 'Incorrect shortcut target.' }
        if ($shortcut.WorkingDirectory.TrimEnd('\') -ne $install.TrimEnd('\')) { throw 'Incorrect shortcut working directory.' }
        if ($shortcut.IconLocation -notmatch '^(.*),\s*0$') { throw 'The shortcut does not use an explicit icon at index zero.' }
        $iconPath = $Matches[1].Trim('"')
        if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "Shortcut icon file missing: $iconPath" }
        $iconBytes = [IO.File]::ReadAllBytes($iconPath)
        if ($iconBytes.Length -lt 6 -or [BitConverter]::ToUInt16($iconBytes, 0) -ne 0 -or [BitConverter]::ToUInt16($iconBytes, 2) -ne 1 -or [BitConverter]::ToUInt16($iconBytes, 4) -eq 0) {
            throw 'The Windows Installer icon cache does not contain valid ICO data.'
        }
        Write-Host "Start menu shortcut verified: target, working directory and valid cached ICO ($($shortcut.IconLocation))."
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
} finally {
    Invoke-Msi @('/x', "`"$msi`"", '/qn', '/norestart', '/L*v', "`"$logs/uninstall.log`"")
}
if (Test-Path -LiteralPath $install) { throw 'Installation directory remains after uninstall.' }
if ($shortcutPath -and (Test-Path -LiteralPath $shortcutPath)) { throw 'Start menu shortcut remains after uninstall.' }
Write-Host 'MSI uninstallation verified: application directory and shortcut removed.'
