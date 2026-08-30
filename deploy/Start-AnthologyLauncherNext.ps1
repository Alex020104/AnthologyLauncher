$ErrorActionPreference = "Stop"
$deploymentRoot = $PSScriptRoot
$pendingUpdateRoot = Join-Path $deploymentRoot "Update\LauncherPending"
$pendingUpdateDescriptor = Join-Path $pendingUpdateRoot "launcher-update.json"
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

function Apply-PendingLauncherUpdate {
    if (-not (Test-Path -LiteralPath $pendingUpdateDescriptor -PathType Leaf)) {
        return
    }

    $descriptor = Get-Content -LiteralPath $pendingUpdateDescriptor -Raw | ConvertFrom-Json
    if ($descriptor.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($descriptor.payloadFile) -or [string]::IsNullOrWhiteSpace($descriptor.sha256)) {
        throw "Launcher update descriptor is invalid."
    }

    $payloadName = [System.IO.Path]::GetFileName([string]$descriptor.payloadFile)
    if ($payloadName -ne [string]$descriptor.payloadFile) {
        throw "Launcher update payload name is unsafe."
    }

    $payloadPath = Join-Path $pendingUpdateRoot $payloadName
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        throw "Launcher update payload was not found: $payloadName"
    }

    $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$descriptor.sha256).Trim().ToLowerInvariant()) {
        throw "Launcher update payload failed SHA-256 verification."
    }

    $updateRoot = Join-Path $deploymentRoot "Update"
    $operationId = [Guid]::NewGuid().ToString("N")
    $stagingRoot = Join-Path $updateRoot "LauncherStaging-$operationId"
    $backupRoot = Join-Path $updateRoot "LauncherBackup-$operationId"
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

    try {
        Expand-Archive -LiteralPath $payloadPath -DestinationPath $stagingRoot -Force
        $requiredAssembly = Join-Path $stagingRoot "App\AnthologyLauncher.Next.dll"
        if (-not (Test-Path -LiteralPath $requiredAssembly -PathType Leaf)) {
            throw "Launcher update payload does not contain AnthologyLauncher.Next.dll."
        }

        $installed = New-Object System.Collections.Generic.List[string]
        foreach ($source in Get-ChildItem -LiteralPath $stagingRoot -File -Recurse) {
            $relative = $source.FullName.Substring($stagingRoot.Length).TrimStart('\')
            if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains("..")) {
                throw "Launcher update contains an unsafe path."
            }

            $destination = Join-Path $deploymentRoot $relative
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                $backup = Join-Path $backupRoot $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backup -Force
            }

            Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
            $installed.Add($relative)
        }

        $stateRoot = Join-Path $deploymentRoot "Data"
        New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
        [pscustomobject]@{
            schemaVersion = 1
            launcherVersion = [string]$descriptor.launcherVersion
            releaseVersion = [string]$descriptor.releaseVersion
            appliedAt = [DateTimeOffset]::UtcNow.ToString("O")
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stateRoot "launcher-version.json") -Encoding UTF8

        Remove-Item -LiteralPath $pendingUpdateDescriptor -Force
        Remove-Item -LiteralPath $payloadPath -Force
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    catch {
        if (Test-Path -LiteralPath $backupRoot -PathType Container) {
            foreach ($backup in Get-ChildItem -LiteralPath $backupRoot -File -Recurse) {
                $relative = $backup.FullName.Substring($backupRoot.Length).TrimStart('\')
                $destination = Join-Path $deploymentRoot $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $backup.FullName -Destination $destination -Force
            }
        }
        throw
    }
}

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
    Apply-PendingLauncherUpdate

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
