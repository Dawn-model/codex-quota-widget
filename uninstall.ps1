param([switch]$KeepFiles)
$ErrorActionPreference = 'Stop'

$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexQuotaTools')).TrimEnd('\')
$widget = Join-Path $expectedRoot 'quota-widget\QuotaWidget.exe'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'CodexQuotaWidget.lnk'
$desktopName = (-join @([char]0x6253,[char]0x5F00,[char]0x989D,[char]0x5EA6,[char]0x72B6,[char]0x6001,[char]0x680F)) + '.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) $desktopName
$shell = New-Object -ComObject WScript.Shell

Get-Process -Name 'QuotaWidget' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $widget } |
    Stop-Process -Force

foreach ($link in @($startupLink, $desktopLink)) {
    if (Test-Path -LiteralPath $link) {
        $target = $shell.CreateShortcut($link).TargetPath
        if ($target -eq $widget) {
            Remove-Item -LiteralPath $link -Force
        } else {
            Write-Warning "Shortcut was not created by this installation and was preserved: $link"
        }
    }
}

if (-not $KeepFiles) {
    foreach ($name in @('QuotaWidget.exe', 'credit-details.ps1')) {
        $file = Join-Path (Split-Path -Parent $widget) $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
}
Write-Output 'Timer, settings, state and backups were preserved.'

Write-Output 'Uninstallation completed.'

