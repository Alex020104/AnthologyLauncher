[CmdletBinding()]
param(
    [string]$Destination = "A:\AnthologyReleaserNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$publishSafety = Join-Path $PSScriptRoot "Publish-Safety.ps1"
. $publishSafety

$destinationRoot = Resolve-AnthologyPublishDestination -Path $Destination
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)
if ($destinationRoot.Equals($sourceFullPath.TrimEnd([char[]]"\/"), [System.StringComparison]::OrdinalIgnoreCase) -or
    $destinationRoot.StartsWith($sourceFullPath.TrimEnd([char[]]"\/") + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
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
$stageRoot = New-AnthologySiblingWorkingDirectory -DestinationRoot $destinationRoot -Purpose "publish"
$keepWorkingDirectories = $false
try {
    $stagedApp = Resolve-AnthologyRelativePublishPath -Root $stageRoot -RelativePath "App"
    New-Item -ItemType Directory -Path $stagedApp -Force | Out-Null
    & $dotnetPath publish (Join-Path $sourceRoot "src\Anthology.Releaser.App\Anthology.Releaser.App.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $stagedApp
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish Anthology Releaser Next."
    }

    Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Launch Anthology Releaser Next.cmd") -Destination $stageRoot -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\RELEASER-README.txt") -Destination $stageRoot -Force
    $bootstrapStage = Resolve-AnthologyRelativePublishPath -Root $stageRoot -RelativePath "_bootstrap"
    & (Join-Path $sourceRoot "scripts\Build-ReleaserBootstrap.ps1") -OutputDirectory $bootstrapStage
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build standalone Anthology Releaser Next entry point."
    }
    Copy-Item -LiteralPath (Join-Path $bootstrapStage "AnthologyReleaser.Next.exe") -Destination $stageRoot -Force

    Copy-AnthologyPreservedAppData -DestinationRoot $destinationRoot -StagingRoot $stageRoot
    Invoke-AnthologyControlledReplacement `
        -DestinationRoot $destinationRoot `
        -StagingRoot $stageRoot `
        -RelativePaths @(
            "App",
            "AnthologyReleaser.Next.exe",
            "Launch Anthology Releaser Next.cmd",
            "RELEASER-README.txt"
        )
}
catch {
    if ($_.Exception.Data.Contains("AnthologyKeepWorkingDirectories")) {
        $keepWorkingDirectories = [bool]$_.Exception.Data["AnthologyKeepWorkingDirectories"]
    }
    throw
}
finally {
    if (-not $keepWorkingDirectories -and (Test-Path -LiteralPath $stageRoot)) {
        Remove-AnthologySiblingWorkingDirectory -DestinationRoot $destinationRoot -WorkingDirectory $stageRoot
    }
}

Write-Host "Standalone releaser published: $destinationRoot"
