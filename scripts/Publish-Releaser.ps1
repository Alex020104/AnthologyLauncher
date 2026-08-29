[CmdletBinding()]
param(
    [string]$Destination = "A:\AnthologyReleaserNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)
if ($destinationRoot.StartsWith($sourceFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The standalone releaser directory cannot be inside Source."
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
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
& $dotnetPath publish (Join-Path $sourceRoot "src\Anthology.Releaser.App\Anthology.Releaser.App.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output (Join-Path $destinationRoot "App")
if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish Anthology Releaser Next."
}

Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Launch Anthology Releaser Next.cmd") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\RELEASER-README.txt") -Destination $destinationRoot -Force
Write-Host "Standalone releaser published: $destinationRoot"
