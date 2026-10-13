param(
    [switch]$NoStart,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'CodexQuotaTools'),
    [string]$StartupDirectory = [Environment]::GetFolderPath('Startup'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'

$packageRoot = $PSScriptRoot
$sourceWidget = Join-Path $packageRoot 'quota-widget\QuotaWidget.exe'
$sourceProbe = Join-Path $packageRoot 'quota-timer\quota-timer.ps1'
$sourceConfig = Join-Path $packageRoot 'quota-timer\config.json'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$installWidgetDir = Join-Path $installRoot 'quota-widget'
$installProbeDir = Join-Path $installRoot 'quota-timer'
$installedWidget = Join-Path $installWidgetDir 'QuotaWidget.exe'

foreach ($required in @($sourceWidget, $sourceProbe, $sourceConfig)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required package file is missing: $required"
    }
}

Get-Process -Name 'QuotaWidget' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $installedWidget } |
    Stop-Process -Force

New-Item -ItemType Directory -Force -Path $installWidgetDir, $installProbeDir, $StartupDirectory, $DesktopDirectory | Out-Null
Copy-Item -LiteralPath $sourceWidget -Destination $installedWidget -Force
Copy-Item -LiteralPath $sourceProbe -Destination (Join-Path $installProbeDir 'quota-timer.ps1') -Force
Copy-Item -LiteralPath $sourceConfig -Destination (Join-Path $installProbeDir 'config.json') -Force

$shell = New-Object -ComObject WScript.Shell
$startupLink = Join-Path $StartupDirectory 'CodexQuotaWidget.lnk'
$startupShortcut = $shell.CreateShortcut($startupLink)
$startupShortcut.TargetPath = $installedWidget
$startupShortcut.WorkingDirectory = $installWidgetDir
$startupShortcut.IconLocation = "$installedWidget,0"
$startupShortcut.Description = 'Read-only Codex quota widget'
$startupShortcut.Save()

$desktopName = (-join @([char]0x6253,[char]0x5F00,[char]0x989D,[char]0x5EA6,[char]0x72B6,[char]0x6001,[char]0x680F)) + '.lnk'
$desktopLink = Join-Path $DesktopDirectory $desktopName
$desktopShortcut = $shell.CreateShortcut($desktopLink)
$desktopShortcut.TargetPath = $installedWidget
$desktopShortcut.WorkingDirectory = $installWidgetDir
$desktopShortcut.IconLocation = "$installedWidget,0"
$desktopShortcut.Description = 'Open or restore Codex quota widget'
$desktopShortcut.Hotkey = 'CTRL+ALT+Q'
$desktopShortcut.Save()

if (-not $NoStart) {
    Start-Process -FilePath $installedWidget -WorkingDirectory $installWidgetDir
}

Write-Output 'Installation completed.'
Write-Output "Installed to: $installRoot"
Write-Output "Startup shortcut: $startupLink"
Write-Output "Desktop shortcut: $desktopLink"

