$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'CodexQuotaTools'
$widget = Join-Path $installRoot 'quota-widget\QuotaWidget.exe'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'CodexQuotaWidget.lnk'
$desktopName = (-join @([char]0x6253,[char]0x5F00,[char]0x989D,[char]0x5EA6,[char]0x72B6,[char]0x6001,[char]0x680F)) + '.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) $desktopName
$shell = New-Object -ComObject WScript.Shell

function Read-ShortcutTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return $shell.CreateShortcut($Path).TargetPath
}

[pscustomobject]@{
    Installed = Test-Path -LiteralPath $widget -PathType Leaf
    InstallPath = $widget
    StartupShortcut = $startupLink
    StartupTarget = Read-ShortcutTarget $startupLink
    DesktopShortcut = $desktopLink
    DesktopTarget = Read-ShortcutTarget $desktopLink
} | Format-List

Get-Process -Name 'QuotaWidget' -ErrorAction SilentlyContinue |
    Select-Object Name, Id, StartTime, Path |
    Format-List

