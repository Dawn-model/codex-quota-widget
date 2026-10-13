param([switch]$ProbeOnly, [switch]$LoadOnly)
$ErrorActionPreference = 'Stop'

$timerRoot = $PSScriptRoot
$settingsFile = Join-Path $timerRoot 'config.json'
$codexPath = ''
if (Test-Path -LiteralPath $settingsFile) {
    $loaded = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if ($loaded.codexPath) { $codexPath = [string]$loaded.codexPath }
}

function Find-CodexExe {
    if ($codexPath) {
        if (-not (Test-Path -LiteralPath $codexPath -PathType Leaf)) {
            throw 'Configured codexPath does not exist'
        }
        return $codexPath
    }

    $commands = @(Get-Command codex.exe -All -ErrorAction SilentlyContinue)
    foreach ($command in $commands) {
        if ($command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
            return $command.Source
        }
    }

    $appAlias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\codex.exe'
    if (Test-Path -LiteralPath $appAlias -PathType Leaf) { return $appAlias }

    $searchRoots = @(
        (Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Codex'),
        (Join-Path $env:ProgramFiles 'OpenAI\Codex')
    )
    $candidate = $searchRoots | Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter codex.exe -Recurse -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) {
        $packages = @(Get-AppxPackage -Name '*Codex*' -ErrorAction SilentlyContinue)
        foreach ($package in $packages) {
            $candidate = Get-ChildItem -LiteralPath $package.InstallLocation -Filter codex.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]resources[\\/]' } | Select-Object -First 1
            if ($candidate) { break }
        }
    }
    if (-not $candidate) { throw 'Codex CLI unavailable. Install or open Codex and sign in, then retry.' }
    return $candidate.FullName
}

function Invoke-QuotaRpc([string]$Method, $Params) {
    $script:rpcId++
    $id = $script:rpcId
    $request = @{ id = $id; method = $Method }
    if ($null -ne $Params) { $request.params = $Params }
    $script:server.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 10 -Compress))
    $script:server.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $lineTask = $script:server.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait(25000)) { throw 'Quota service timed out' }
        if (-not $lineTask.Result) { throw 'Quota service disconnected' }
        $message = $lineTask.Result | ConvertFrom-Json
        if ($message.id -eq $id) {
            if ($message.error) {
                throw ('Quota service rejected request: ' + $message.error.code + ' ' + $message.error.message)
            }
            return $message.result
        }
    }
    throw 'Quota service response unavailable'
}

function Start-QuotaServer([string]$Exe) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = 'app-server --listen stdio://'
    $info.WorkingDirectory = $timerRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    $script:serverErrorDrain = $process.StandardError.ReadToEndAsync()
    $script:server = $process
    $script:rpcId = 0
    [void](Invoke-QuotaRpc 'initialize' @{ clientInfo = @{ name = 'quota_widget'; title = 'Quota Widget'; version = '1.1.1' } })
    $process.StandardInput.WriteLine('{"method":"initialized","params":{}}')
    $process.StandardInput.Flush()
}

function Read-Quota {
    $result = Invoke-QuotaRpc 'account/rateLimits/read' $null
    $limits = $result.rateLimits
    if ($result.rateLimitsByLimitId -and $result.rateLimitsByLimitId.codex) {
        $limits = $result.rateLimitsByLimitId.codex
    }
    if (-not $limits -or -not $limits.primary) { throw 'Usage windows unavailable' }
    return $limits
}

if ($LoadOnly) { return }
if (-not $ProbeOnly) {
    throw 'This monitoring-only installation permits read-only quota probes only.'
}

$mutex = New-Object Threading.Mutex($false, 'Local\CodexQuotaWidgetProbeOyzy2')
$ownsMutex = $false
$script:server = $null
try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) { throw 'A quota probe is already running' }
    Start-QuotaServer (Find-CodexExe)
    $limits = Read-Quota
    [pscustomobject]@{
        planType = $limits.planType
        primary = $limits.primary
        secondary = $limits.secondary
    } | ConvertTo-Json -Depth 6
} finally {
    if ($script:server) {
        if (-not $script:server.HasExited) { $script:server.Kill() }
        $script:server.Dispose()
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
