$ErrorActionPreference='Stop'
$dir=$env:CODEX_HOME
if ([string]::IsNullOrWhiteSpace($dir)) { $dir=Join-Path $env:USERPROFILE '.codex' }
$auth=Get-Content (Join-Path $dir 'auth.json') -Raw | ConvertFrom-Json
$headers=@{Authorization=('Bearer '+$auth.tokens.access_token);'ChatGPT-Account-Id'=$auth.tokens.account_id}
$output=@{availableCount=$null;credits=@();subscriptionExpiresAt=$null}
try {
    $result=Invoke-RestMethod -Uri 'https://chatgpt.com/backend-api/wham/rate-limit-reset-credits' -Headers $headers -TimeoutSec 15
    if ($null -ne $result.available_count) { $output.availableCount=[int]$result.available_count }
    $output.credits=@($result.credits | Where-Object { $_.status -eq 'available' } | ForEach-Object { $expiry=$null; if ($_.expires_at) { try {$expiry=[DateTimeOffset]::Parse([string]$_.expires_at).ToUnixTimeSeconds()} catch {} }; @{expiresAt=$expiry} })
} catch {}
try {
    $account=Invoke-RestMethod -Uri 'https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27' -Headers $headers -TimeoutSec 15
    $entry=$account.accounts.($auth.tokens.account_id)
    if (-not $entry) { $entry=$account.accounts.default }
    if ($entry.entitlement.has_active_subscription -and $entry.entitlement.expires_at) {
        $output.subscriptionExpiresAt=[DateTimeOffset]::Parse([string]$entry.entitlement.expires_at).ToUnixTimeSeconds()
    }
} catch {}
$output | ConvertTo-Json -Depth 5 -Compress
