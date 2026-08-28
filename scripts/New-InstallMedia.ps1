[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Input,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PrivateKey,

    [Parameter(Mandatory = $true)]
    [string]$PublicKey,

    [string]$Version = "1.0.0",
    [string]$PackageId = "anthology-base",
    [string]$DisplayName = "Anthology Base Game",
    [string]$KeyId = "installation-production-01",
    [string]$Releaser,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$deploymentRoot = Split-Path -Parent $sourceRoot
if ([string]::IsNullOrWhiteSpace($Releaser)) {
    $Releaser = Join-Path $deploymentRoot "Tools\Releaser\Anthology.Releaser.exe"
}

$inputRoot = [System.IO.Path]::GetFullPath($Input)
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$privateKeyPath = [System.IO.Path]::GetFullPath($PrivateKey)
$publicKeyPath = [System.IO.Path]::GetFullPath($PublicKey)
$releaserPath = [System.IO.Path]::GetFullPath($Releaser)
if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) { throw "Input folder not found: $inputRoot" }
if (-not (Test-Path -LiteralPath $privateKeyPath -PathType Leaf)) { throw "Private key not found: $privateKeyPath" }
if (-not (Test-Path -LiteralPath $publicKeyPath -PathType Leaf)) { throw "Public key not found: $publicKeyPath" }
if (-not (Test-Path -LiteralPath $releaserPath -PathType Leaf)) { throw "Releaser not found: $releaserPath" }

$packagesRoot = Join-Path $outputRoot "packages"
$artifactName = "$PackageId.zip"
$artifactPath = Join-Path $packagesRoot $artifactName
$manifestPath = Join-Path $outputRoot "manifest.json"
$installPublicKeyPath = Join-Path $outputRoot "install.public.pem"
New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null

$arguments = @(
    "package", "create",
    "--input", $inputRoot,
    "--artifact", $artifactPath,
    "--manifest", $manifestPath,
    "--id", $PackageId,
    "--name", $DisplayName,
    "--version", $Version,
    "--kind", "Game",
    "--install-root", "game",
    "--private-key", $privateKeyPath,
    "--key-id", $KeyId,
    "--mirror", "bundle-file=bundle:///packages/$artifactName",
    "--channel", "install"
)
if ($Force) { $arguments += "--force" }

& $releaserPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Releaser failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath $publicKeyPath -Destination $installPublicKeyPath -Force

Write-Host "Install media: $outputRoot"
Write-Host "Copy this folder to App\InstallMedia before publishing the launcher."
Write-Host "Keep the private key outside Source, App and InstallMedia."
