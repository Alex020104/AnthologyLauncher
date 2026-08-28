$ErrorActionPreference = "Stop"
$deploymentRoot = $PSScriptRoot
$apiPath = Join-Path $deploymentRoot "Services\CommunityApi\Anthology.Community.Api.exe"
$launcherPath = Join-Path $deploymentRoot "App\AnthologyLauncher.Next.exe"
$apiAddress = "http://127.0.0.1:5249"
$startedApi = $null

if (-not (Test-Path -LiteralPath $apiPath) -or -not (Test-Path -LiteralPath $launcherPath)) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "Portable-сборка неполна. Повторите публикацию проекта.",
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
            throw "Community API не запустился."
        }
    }

    $env:ANTHOLOGY_COMMUNITY_API = $apiAddress
    Remove-Item Env:ANTHOLOGY_GAME_ROOT -ErrorAction SilentlyContinue
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
