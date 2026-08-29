[CmdletBinding()]
param(
    [string]$Destination = "A:\AnthologyCommunityServer"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourceFullPath = [System.IO.Path]::GetFullPath($sourceRoot)
if ($destinationRoot.StartsWith($sourceFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The community server directory cannot be inside Source."
}

$dotnetPath = "A:\AnthologyBuildTools\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "The .NET 10 SDK was not found."
    }
    $dotnetPath = $dotnet.Source
}

$appRoot = Join-Path $destinationRoot "App"
$dataRoot = Join-Path $destinationRoot "Data"
$toolsRoot = Join-Path $destinationRoot "Tools"
New-Item -ItemType Directory -Path $appRoot,$dataRoot,$toolsRoot -Force | Out-Null
& $dotnetPath publish (Join-Path $sourceRoot "src\Anthology.Community.Api\Anthology.Community.Api.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $appRoot
if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish Anthology Community Server."
}

$deployRoot = Join-Path $sourceRoot "deploy\CommunityServer"
Copy-Item -LiteralPath (Join-Path $deployRoot "appsettings.Production.json") -Destination $appRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Start-CommunityServer.cmd") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Open-CommunitySite.cmd") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "README.txt") -Destination $destinationRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Install-Service.ps1") -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Uninstall-Service.ps1") -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Backup-CommunityServer.ps1") -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Configure-LocalClients.ps1") -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Install-StartupTask.ps1") -Destination $toolsRoot -Force
Copy-Item -LiteralPath (Join-Path $deployRoot "Uninstall-StartupTask.ps1") -Destination $toolsRoot -Force

$tokenRoot = Join-Path $dataRoot "Config"
$tokenPath = Join-Path $tokenRoot "developer-token.txt"
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
    New-Item -ItemType Directory -Path $tokenRoot -Force | Out-Null
    $bytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    $token = ([System.BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
    [System.IO.File]::WriteAllText($tokenPath, $token, [System.Text.UTF8Encoding]::new($false))
}
Write-Host "Community Server published: $destinationRoot"
