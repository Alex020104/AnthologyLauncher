$ErrorActionPreference = "Stop"
$deploymentRoot = $PSScriptRoot
$apiPath = Join-Path $deploymentRoot "Services\CommunityApi\Anthology.Community.Api.exe"
$launcherPath = Join-Path $deploymentRoot "App\AnthologyLauncher.Next.exe"
$apiAddress = "http://127.0.0.1:5249"
$startedApi = $null
$env:ANTHOLOGY_DATA_ROOT = Join-Path $deploymentRoot "Data"
$env:ANTHOLOGY_GAME_ROOT = Split-Path -Parent $deploymentRoot
$modpackCandidates = @(
    (Join-Path (Split-Path -Parent $env:ANTHOLOGY_GAME_ROOT) "Modpack-1.5.3- Anthology 2.1"),
    (Join-Path (Split-Path -Parent $env:ANTHOLOGY_GAME_ROOT) "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT")
)
$env:ANTHOLOGY_MO2_ROOT = ($modpackCandidates |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ "ModOrganizer.exe") -PathType Leaf } |
    Select-Object -First 1)

if (-not (Test-Path -LiteralPath $apiPath) -or -not (Test-Path -LiteralPath $launcherPath)) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "Portable build is incomplete. Publish the project again.",
        "Anthology Launcher Next",
        "OK",
        "Error") | Out-Null
    exit 1
}

try {
    try {
        Invoke-RestMethod -Uri "$apiAddress/health" -TimeoutSec 1 | Out-Null
    }
    catch {
        $startedApi = Start-Process `
            -FilePath $apiPath `
            -ArgumentList "--urls", $apiAddress `
            -WorkingDirectory (Split-Path -Parent $apiPath) `
            -WindowStyle Hidden `
            -PassThru

        $apiReady = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 250
            try {
                Invoke-RestMethod -Uri "$apiAddress/health" -TimeoutSec 1 | Out-Null
                $apiReady = $true
                break
            }
            catch {
            }
        }

        if (-not $apiReady) {
            throw "Community API did not start."
        }
    }

    $env:ANTHOLOGY_COMMUNITY_API = $apiAddress
    $launcher = Start-Process -FilePath $launcherPath -WorkingDirectory (Split-Path -Parent $launcherPath) -PassThru
    $launcher.WaitForExit()
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        $_.Exception.Message,
        "Anthology Launcher Next",
        "OK",
        "Error") | Out-Null
    exit 1
}
finally {
    if ($null -ne $startedApi -and -not $startedApi.HasExited) {
        Stop-Process -Id $startedApi.Id
    }
}
