[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serverRoot = Split-Path -Parent $PSScriptRoot
$tokenPath = Join-Path $serverRoot "Data\Config\developer-token.txt"
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
    throw "Developer token is missing. Start the server first."
}
$headers = @{ "X-Anthology-Developer-Token" = (Get-Content -LiteralPath $tokenPath -Raw).Trim() }
$result = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5249/api/v1/admin/backups" -Headers $headers
Write-Host "Backup: $($result.path)"
