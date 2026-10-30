$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$root = Split-Path -Parent $sourceDir
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = foreach ($name in @('UIAutomationClient','UIAutomationTypes','WindowsBase')) {
    $file = Get-ChildItem (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\$name") -Recurse -Filter "$name.dll" | Select-Object -First 1
    if (-not $file) { throw "Missing .NET reference: $name" }
    '/reference:' + $file.FullName
}
& $compiler /nologo /target:winexe /optimize+ "/win32manifest:$sourceDir\widget.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll $refs "/out:$root\quota-widget\QuotaWidget.exe" "$sourceDir\QuotaWidget.cs" "$sourceDir\NativeQuotaWake.cs"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Write-Output 'Build completed.'
