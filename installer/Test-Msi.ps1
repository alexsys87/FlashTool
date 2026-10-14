# Installs only the generated application (never opens or writes physical disks).
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
$install = [IO.Path]::GetFullPath($InstallDirectory)
$logs = [IO.Path]::GetFullPath($LogDirectory)
if (Test-Path -LiteralPath $install) { throw 'Use a new, empty installation test directory.' }
New-Item -ItemType Directory -Force -Path $logs | Out-Null
function Invoke-Msi([string[]]$Arguments) {
    $process = Start-Process msiexec.exe -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "msiexec failed with exit code $($process.ExitCode). See $logs." }
}
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
    Write-Host "MSI installation verified: $($sourceFiles.Count) files, SHA-256 matches."
} finally {
    Invoke-Msi @('/x', "`"$msi`"", '/qn', '/norestart', '/L*v', "`"$logs/uninstall.log`"")
}
if (Test-Path -LiteralPath $install) { throw 'Installation directory remains after uninstall.' }
Write-Host 'MSI uninstallation verified.'
