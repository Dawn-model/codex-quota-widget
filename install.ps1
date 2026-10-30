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
$sourceCredits = Join-Path $packageRoot 'quota-widget\credit-details.ps1'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$installWidgetDir = Join-Path $installRoot 'quota-widget'
$installProbeDir = Join-Path $installRoot 'quota-timer'
$installedWidget = Join-Path $installWidgetDir 'QuotaWidget.exe'

foreach ($required in @($sourceWidget, $sourceProbe, $sourceConfig, $sourceCredits)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required package file is missing: $required"
    }
}

$backup = Join-Path $installRoot ('backups\widget-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $installWidgetDir) {
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    foreach ($name in @('QuotaWidget.exe','credit-details.ps1')) {
        $old = Join-Path $installWidgetDir $name
        if (Test-Path -LiteralPath $old -PathType Leaf) { Copy-Item -LiteralPath $old -Destination $backup }
    }
    $oldLink = Join-Path $StartupDirectory 'CodexQuotaWidget.lnk'
    if (Test-Path -LiteralPath $oldLink) { Copy-Item -LiteralPath $oldLink -Destination $backup }
    Write-Output "Previous widget backup: $backup"
}

$checkShell = New-Object -ComObject WScript.Shell
$checkDesktopName = (-join @([char]0x6253,[char]0x5F00,[char]0x989D,[char]0x5EA6,[char]0x72B6,[char]0x6001,[char]0x680F)) + '.lnk'
foreach ($link in @((Join-Path $StartupDirectory 'CodexQuotaWidget.lnk'), (Join-Path $DesktopDirectory $checkDesktopName))) {
    if ((Test-Path -LiteralPath $link) -and $checkShell.CreateShortcut($link).TargetPath -ne $installedWidget) {
        throw "Shortcut belongs to another program and will not be overwritten: $link"
    }
}

New-Item -ItemType Directory -Force -Path $installWidgetDir, $installProbeDir, $StartupDirectory, $DesktopDirectory | Out-Null
# Stage on the same volume before stopping the old widget. Replacing the file
# is atomic, so a sharing violation cannot leave a partially copied executable.
$stagedWidget = Join-Path $installWidgetDir ('.QuotaWidget-' + [guid]::NewGuid().ToString('N') + '.tmp')
$replaceBackup = $stagedWidget + ".old"
$wasRunning = $false
$replaced = $false
try {
    Copy-Item -LiteralPath $sourceWidget -Destination $stagedWidget
    Unblock-File -LiteralPath $stagedWidget
    $running = @(Get-Process -Name 'QuotaWidget' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedWidget })
    foreach ($process in $running) {
        try {
            $wasRunning = $true
            $process.Kill()
            if (-not $process.WaitForExit(15000)) { throw "Timed out waiting for widget process $($process.Id) to exit." }
        } catch [System.InvalidOperationException] { # Already exited.
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not $replaced) {
        try {
            if (Test-Path -LiteralPath $installedWidget -PathType Leaf) {
                [IO.File]::Replace($stagedWidget, $installedWidget, $replaceBackup)
            } else {
                [IO.File]::Move($stagedWidget, $installedWidget)
            }
            $replaced = $true
        } catch {
            $failure = $_.Exception
            while ($failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [System.IO.IOException]) { throw }
            if ([DateTime]::UtcNow -ge $deadline) { throw "Cannot replace the widget because a process still holds the file. Close programs using $installedWidget and run install.cmd again. Backup: $backup. Details: $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 300
        }
    }
} catch {
    if ($wasRunning -and -not $replaced -and -not $NoStart -and (Test-Path -LiteralPath $installedWidget -PathType Leaf)) {
        try { Start-Process -FilePath $installedWidget -WorkingDirectory $installWidgetDir } catch { Write-Warning 'Could not restart the previous widget; it remains on disk.' }
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stagedWidget -PathType Leaf) { Remove-Item -LiteralPath $stagedWidget -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force -ErrorAction SilentlyContinue }
}
$installedProbe = Join-Path $installProbeDir 'quota-timer.ps1'
$installedConfig = Join-Path $installProbeDir 'config.json'
if (-not (Test-Path -LiteralPath $installedProbe)) { Copy-Item -LiteralPath $sourceProbe -Destination $installedProbe; Unblock-File -LiteralPath $installedProbe }
if (-not (Test-Path -LiteralPath $installedConfig)) { Copy-Item -LiteralPath $sourceConfig -Destination $installedConfig; Unblock-File -LiteralPath $installedConfig }
$installedCredits = Join-Path $installWidgetDir 'credit-details.ps1'
Copy-Item -LiteralPath $sourceCredits -Destination $installedCredits -Force

# ZIP files downloaded from GitHub carry Mark-of-the-Web. Copy-Item preserves
# that alternate data stream, and RemoteSigned would then reject our unsigned
# local probe. The user has explicitly launched this installer, so remove the
# download marker only from the three files installed by this package.
foreach ($installedFile in @($installedWidget, $installedCredits)) {
    Unblock-File -LiteralPath $installedFile
}

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
Write-Output 'Downloaded-file markers were removed from installed package files.'

