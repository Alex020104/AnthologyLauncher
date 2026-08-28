[CmdletBinding()]
param(
    [string]$Destination = "E:\AnthologyLauncherNext"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)

if ($destinationRoot.StartsWith($sourceFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Каталог публикации не может находиться внутри Source."
}

$dotnet = Get-Command dotnet -ErrorAction Stop
$projects = @(
    @{ Project = "src\Anthology.Launcher\Anthology.Launcher.csproj"; Output = "App" },
    @{ Project = "src\Anthology.Community.Api\Anthology.Community.Api.csproj"; Output = "Services\CommunityApi" },
    @{ Project = "src\Anthology.Releaser\Anthology.Releaser.csproj"; Output = "Tools\Releaser" }
)

foreach ($item in $projects) {
    $projectPath = Join-Path $sourceRoot $item.Project
    $outputPath = Join-Path $destinationRoot $item.Output
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    & $dotnet.Source publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось опубликовать $($item.Project)."
    }
}

Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Start-AnthologyLauncherNext.ps1") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\Launch Anthology Next.cmd") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "deploy\README.txt") -Destination $destinationRoot -Force

Write-Host "Готово: $destinationRoot"
