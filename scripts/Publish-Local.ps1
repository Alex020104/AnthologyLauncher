[CmdletBinding()]
param(
    [string]$Destination = "A:\AnthologyLauncherNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$publishSafety = Join-Path $PSScriptRoot "Publish-Safety.ps1"
. $publishSafety

$destinationRoot = Resolve-AnthologyPublishDestination -Path $Destination
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)

if ($destinationRoot.Equals($sourceFullPath.TrimEnd([char[]]"\/"), [System.StringComparison]::OrdinalIgnoreCase) -or
    $destinationRoot.StartsWith($sourceFullPath.TrimEnd([char[]]"\/") + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
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

$stageRoot = New-AnthologySiblingWorkingDirectory -DestinationRoot $destinationRoot -Purpose "publish"
$keepWorkingDirectories = $false
try {
    foreach ($item in $projects) {
        $projectPath = Join-Path $sourceRoot $item.Project
        $outputPath = Resolve-AnthologyRelativePublishPath -Root $stageRoot -RelativePath $item.Output
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

    Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Start-AnthologyLauncherNext.ps1") -Destination $stageRoot -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Launch Anthology Next.cmd") -Destination $stageRoot -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\README.txt") -Destination $stageRoot -Force
    $installMediaSource = Join-Path $sourceRoot "deploy\InstallMedia"
    $installMediaDestination = Resolve-AnthologyRelativePublishPath -Root $stageRoot -RelativePath "App\InstallMedia"
    if (Test-Path -LiteralPath $installMediaSource) {
        New-Item -ItemType Directory -Path $installMediaDestination -Force | Out-Null
        Copy-Item -Path (Join-Path $installMediaSource "*") -Destination $installMediaDestination -Recurse -Force
    }

    $setupSource = Join-Path $sourceRoot "deploy\Setup"
    $setupDestination = Resolve-AnthologyRelativePublishPath -Root $stageRoot -RelativePath "App\Setup"
    if (Test-Path -LiteralPath $setupSource) {
        New-Item -ItemType Directory -Path $setupDestination -Force | Out-Null
        Copy-Item -Path (Join-Path $setupSource "*") -Destination $setupDestination -Recurse -Force
    }

    Copy-AnthologyPreservedAppData -DestinationRoot $destinationRoot -StagingRoot $stageRoot
    Invoke-AnthologyControlledReplacement `
        -DestinationRoot $destinationRoot `
        -StagingRoot $stageRoot `
        -RelativePaths @(
            "App",
            "Services\CommunityApi",
            "Start-AnthologyLauncherNext.ps1",
            "Launch Anthology Next.cmd",
            "README.txt"
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

Write-Host "Published: $destinationRoot"
