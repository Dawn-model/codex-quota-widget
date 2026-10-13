$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$packageRoot = Split-Path -Parent $sourceDir
$source = Join-Path $sourceDir 'QuotaWidget.cs'
$manifest = Join-Path $sourceDir 'widget.manifest'
$output = Join-Path $packageRoot 'quota-widget\QuotaWidget.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

foreach ($required in @($source, $manifest, $compiler)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required build file is missing: $required"
    }
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$output" $source
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }

$testProcess = Start-Process -FilePath $output -ArgumentList '--self-test' -Wait -PassThru
if ($testProcess.ExitCode -ne 0) { throw "Self-test process failed with exit code $($testProcess.ExitCode)" }
$result = Join-Path (Split-Path -Parent $output) 'test-result.txt'
if (-not (Test-Path -LiteralPath $result)) { throw 'Self-test did not produce a result file.' }
$text = Get-Content -LiteralPath $result -Raw
if (-not $text.StartsWith('PASS:')) { throw "Self-test failed: $text" }

Write-Output $text
Write-Output "Build completed: $output"
