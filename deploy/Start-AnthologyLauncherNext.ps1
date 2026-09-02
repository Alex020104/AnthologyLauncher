[CmdletBinding()]
param(
    [ValidateRange(0, 2147483647)]
    [int]$RestartAfterProcessId = 0,

    [ValidateRange(0, 9223372036854775807)]
    [long]$RestartAfterProcessStartTimeUtcTicks = 0,

    [string]$RestartAfterProcessPath = ""
)

$ErrorActionPreference = "Stop"
$deploymentRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
$updateRoot = Join-Path $deploymentRoot "Update"
$pendingUpdateRoot = Join-Path $updateRoot "LauncherPending"
$pendingUpdateDescriptor = Join-Path $pendingUpdateRoot "launcher-update.json"
$bootstrapLockPath = Join-Path $updateRoot "launcher-bootstrap.lock"
$apiOwnershipPath = Join-Path $updateRoot "community-api-owner.json"
$apiPath = Join-Path $deploymentRoot "Services\CommunityApi\Anthology.Community.Api.exe"
$launcherPath = Join-Path $deploymentRoot "App\AnthologyLauncher.Next.exe"
$apiAddress = "http://127.0.0.1:5249"
$script:startedApi = $null

function Remove-BootstrapPathBestEffort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [switch]$Recurse
    )

    try {
        if (Test-Path -LiteralPath $LiteralPath) {
            Remove-Item -LiteralPath $LiteralPath -Force -Recurse:$Recurse -ErrorAction Stop
        }
    }
    catch {
        # Cleanup must never turn a committed update into a rollback attempt.
    }
}

function Write-BootstrapJsonAtomically {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $directory = Split-Path -Parent $LiteralPath
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($LiteralPath), [Guid]::NewGuid().ToString("N"))
    $replaceBackupPath = Join-Path $directory (".{0}.{1}.replaced" -f [System.IO.Path]::GetFileName($LiteralPath), [Guid]::NewGuid().ToString("N"))
    try {
        $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $LiteralPath, $replaceBackupPath)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $LiteralPath)
        }
    }
    finally {
        Remove-BootstrapPathBestEffort -LiteralPath $temporaryPath
        Remove-BootstrapPathBestEffort -LiteralPath $replaceBackupPath
    }
}

function Install-BootstrapFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    $destinationDirectory = Split-Path -Parent $Destination
    [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
    $temporaryPath = Join-Path $destinationDirectory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($Destination), [Guid]::NewGuid().ToString("N"))
    $replaceBackupPath = Join-Path $destinationDirectory (".{0}.{1}.replaced" -f [System.IO.Path]::GetFileName($Destination), [Guid]::NewGuid().ToString("N"))
    try {
        Copy-Item -LiteralPath $Source -Destination $temporaryPath -Force
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $Destination, $replaceBackupPath)
        }
        elseif (Test-Path -LiteralPath $Destination) {
            throw "Launcher update cannot replace a directory: $Destination"
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Destination)
        }
    }
    finally {
        Remove-BootstrapPathBestEffort -LiteralPath $temporaryPath
        Remove-BootstrapPathBestEffort -LiteralPath $replaceBackupPath
    }
}

function Test-CanonicalPathEquals {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathIsUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    return $canonicalPath.StartsWith($canonicalRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoDestinationReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $deployment = [System.IO.DirectoryInfo]::new($deploymentRoot)
    $current = [System.IO.DirectoryInfo]::new((Split-Path -Parent $Destination))
    while ($null -ne $current -and -not (Test-CanonicalPathEquals -Left $current.FullName -Right $deployment.FullName)) {
        if ($current.Exists -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Launcher update destination traverses a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }

    if ($null -eq $current) {
        throw "Launcher update destination escaped the deployment root."
    }

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $destinationItem = Get-Item -LiteralPath $Destination -Force
        if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Launcher update cannot replace a reparse point: $Destination"
        }
    }
}

function Enter-BootstrapLock {
    param([Parameter(Mandatory = $true)][int]$TimeoutMilliseconds)

    [System.IO.Directory]::CreateDirectory($updateRoot) | Out-Null
    $updateDirectory = Get-Item -LiteralPath $updateRoot -Force
    if (($updateDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Launcher Update directory cannot be a reparse point."
    }
    $startedAt = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            return [System.IO.File]::Open(
                $bootstrapLockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        }
        catch [System.IO.IOException] {
            if ($startedAt.ElapsedMilliseconds -ge $TimeoutMilliseconds) {
                return $null
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Wait-RestartTarget {
    if ($RestartAfterProcessId -le 0) {
        return
    }

    if ($RestartAfterProcessStartTimeUtcTicks -le 0 -or [string]::IsNullOrWhiteSpace($RestartAfterProcessPath)) {
        throw "Restart target identity is incomplete. Start the launcher manually through AnomalyLauncher.exe."
    }

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($RestartAfterProcessId)
    }
    catch [System.ArgumentException] {
        return
    }

    try {
        if ($process.HasExited) {
            return
        }

        $actualStartTicks = $process.StartTime.ToUniversalTime().Ticks
        $actualPath = $process.MainModule.FileName
        if ($actualStartTicks -ne $RestartAfterProcessStartTimeUtcTicks -or
            -not (Test-CanonicalPathEquals -Left $actualPath -Right $RestartAfterProcessPath)) {
            throw "Restart target identity no longer matches the launcher process."
        }

        if (-not $process.WaitForExit(60000)) {
            throw "Launcher did not exit within 60 seconds; the update was not applied."
        }
        Start-Sleep -Milliseconds 250
    }
    finally {
        $process.Dispose()
    }
}

function Set-BootstrapEnvironment {
    $currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
    try {
        $env:ANTHOLOGY_LAUNCHER_ROOT = $deploymentRoot
        $env:ANTHOLOGY_LAUNCHER_BOOTSTRAPPED = "1"
        $env:ANTHOLOGY_LAUNCHER_BOOTSTRAP_PID = [string]$PID
        $env:ANTHOLOGY_LAUNCHER_BOOTSTRAP_STARTED_AT_UTC_TICKS = [string]$currentProcess.StartTime.ToUniversalTime().Ticks
        $env:ANTHOLOGY_LAUNCHER_BOOTSTRAP_PROCESS_PATH = [System.IO.Path]::GetFullPath($currentProcess.MainModule.FileName)
        $env:ANTHOLOGY_LAUNCHER_BOOTSTRAP_LOCK_PATH = $bootstrapLockPath
        $env:ANTHOLOGY_DATA_ROOT = Join-Path $deploymentRoot "Data"
        $env:ANTHOLOGY_GAME_ROOT = Split-Path -Parent $deploymentRoot

        $modpackCandidates = @(
            (Join-Path (Split-Path -Parent $env:ANTHOLOGY_GAME_ROOT) "Modpack-1.5.3- Anthology 2.1"),
            (Join-Path (Split-Path -Parent $env:ANTHOLOGY_GAME_ROOT) "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT")
        )
        $env:ANTHOLOGY_MO2_ROOT = ($modpackCandidates |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ "ModOrganizer.exe") -PathType Leaf } |
            Select-Object -First 1)
    }
    finally {
        $currentProcess.Dispose()
    }
}

function Get-ProcessExecutablePath {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    return [System.IO.Path]::GetFullPath($Process.MainModule.FileName)
}

function Read-OwnedCommunityApiProcess {
    if (-not (Test-Path -LiteralPath $apiOwnershipPath -PathType Leaf)) {
        return $null
    }

    try {
        $owner = Get-Content -LiteralPath $apiOwnershipPath -Raw | ConvertFrom-Json
        if ($owner.schemaVersion -ne 1 -or
            [int]$owner.processId -le 0 -or
            [long]$owner.startTimeUtcTicks -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$owner.executablePath) -or
            -not (Test-CanonicalPathEquals -Left ([string]$owner.executablePath) -Right $apiPath) -or
            -not (Test-CanonicalPathEquals -Left ([string]$owner.deploymentRoot) -Right $deploymentRoot) -or
            -not [string]::Equals([string]$owner.apiAddress, $apiAddress, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Community API ownership record is stale."
        }

        $process = [System.Diagnostics.Process]::GetProcessById([int]$owner.processId)
        if ($process.HasExited -or
            $process.StartTime.ToUniversalTime().Ticks -ne [long]$owner.startTimeUtcTicks -or
            -not (Test-CanonicalPathEquals -Left (Get-ProcessExecutablePath -Process $process) -Right $apiPath)) {
            $process.Dispose()
            throw "Community API ownership record no longer identifies its process."
        }
        return $process
    }
    catch {
        Remove-BootstrapPathBestEffort -LiteralPath $apiOwnershipPath
        return $null
    }
}

function Write-CommunityApiOwnership {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $owner = [pscustomobject]@{
        schemaVersion = 1
        processId = $Process.Id
        startTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        executablePath = Get-ProcessExecutablePath -Process $Process
        apiAddress = $apiAddress
        deploymentRoot = $deploymentRoot
        createdAt = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Write-BootstrapJsonAtomically -LiteralPath $apiOwnershipPath -Value $owner
}

function Stop-OwnedCommunityApi {
    param([switch]$BestEffort)

    $process = Read-OwnedCommunityApiProcess
    if ($null -eq $process) {
        if ($null -ne $script:startedApi) {
            $script:startedApi.Dispose()
        }
        $script:startedApi = $null
        return
    }

    try {
        $process.Kill()
        if (-not $process.WaitForExit(10000)) {
            throw "Owned Community API did not exit within 10 seconds."
        }
        Remove-BootstrapPathBestEffort -LiteralPath $apiOwnershipPath
        if ($null -ne $script:startedApi) {
            $script:startedApi.Dispose()
        }
        $script:startedApi = $null
    }
    catch {
        if (-not $BestEffort) {
            throw
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-NoUnownedDeploymentApi {
    $ownerProcess = Read-OwnedCommunityApiProcess
    $ownerId = if ($null -ne $ownerProcess) { $ownerProcess.Id } else { -1 }
    if ($null -ne $ownerProcess) {
        $ownerProcess.Dispose()
    }
    Test-CommunityApiHealth -ThrowOnIdentityMismatch | Out-Null

    foreach ($candidate in @([System.Diagnostics.Process]::GetProcessesByName("Anthology.Community.Api"))) {
        try {
            if (-not $candidate.HasExited -and
                $candidate.Id -ne $ownerId -and
                (Test-CanonicalPathEquals -Left (Get-ProcessExecutablePath -Process $candidate) -Right $apiPath)) {
                throw "Community API from this deployment is running without a verifiable ownership record. Close the older launcher once, then start it again."
            }
        }
        finally {
            $candidate.Dispose()
        }
    }
}

function Test-CommunityApiHealth {
    param([switch]$ThrowOnIdentityMismatch)

    try {
        $health = Invoke-RestMethod -Uri "$apiAddress/health" -TimeoutSec 1
    }
    catch {
        return $false
    }

    $valid = [string]::Equals(
        [string]$health.service,
        "anthology-community-server",
        [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$health.status, "ok", [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $valid -and $ThrowOnIdentityMismatch) {
        throw "Port $apiAddress is occupied by a service that is not the Anthology Community API."
    }
    return $valid
}

function Ensure-CommunityApi {
    if (Test-CommunityApiHealth -ThrowOnIdentityMismatch) {
        # A verified standalone API may intentionally serve this launcher from
        # another installation root. Accept it, but never create an ownership
        # record for it. The exact deployment binary remains protected below.
        Assert-NoUnownedDeploymentApi
        return
    }

    $existingOwner = Read-OwnedCommunityApiProcess
    if ($null -ne $existingOwner) {
        $existingOwner.Dispose()
        throw "The owned Community API process is running but its health endpoint is unavailable."
    }

    $newApi = Start-Process `
        -FilePath $apiPath `
        -ArgumentList "--urls", $apiAddress `
        -WorkingDirectory (Split-Path -Parent $apiPath) `
        -WindowStyle Hidden `
        -PassThru
    try {
        Write-CommunityApiOwnership -Process $newApi
    }
    catch {
        try {
            if (-not $newApi.HasExited) {
                $newApi.Kill()
                $newApi.WaitForExit(10000) | Out-Null
            }
        }
        finally {
            $newApi.Dispose()
        }
        throw
    }

    $script:startedApi = $newApi
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($newApi.HasExited) {
            throw "Community API exited during startup (code $($newApi.ExitCode))."
        }
        if (Test-CommunityApiHealth -ThrowOnIdentityMismatch) {
            return
        }
    }

    throw "Community API did not report a verified health identity within 5 seconds."
}

function Assert-SafeLauncherArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadPath,
        [Parameter(Mandatory = $true)][string]$StagingRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PayloadPath)
    try {
        $entryTargets = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
        [long]$totalExpandedBytes = 0
        if ($archive.Entries.Count -gt 250000) {
            throw "Launcher update archive contains too many entries."
        }

        foreach ($entry in $archive.Entries) {
            $entryPath = ([string]$entry.FullName).Replace('/', '\').TrimEnd('\')
            if ([string]::IsNullOrWhiteSpace($entryPath)) {
                continue
            }

            $segments = @($entryPath.Split('\'))
            if ([System.IO.Path]::IsPathRooted($entryPath) -or
                $entryPath.Contains(":") -or
                $segments.Count -eq 0 -or
                @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq ".." -or $_ -eq "." }).Count -gt 0) {
                throw "Launcher update archive contains an unsafe entry: $($entry.FullName)"
            }

            $allowedPath = $entryPath.Equals("App", [System.StringComparison]::OrdinalIgnoreCase) -or
                $entryPath.StartsWith("App\", [System.StringComparison]::OrdinalIgnoreCase) -or
                $entryPath.Equals("Services", [System.StringComparison]::OrdinalIgnoreCase) -or
                $entryPath.Equals("Services\CommunityApi", [System.StringComparison]::OrdinalIgnoreCase) -or
                $entryPath.StartsWith("Services\CommunityApi\", [System.StringComparison]::OrdinalIgnoreCase)
            if (-not $allowedPath) {
                throw "Launcher update archive contains a path outside App or Services\CommunityApi: $entryPath"
            }

            $entryTarget = [System.IO.Path]::GetFullPath((Join-Path $StagingRoot $entryPath))
            if (-not (Test-PathIsUnderRoot -Path $entryTarget -Root $StagingRoot) -or
                -not $entryTargets.Add($entryTarget)) {
                throw "Launcher update archive contains a duplicate or escaping entry: $entryPath"
            }

            $unixFileType = ([int64]$entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixFileType -eq 0xA000 -or
                (($entry.ExternalAttributes -band [int][System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Launcher update archive contains a link or reparse point: $entryPath"
            }

            $totalExpandedBytes += [long]$entry.Length
            if ($totalExpandedBytes -gt 2147483648) {
                throw "Launcher update archive expands beyond the 2 GiB safety limit."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Prepare-PendingLauncherUpdate {
    if (-not (Test-Path -LiteralPath $pendingUpdateDescriptor -PathType Leaf)) {
        return $null
    }

    Assert-NoDestinationReparsePoint -Destination $pendingUpdateDescriptor

    $descriptor = Get-Content -LiteralPath $pendingUpdateDescriptor -Raw | ConvertFrom-Json
    if ($descriptor.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$descriptor.payloadFile) -or
        [string]::IsNullOrWhiteSpace([string]$descriptor.launcherVersion) -or
        [string]::IsNullOrWhiteSpace([string]$descriptor.releaseVersion) -or
        -not ([string]$descriptor.sha256 -match '^[a-fA-F0-9]{64}$')) {
        throw "Launcher update descriptor is invalid."
    }

    $payloadName = [System.IO.Path]::GetFileName([string]$descriptor.payloadFile)
    if (-not [string]::Equals($payloadName, [string]$descriptor.payloadFile, [System.StringComparison]::Ordinal) -or
        -not ($payloadName -match '^[a-zA-Z0-9][a-zA-Z0-9._-]*\.zip$') -or
        $payloadName.Contains("..")) {
        throw "Launcher update payload name is unsafe."
    }

    $payloadPath = Join-Path $pendingUpdateRoot $payloadName
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        throw "Launcher update payload was not found: $payloadName"
    }
    Assert-NoDestinationReparsePoint -Destination $payloadPath

    $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, ([string]$descriptor.sha256).Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Launcher update payload failed SHA-256 verification."
    }

    $operationId = [Guid]::NewGuid().ToString("N")
    $stagingRoot = Join-Path $updateRoot "LauncherStaging-$operationId"
    $backupRoot = Join-Path $updateRoot "LauncherBackup-$operationId"
    [System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
    try {
        Assert-SafeLauncherArchive -PayloadPath $payloadPath -StagingRoot $stagingRoot
        Expand-Archive -LiteralPath $payloadPath -DestinationPath $stagingRoot -Force
        foreach ($item in @(Get-ChildItem -LiteralPath $stagingRoot -Force -Recurse)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Launcher update archive contains a reparse point."
            }
        }

        $requiredAssembly = Join-Path $stagingRoot "App\AnthologyLauncher.Next.dll"
        if (-not (Test-Path -LiteralPath $requiredAssembly -PathType Leaf)) {
            throw "Launcher update payload does not contain AnthologyLauncher.Next.dll."
        }

        $files = New-Object System.Collections.Generic.List[object]
        $destinations = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
        $stagingPrefix = [System.IO.Path]::GetFullPath($stagingRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        foreach ($source in @(Get-ChildItem -LiteralPath $stagingRoot -File -Force -Recurse | Sort-Object FullName)) {
            $sourcePath = [System.IO.Path]::GetFullPath($source.FullName)
            if (-not $sourcePath.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Launcher update source escaped its staging directory."
            }

            $relative = $sourcePath.Substring($stagingPrefix.Length)
            if ([string]::IsNullOrWhiteSpace($relative) -or
                -not ($relative.StartsWith("App\", [System.StringComparison]::OrdinalIgnoreCase) -or
                      $relative.StartsWith("Services\CommunityApi\", [System.StringComparison]::OrdinalIgnoreCase))) {
                throw "Launcher update contains a path outside App or Services\CommunityApi: $relative"
            }

            $destination = [System.IO.Path]::GetFullPath((Join-Path $deploymentRoot $relative))
            if (-not (Test-PathIsUnderRoot -Path $destination -Root $deploymentRoot) -or
                -not $destinations.Add($destination)) {
                throw "Launcher update contains an unsafe or duplicate destination: $relative"
            }
            Assert-NoDestinationReparsePoint -Destination $destination
            $files.Add([pscustomobject]@{
                Relative = $relative
                Source = $sourcePath
                Destination = $destination
            })
        }

        if ($files.Count -eq 0) {
            throw "Launcher update payload is empty."
        }

        $updatesCommunityApi = $false
        foreach ($file in $files) {
            if ($file.Relative.StartsWith("Services\CommunityApi\", [System.StringComparison]::OrdinalIgnoreCase)) {
                $updatesCommunityApi = $true
                break
            }
        }

        return [pscustomobject]@{
            OperationId = $operationId
            Descriptor = $descriptor
            DescriptorPath = $pendingUpdateDescriptor
            AppliedDescriptorPath = Join-Path $pendingUpdateRoot "launcher-update.applied-$operationId.json"
            PayloadPath = $payloadPath
            StagingRoot = $stagingRoot
            BackupRoot = $backupRoot
            Files = $files.ToArray()
            UpdatesCommunityApi = $updatesCommunityApi
        }
    }
    catch {
        Remove-BootstrapPathBestEffort -LiteralPath $stagingRoot -Recurse
        throw
    }
}

function Rollback-PendingLauncherUpdate {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$AppliedFiles,
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)][bool]$StateWasChanged,
        [Parameter(Mandatory = $true)][bool]$StateExisted,
        [string]$StateBackupPath
    )

    $errors = New-Object System.Collections.Generic.List[string]
    for ($index = $AppliedFiles.Count - 1; $index -ge 0; $index--) {
        $entry = $AppliedFiles[$index]
        try {
            if ($entry.Existed) {
                Install-BootstrapFile -Source $entry.Backup -Destination $entry.Destination
            }
            elseif (Test-Path -LiteralPath $entry.Destination -PathType Leaf) {
                Remove-Item -LiteralPath $entry.Destination -Force
            }
        }
        catch {
            $errors.Add("$($entry.Relative): $($_.Exception.Message)")
        }
    }

    if ($StateWasChanged) {
        try {
            if ($StateExisted) {
                Install-BootstrapFile -Source $StateBackupPath -Destination $StatePath
            }
            elseif (Test-Path -LiteralPath $StatePath -PathType Leaf) {
                Remove-Item -LiteralPath $StatePath -Force
            }
        }
        catch {
            $errors.Add("launcher-version.json: $($_.Exception.Message)")
        }
    }

    return $errors.ToArray()
}

function Commit-PendingLauncherUpdate {
    param([Parameter(Mandatory = $true)][object]$Plan)

    [System.IO.Directory]::CreateDirectory($Plan.BackupRoot) | Out-Null
    $appliedFiles = New-Object System.Collections.Generic.List[object]
    $statePath = Join-Path $deploymentRoot "Data\launcher-version.json"
    Assert-NoDestinationReparsePoint -Destination $statePath
    $stateBackupPath = Join-Path $Plan.BackupRoot "__bootstrap-state\launcher-version.json"
    $stateExisted = Test-Path -LiteralPath $statePath -PathType Leaf
    $stateWasChanged = $false
    $committed = $false

    try {
        foreach ($file in $Plan.Files) {
            $existed = Test-Path -LiteralPath $file.Destination -PathType Leaf
            $backup = Join-Path $Plan.BackupRoot $file.Relative
            if ($existed) {
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
                Copy-Item -LiteralPath $file.Destination -Destination $backup -Force
            }

            $entry = [pscustomobject]@{
                Relative = $file.Relative
                Destination = $file.Destination
                Backup = $backup
                Existed = $existed
            }
            $appliedFiles.Add($entry)
            Install-BootstrapFile -Source $file.Source -Destination $file.Destination
        }

        if ($stateExisted) {
            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $stateBackupPath)) | Out-Null
            Copy-Item -LiteralPath $statePath -Destination $stateBackupPath -Force
        }
        $stateWasChanged = $true
        Write-BootstrapJsonAtomically -LiteralPath $statePath -Value ([pscustomobject]@{
            schemaVersion = 1
            launcherVersion = [string]$Plan.Descriptor.launcherVersion
            releaseVersion = [string]$Plan.Descriptor.releaseVersion
            appliedAt = [DateTimeOffset]::UtcNow.ToString("O")
        })

        [System.IO.File]::Move($Plan.DescriptorPath, $Plan.AppliedDescriptorPath)
        $committed = $true
    }
    catch {
        $installFailure = $_.Exception
        $rollbackErrors = @(Rollback-PendingLauncherUpdate `
            -AppliedFiles ($appliedFiles.ToArray()) `
            -StatePath $statePath `
            -StateWasChanged $stateWasChanged `
            -StateExisted $stateExisted `
            -StateBackupPath $stateBackupPath)
        if ($rollbackErrors.Count -gt 0) {
            throw [System.InvalidOperationException]::new(
                "Launcher update failed and rollback is incomplete. Original error: $($installFailure.Message). Rollback errors: $($rollbackErrors -join '; ')",
                $installFailure)
        }
        Remove-BootstrapPathBestEffort -LiteralPath $Plan.StagingRoot -Recurse
        Remove-BootstrapPathBestEffort -LiteralPath $Plan.BackupRoot -Recurse
        throw
    }

    if ($committed) {
        Remove-BootstrapPathBestEffort -LiteralPath $Plan.StagingRoot -Recurse
        Remove-BootstrapPathBestEffort -LiteralPath $Plan.BackupRoot -Recurse
        Remove-BootstrapPathBestEffort -LiteralPath $Plan.PayloadPath
        if (-not (Test-Path -LiteralPath $Plan.StagingRoot) -and
            -not (Test-Path -LiteralPath $Plan.BackupRoot) -and
            -not (Test-Path -LiteralPath $Plan.PayloadPath)) {
            Remove-BootstrapPathBestEffort -LiteralPath $Plan.AppliedDescriptorPath
        }
    }
}

function Remove-StaleCommittedLauncherUpdates {
    if (-not (Test-Path -LiteralPath $pendingUpdateRoot -PathType Container)) {
        return
    }

    $currentPayloadName = $null
    if (Test-Path -LiteralPath $pendingUpdateDescriptor -PathType Leaf) {
        try {
            $currentDescriptor = Get-Content -LiteralPath $pendingUpdateDescriptor -Raw | ConvertFrom-Json
            $currentPayloadName = [string]$currentDescriptor.payloadFile
        }
        catch {
            # A malformed current descriptor is handled by normal preflight.
        }
    }

    foreach ($marker in @(Get-ChildItem -LiteralPath $pendingUpdateRoot -File -Filter "launcher-update.applied-*.json")) {
        if ($marker.Name -notmatch '^launcher-update\.applied-([a-fA-F0-9]{32})\.json$') {
            continue
        }

        $operationId = $Matches[1]
        try {
            $descriptor = Get-Content -LiteralPath $marker.FullName -Raw | ConvertFrom-Json
            $payloadName = [string]$descriptor.payloadFile
            if (-not ($payloadName -match '^[a-zA-Z0-9][a-zA-Z0-9._-]*\.zip$') -or
                $payloadName.Contains("..")) {
                continue
            }

            $stagingRoot = Join-Path $updateRoot "LauncherStaging-$operationId"
            $backupRoot = Join-Path $updateRoot "LauncherBackup-$operationId"
            $payloadPath = Join-Path $pendingUpdateRoot $payloadName
            Remove-BootstrapPathBestEffort -LiteralPath $stagingRoot -Recurse
            Remove-BootstrapPathBestEffort -LiteralPath $backupRoot -Recurse
            if (-not [string]::Equals($payloadName, $currentPayloadName, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-BootstrapPathBestEffort -LiteralPath $payloadPath
            }

            if (-not (Test-Path -LiteralPath $stagingRoot) -and
                -not (Test-Path -LiteralPath $backupRoot) -and
                -not (Test-Path -LiteralPath $payloadPath)) {
                Remove-BootstrapPathBestEffort -LiteralPath $marker.FullName
            }
        }
        catch {
            # Preserve an unparseable marker and its recovery artifacts for diagnostics.
        }
    }
}

function Apply-PendingLauncherUpdate {
    $plan = Prepare-PendingLauncherUpdate
    if ($null -eq $plan) {
        return $false
    }

    if ($plan.UpdatesCommunityApi) {
        Assert-NoUnownedDeploymentApi
        Stop-OwnedCommunityApi
        Assert-NoUnownedDeploymentApi
    }
    Commit-PendingLauncherUpdate -Plan $plan
    return $true
}

function Show-BootstrapError {
    param([Parameter(Mandatory = $true)][string]$Message)

    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            $Message,
            "Anthology Launcher Next",
            "OK",
            "Error") | Out-Null
    }
    catch {
        Write-Error $Message -ErrorAction Continue
    }
}

function Invoke-AnthologyLauncherBootstrap {
    $bootstrapLock = $null
    try {
        $lockTimeoutMilliseconds = if ($RestartAfterProcessId -gt 0) { 15000 } else { 0 }
        $bootstrapLock = Enter-BootstrapLock -TimeoutMilliseconds $lockTimeoutMilliseconds
        if ($null -eq $bootstrapLock) {
            return 0
        }

        Set-BootstrapEnvironment
        Wait-RestartTarget

        Assert-NoDestinationReparsePoint -Destination $apiPath
        Assert-NoDestinationReparsePoint -Destination $launcherPath

        if (-not (Test-Path -LiteralPath $apiPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
            throw "Portable build is incomplete. Publish the project again."
        }

        Remove-StaleCommittedLauncherUpdates
        Apply-PendingLauncherUpdate | Out-Null
        Ensure-CommunityApi

        $env:ANTHOLOGY_COMMUNITY_API = $apiAddress
        while ($true) {
            $launcher = Start-Process `
                -FilePath $launcherPath `
                -WorkingDirectory (Split-Path -Parent $launcherPath) `
                -PassThru
            try {
                $launcher.WaitForExit()
            }
            finally {
                $launcher.Dispose()
            }

            if (-not (Test-Path -LiteralPath $pendingUpdateDescriptor -PathType Leaf)) {
                break
            }

            # Preflight runs while the API is still alive. Only an update that
            # passed descriptor, hash, archive and path checks may stop our API.
            Apply-PendingLauncherUpdate | Out-Null
            Ensure-CommunityApi
        }
        return 0
    }
    catch {
        Show-BootstrapError -Message $_.Exception.Message
        return 1
    }
    finally {
        if ($null -ne $bootstrapLock) {
            Stop-OwnedCommunityApi -BestEffort
            if ($null -ne $script:startedApi) {
                $script:startedApi.Dispose()
                $script:startedApi = $null
            }
            $bootstrapLock.Dispose()
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-AnthologyLauncherBootstrap)
}
