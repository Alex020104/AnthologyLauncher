[CmdletBinding()]
param(
    [string]$Destination = "E:\AnthologyLauncherNext\DeveloperDemo"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$deploymentRoot = Split-Path -Parent $sourceRoot
$releaser = Join-Path $deploymentRoot "Tools\Releaser\Anthology.Releaser.exe"
if (-not (Test-Path -LiteralPath $releaser)) {
    throw "Publish the portable build first: scripts\Publish-Local.ps1"
}

$demoRoot = [System.IO.Path]::GetFullPath($Destination)
$payloadRoot = Join-Path $demoRoot "Payload"
$releaseRoot = Join-Path $demoRoot "Release"
$keysRoot = Join-Path $demoRoot "Keys"
$gameRoot = Join-Path $demoRoot "SandboxGame"
$artifactPath = Join-Path $releaseRoot "anthology-demo.zip"
$manifestPath = Join-Path $releaseRoot "manifest.json"
$privateKey = Join-Path $keysRoot "demo.private.pem"
$publicKey = Join-Path $keysRoot "demo.public.pem"

New-Item -ItemType Directory -Path (Join-Path $payloadRoot "gamedata\configs") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $gameRoot "bin") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $payloadRoot "gamedata\configs\anthology-next-demo.ltx") -Encoding UTF8 -Value "[anthology_next_demo]`ninstalled = true"
Set-Content -LiteralPath (Join-Path $gameRoot "fsgame.ltx") -Encoding UTF8 -Value '; Safe Anthology Launcher Next sandbox'

if (-not (Test-Path -LiteralPath $privateKey)) {
    & $releaser keys generate --private $privateKey --public $publicKey
    if ($LASTEXITCODE -ne 0) { throw "Failed to create demo keys." }
}

$artifactUri = ([Uri]$artifactPath).AbsoluteUri
& $releaser package create `
    --input $payloadRoot `
    --artifact $artifactPath `
    --manifest $manifestPath `
    --id anthology-demo `
    --name "Anthology Demo Update" `
    --version "1.0.0" `
    --kind Game `
    --install-root game `
    --private-key $privateKey `
    --key-id demo-local-01 `
    --mirror "local-file=$artifactUri" `
    --channel next `
    --force
if ($LASTEXITCODE -ne 0) { throw "Failed to create the demo release." }

Write-Host "Demo is ready. The real game was not modified."
Write-Host "Game:     $gameRoot"
Write-Host "Manifest: $manifestPath"
Write-Host "Key:      $publicKey"
