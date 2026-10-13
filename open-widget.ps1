$ErrorActionPreference = 'Stop'
$widget = Join-Path $env:LOCALAPPDATA 'CodexQuotaTools\quota-widget\QuotaWidget.exe'
if (-not (Test-Path -LiteralPath $widget -PathType Leaf)) {
    throw 'The quota widget is not installed. Run install.cmd first.'
}
Start-Process -FilePath $widget -WorkingDirectory (Split-Path -Parent $widget)

