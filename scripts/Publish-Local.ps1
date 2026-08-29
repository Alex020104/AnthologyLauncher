[CmdletBinding()]
param(
    [string]$Destination = "A:\AnthologyLauncherNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)

if ($destinationRoot.StartsWith($sourceFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The publish directory cannot be inside Source."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$portableDotnet = "A:\AnthologyBuildTools\dotnet\dotnet.exe"
if ($null -eq $dotnet -or -not (& $dotnet.Source --list-sdks | Select-String -SimpleMatch "10.0.400")) {
    if (-not (Test-Path -LiteralPath $portableDotnet -PathType Leaf)) {
        throw "The .NET 10.0.400 SDK was not found. Install it or place the portable SDK at $portableDotnet."
    }
    $dotnetPath = $portableDotnet
}
else {
    $dotnetPath = $dotnet.Source
}
$projects = @(
    @{ Project = "src\Anthology.Launcher\Anthology.Launcher.csproj"; Output = "App" },
    @{ Project = "src\Anthology.Community.Api\Anthology.Community.Api.csproj"; Output = "Services\CommunityApi" }
)

foreach ($item in $projects) {
    $projectPath = Join-Path $sourceRoot $item.Project
    $outputPath = Join-Path $destinationRoot $item.Output
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    & $dotnetPath publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $($item.Project)."
    }
}

Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Start-AnthologyLauncherNext.ps1") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Launch Anthology Next.cmd") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\README.txt") -Destination $destinationRoot -Force
$installMediaSource = Join-Path $sourceRoot "deploy\InstallMedia"
$installMediaDestination = Join-Path $destinationRoot "App\InstallMedia"
if (Test-Path -LiteralPath $installMediaSource) {
    New-Item -ItemType Directory -Path $installMediaDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $installMediaSource "*") -Destination $installMediaDestination -Recurse -Force
}

$setupSource = Join-Path $sourceRoot "deploy\Setup"
$setupDestination = Join-Path $destinationRoot "App\Setup"
if (Test-Path -LiteralPath $setupSource) {
    New-Item -ItemType Directory -Path $setupDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $setupSource "*") -Destination $setupDestination -Recurse -Force
}

Write-Host "Published: $destinationRoot"
