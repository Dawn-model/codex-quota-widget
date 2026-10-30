$ErrorActionPreference='Stop'
$dir=$env:CODEX_HOME
if ([string]::IsNullOrWhiteSpace($dir)) { $dir=Join-Path $env:USERPROFILE '.codex' }
$auth=Get-Content (Join-Path $dir 'auth.json') -Raw | ConvertFrom-Json
$headers=@{Authorization=('Bearer '+$auth.tokens.access_token);'ChatGPT-Account-Id'=$auth.tokens.account_id}
$result=Invoke-RestMethod -Uri 'https://chatgpt.com/backend-api/wham/rate-limit-reset-credits' -Headers $headers -TimeoutSec 15
$items=@($result.credits | Where-Object { $_.status -eq 'available' } | ForEach-Object { $expiry = $null; if ($_.expires_at) { try { $expiry = [DateTimeOffset]::Parse($_.expires_at).ToUnixTimeSeconds() } catch {} }; @{expiresAt=$expiry} })
@{availableCount=[int]$result.available_count;credits=$items} | ConvertTo-Json -Depth 5 -Compress
