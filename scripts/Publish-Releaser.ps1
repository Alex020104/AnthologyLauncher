[CmdletBinding()]
param(
    [string]$Destination = "E:\AnthologyReleaserNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)
if ($destinationRoot.StartsWith($sourceFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The standalone releaser directory cannot be inside Source."
}

$dotnet = Get-Command dotnet -ErrorAction Stop
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
& $dotnet.Source publish (Join-Path $sourceRoot "src\Anthology.Releaser.App\Anthology.Releaser.App.csproj") `
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
