[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$bootstrapPath = Join-Path $sourceRoot "deploy\Start-AnthologyLauncherNext.ps1"

function Assert-Smoke {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Launcher restart smoke failed: $Message"
    }
}

function Get-FunctionAst {
    param(
        [Parameter(Mandatory = $true)][System.Management.Automation.Language.Ast]$Ast,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return $Ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $Name
    }, $true)
}

function Get-CommandOffset {
    param(
        [Parameter(Mandatory = $true)][System.Management.Automation.Language.FunctionDefinitionAst]$Function,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $command = $Function.Find({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq $Name
    }, $true)
    Assert-Smoke ($null -ne $command) "function $($Function.Name) must invoke $Name"
    return $command.Extent.StartOffset
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $bootstrapPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-Smoke (@($parseErrors).Count -eq 0) "bootstrap must parse in Windows PowerShell"

$main = Get-FunctionAst -Ast $ast -Name "Invoke-AnthologyLauncherBootstrap"
$apply = Get-FunctionAst -Ast $ast -Name "Apply-PendingLauncherUpdate"
$commit = Get-FunctionAst -Ast $ast -Name "Commit-PendingLauncherUpdate"
$wait = Get-FunctionAst -Ast $ast -Name "Wait-RestartTarget"
$apiGuard = Get-FunctionAst -Ast $ast -Name "Assert-NoUnownedDeploymentApi"
$ensureApi = Get-FunctionAst -Ast $ast -Name "Ensure-CommunityApi"
Assert-Smoke ($null -ne $main) "main bootstrap function is missing"
Assert-Smoke ($null -ne $apply) "update apply function is missing"
Assert-Smoke ($null -ne $commit) "update commit function is missing"
Assert-Smoke ($null -ne $wait) "verified wait function is missing"
Assert-Smoke ($null -ne $apiGuard) "deployment API guard is missing"
Assert-Smoke ($null -ne $ensureApi) "Community API startup function is missing"

$lockOffset = Get-CommandOffset -Function $main -Name "Enter-BootstrapLock"
$waitOffset = Get-CommandOffset -Function $main -Name "Wait-RestartTarget"
Assert-Smoke ($lockOffset -lt $waitOffset) "deployment lock must be acquired before waiting for the launcher"
Assert-Smoke ($main.Extent.Text.Contains('if ($null -ne $bootstrapLock)')) "lock-contention early exit must not run owned-resource cleanup"

$prepareOffset = Get-CommandOffset -Function $apply -Name "Prepare-PendingLauncherUpdate"
$stopOffset = Get-CommandOffset -Function $apply -Name "Stop-OwnedCommunityApi"
$commitOffset = Get-CommandOffset -Function $apply -Name "Commit-PendingLauncherUpdate"
Assert-Smoke ($prepareOffset -lt $stopOffset) "payload preflight must run before API shutdown"
Assert-Smoke ($stopOffset -lt $commitOffset) "API shutdown must precede update commit"

$bootstrapText = [System.IO.File]::ReadAllText($bootstrapPath)
Assert-Smoke (-not $bootstrapText.Contains("Stop-Process")) "bootstrap must not stop processes by an unverified PID"
Assert-Smoke (-not $bootstrapText.Contains("Wait-Process")) "bootstrap must not use an unbounded PID-only wait"
Assert-Smoke ($wait.Extent.Text.Contains("WaitForExit(60000)")) "restart wait must be bounded"
Assert-Smoke ($bootstrapText.Contains('"anthology-community-server"')) "health response identity must be checked"
Assert-Smoke ($apiGuard.Extent.Text.Contains("Test-CanonicalPathEquals")) "an unowned API from the deployment path must still be rejected"
Assert-Smoke (-not $apiGuard.Extent.Text.Contains("healthy Anthology Community API is already using")) "a verified external API must not be rejected solely for lacking local ownership"
Assert-Smoke ($ensureApi.Extent.Text.Contains("Assert-NoUnownedDeploymentApi")) "verified external health must still check for an unowned deployment binary"
Assert-Smoke ($commit.Extent.Text.IndexOf('[System.IO.File]::Move($Plan.DescriptorPath', [StringComparison]::Ordinal) -ge 0) "commit marker must be an atomic descriptor move"
Assert-Smoke ($commit.Extent.Text.IndexOf("Remove-BootstrapPathBestEffort", [StringComparison]::Ordinal) -ge 0) "post-commit cleanup must be best effort"

$serviceText = [System.IO.File]::ReadAllText((Join-Path $sourceRoot "src\Anthology.Launcher\LauncherUpdateService.cs"))
$mainRazorText = [System.IO.File]::ReadAllText((Join-Path $sourceRoot "src\Anthology.Launcher\Main.razor"))
$windowText = [System.IO.File]::ReadAllText((Join-Path $sourceRoot "src\Anthology.Launcher\MainWindow.xaml.cs"))
$appText = [System.IO.File]::ReadAllText((Join-Path $sourceRoot "src\Anthology.Launcher\App.xaml.cs"))
Assert-Smoke ($serviceText.Contains('LauncherPackageId = "anthology-launcher"')) "real launcher package id must be explicit"
Assert-Smoke ($serviceText.Contains("RestartAfterProcessStartTimeUtcTicks")) "restart must pass process start time"
Assert-Smoke ($serviceText.Contains("RestartAfterProcessPath")) "restart must pass process executable path"
Assert-Smoke ($serviceText.Contains("AuthorizeRestartShutdown")) "planned shutdown must be authorized through the operation gate"
Assert-Smoke ($mainRazorText.Contains("OperationGate.EnterTransfer()")) "Blazor transfers must acquire the operation gate"
Assert-Smoke ($windowText.Contains("_operationGate.ShouldBlockWindowClose")) "native close must consult the operation gate"
Assert-Smoke ($appText.Contains("CreateSingleInstanceMutexName")) "application mutex must be deployment scoped"

# Dot-source exposes transaction seams without starting the launcher.
. $bootstrapPath

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("anthology-launcher-restart-smoke-{0}" -f [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
try {
    $deploymentRoot = Join-Path $smokeRoot "AnthologyLauncher"
    $updateRoot = Join-Path $deploymentRoot "Update"
    $pendingUpdateRoot = Join-Path $updateRoot "LauncherPending"
    $pendingUpdateDescriptor = Join-Path $pendingUpdateRoot "launcher-update.json"
    $bootstrapLockPath = Join-Path $updateRoot "launcher-bootstrap.lock"
    $apiOwnershipPath = Join-Path $updateRoot "community-api-owner.json"
    $apiPath = Join-Path $deploymentRoot "Services\CommunityApi\Anthology.Community.Api.exe"
    $launcherPath = Join-Path $deploymentRoot "App\AnthologyLauncher.Next.exe"

    $archiveRoot = Join-Path $smokeRoot "archive"
    $archiveApp = Join-Path $archiveRoot "App"
    $destinationApp = Join-Path $deploymentRoot "App"
    [System.IO.Directory]::CreateDirectory($archiveApp) | Out-Null
    [System.IO.Directory]::CreateDirectory($destinationApp) | Out-Null
    [System.IO.Directory]::CreateDirectory($pendingUpdateRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $deploymentRoot "Data")) | Out-Null

    $oldAssembly = [byte[]](1, 2, 3, 4)
    $newAssembly = [byte[]](9, 8, 7, 6)
    [System.IO.File]::WriteAllBytes((Join-Path $destinationApp "AnthologyLauncher.Next.dll"), $oldAssembly)
    [System.IO.File]::WriteAllBytes((Join-Path $archiveApp "AnthologyLauncher.Next.dll"), $newAssembly)
    [System.IO.File]::WriteAllText((Join-Path $archiveApp "new-file.txt"), "new")
    $oldState = '{"schemaVersion":1,"launcherVersion":"old","releaseVersion":"old"}'
    [System.IO.File]::WriteAllText((Join-Path $deploymentRoot "Data\launcher-version.json"), $oldState)

    $payloadPath = Join-Path $pendingUpdateRoot "payload.zip"
    Compress-Archive -Path (Join-Path $archiveRoot "*") -DestinationPath $payloadPath -Force
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
    [pscustomobject]@{
        schemaVersion = 1
        payloadFile = "payload.zip"
        sha256 = $payloadHash
        launcherVersion = "smoke-new"
        releaseVersion = "smoke-release"
    } | ConvertTo-Json | Set-Content -LiteralPath $pendingUpdateDescriptor -Encoding UTF8

    $failedPlan = Prepare-PendingLauncherUpdate
    [System.IO.File]::WriteAllText($failedPlan.AppliedDescriptorPath, "forced commit conflict")
    $failed = $false
    $failureMessage = $null
    try {
        Commit-PendingLauncherUpdate -Plan $failedPlan
    }
    catch {
        $failed = $true
        $failureMessage = $_.Exception.Message
    }
    Assert-Smoke $failed "a forced pre-commit failure must surface"
    Assert-Smoke (-not $failureMessage.Contains("rollback is incomplete")) "forced failure must roll back completely: $failureMessage"
    Assert-Smoke ([System.Linq.Enumerable]::SequenceEqual(
        [byte[]][System.IO.File]::ReadAllBytes((Join-Path $destinationApp "AnthologyLauncher.Next.dll")),
        $oldAssembly)) "rollback must restore an overwritten file byte for byte"
    Assert-Smoke (-not (Test-Path -LiteralPath (Join-Path $destinationApp "new-file.txt"))) "rollback must remove a newly created file"
    Assert-Smoke ([System.IO.File]::ReadAllText((Join-Path $deploymentRoot "Data\launcher-version.json")) -eq $oldState) "rollback must restore launcher version state"
    Assert-Smoke (Test-Path -LiteralPath $pendingUpdateDescriptor -PathType Leaf) "failed update must keep its descriptor for retry"
    Assert-Smoke (Test-Path -LiteralPath $payloadPath -PathType Leaf) "failed update must keep its payload for retry"
    $remainingStaging = @(Get-ChildItem -LiteralPath $updateRoot -Directory -Filter "LauncherStaging-*")
    $remainingBackups = @(Get-ChildItem -LiteralPath $updateRoot -Directory -Filter "LauncherBackup-*")
    Assert-Smoke ($remainingStaging.Count -eq 0) "successful rollback must clean staging: $($remainingStaging.FullName -join ', ')"
    Assert-Smoke ($remainingBackups.Count -eq 0) "successful rollback must clean backup: $($remainingBackups.FullName -join ', ')"

    Remove-Item -LiteralPath $failedPlan.AppliedDescriptorPath -Force
    $retryPlan = Prepare-PendingLauncherUpdate
    $lockedStagingFile = [System.IO.File]::Open(
        $retryPlan.Files[0].Source,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        Commit-PendingLauncherUpdate -Plan $retryPlan
    }
    finally {
        $lockedStagingFile.Dispose()
    }
    Assert-Smoke ([System.Linq.Enumerable]::SequenceEqual(
        [byte[]][System.IO.File]::ReadAllBytes((Join-Path $destinationApp "AnthologyLauncher.Next.dll")),
        $newAssembly)) "retry must install the replacement file"
    Assert-Smoke (Test-Path -LiteralPath (Join-Path $destinationApp "new-file.txt") -PathType Leaf) "retry must install the new file"
    Assert-Smoke (-not (Test-Path -LiteralPath $pendingUpdateDescriptor)) "commit must consume the descriptor"
    Assert-Smoke (-not (Test-Path -LiteralPath $payloadPath)) "commit cleanup must remove the payload"
    Assert-Smoke (Test-Path -LiteralPath $retryPlan.StagingRoot -PathType Container) "a cleanup failure after commit must not roll back installed files"
    Remove-BootstrapPathBestEffort -LiteralPath $retryPlan.StagingRoot -Recurse
    Assert-Smoke (-not (Test-Path -LiteralPath $retryPlan.StagingRoot)) "leftover committed staging must be removable on retry"
    Remove-StaleCommittedLauncherUpdates
    Assert-Smoke (-not (Test-Path -LiteralPath $retryPlan.AppliedDescriptorPath)) "next-run housekeeping must remove a completed commit marker"

    $firstLock = Enter-BootstrapLock -TimeoutMilliseconds 0
    Assert-Smoke ($null -ne $firstLock) "first deployment lock acquisition must succeed"
    try {
        $contendedLock = Enter-BootstrapLock -TimeoutMilliseconds 0
        Assert-Smoke ($null -eq $contendedLock) "second lock acquisition for the same deployment must fail"
    }
    finally {
        if ($null -ne $contendedLock) {
            $contendedLock.Dispose()
        }
        $firstLock.Dispose()
    }

    $releasedLock = Enter-BootstrapLock -TimeoutMilliseconds 0
    Assert-Smoke ($null -ne $releasedLock) "deployment lock must be reusable after cleanup"
    $releasedLock.Dispose()

    $originalHealthCheck = (Get-Command Test-CommunityApiHealth).ScriptBlock
    Set-Item -LiteralPath Function:\Test-CommunityApiHealth -Value {
        param([switch]$ThrowOnIdentityMismatch)
        return $true
    }
    try {
        Ensure-CommunityApi
        Assert-Smoke (-not (Test-Path -LiteralPath $apiOwnershipPath)) "a verified external API must not be claimed by this deployment"
        Assert-Smoke ($null -eq $script:startedApi) "a verified external API must not be tracked as a child process"
    }
    finally {
        Set-Item -LiteralPath Function:\Test-CommunityApiHealth -Value $originalHealthCheck
    }
}
finally {
    $canonicalSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
    $canonicalTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($canonicalSmokeRoot.StartsWith($canonicalTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($canonicalSmokeRoot).StartsWith("anthology-launcher-restart-smoke-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $canonicalSmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "Launcher restart AST and transaction smoke checks passed."
