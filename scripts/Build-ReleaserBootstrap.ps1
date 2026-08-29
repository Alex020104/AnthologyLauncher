[CmdletBinding()]
param(
    [string]$OutputDirectory = "A:\AnthologyDeployStage\ReleaserBootstrap"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $sourceRoot "deploy\ReleaserBootstrap\AnthologyReleaser.vcxproj"
$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio Build Tools не найдены."
}

$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installation)) {
    throw "Не найден комплект C++ x64 для Visual Studio."
}

$msbuild = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "MSBuild не найден: $msbuild"
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$arguments = @(
    "/nologo",
    "/m",
    "/p:Configuration=Release",
    "/p:Platform=x64",
    "/p:OutDir=$output\"
)
& $msbuild $project $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось собрать AnthologyReleaser.Next.exe."
}

$releaser = Join-Path $output "AnthologyReleaser.Next.exe"
if (-not (Test-Path -LiteralPath $releaser -PathType Leaf)) {
    throw "Сборка завершилась без AnthologyReleaser.Next.exe."
}

Write-Host "Built: $releaser"
