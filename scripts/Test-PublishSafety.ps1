[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Publish-Safety.ps1")

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AnthologyPublishSafety-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $destination = Join-Path $testRoot "Launcher"
    New-Item -ItemType Directory -Path (Join-Path $destination "App\Data") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destination "Services\CommunityApi") -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $destination "App\stale.dll"), "stale")
    [System.IO.File]::WriteAllText((Join-Path $destination "App\Data\settings.json"), "user-state")
    [System.IO.File]::WriteAllText((Join-Path $destination "Services\CommunityApi\stale.dll"), "stale")
    [System.IO.File]::WriteAllText((Join-Path $destination "unrelated.txt"), "keep")
    [System.IO.File]::WriteAllText((Join-Path $destination "launcher.cmd"), "old")

    $stage = New-AnthologySiblingWorkingDirectory -DestinationRoot $destination -Purpose "test"
    New-Item -ItemType Directory -Path (Join-Path $stage "App") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage "Services\CommunityApi") -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $stage "App\current.dll"), "current")
    [System.IO.File]::WriteAllText((Join-Path $stage "Services\CommunityApi\current.dll"), "current")
    [System.IO.File]::WriteAllText((Join-Path $stage "launcher.cmd"), "new")
    Copy-AnthologyPreservedAppData -DestinationRoot $destination -StagingRoot $stage
    Invoke-AnthologyControlledReplacement -DestinationRoot $destination -StagingRoot $stage -RelativePaths @("App", "Services\CommunityApi", "launcher.cmd")

    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $destination "App\current.dll") -PathType Leaf) -Message "Current launcher output was not installed."
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destination "App\stale.dll"))) -Message "A stale launcher binary survived replacement."
    Assert-TestCondition -Condition (([System.IO.File]::ReadAllText((Join-Path $destination "App\Data\settings.json"))) -eq "user-state") -Message "Launcher App\Data was not preserved."
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destination "Services\CommunityApi\stale.dll"))) -Message "A stale service binary survived replacement."
    Assert-TestCondition -Condition (([System.IO.File]::ReadAllText((Join-Path $destination "unrelated.txt"))) -eq "keep") -Message "Unrelated destination content was modified."
    Assert-TestCondition -Condition (([System.IO.File]::ReadAllText((Join-Path $destination "launcher.cmd"))) -eq "new") -Message "Known root output was not replaced."
    Remove-AnthologySiblingWorkingDirectory -DestinationRoot $destination -WorkingDirectory $stage

    $rollbackDestination = Join-Path $testRoot "RollbackTarget"
    New-Item -ItemType Directory -Path (Join-Path $rollbackDestination "App\Data") -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $rollbackDestination "App\old.dll"), "old")
    [System.IO.File]::WriteAllText((Join-Path $rollbackDestination "App\Data\settings.json"), "rollback-state")
    [System.IO.File]::WriteAllText((Join-Path $rollbackDestination "Services"), "parent-collision")

    $rollbackStage = New-AnthologySiblingWorkingDirectory -DestinationRoot $rollbackDestination -Purpose "test"
    New-Item -ItemType Directory -Path (Join-Path $rollbackStage "App") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $rollbackStage "Services\CommunityApi") -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $rollbackStage "App\new.dll"), "new")
    [System.IO.File]::WriteAllText((Join-Path $rollbackStage "Services\CommunityApi\new.dll"), "new")
    Copy-AnthologyPreservedAppData -DestinationRoot $rollbackDestination -StagingRoot $rollbackStage

    $replacementFailed = $false
    try {
        Invoke-AnthologyControlledReplacement -DestinationRoot $rollbackDestination -StagingRoot $rollbackStage -RelativePaths @("App", "Services\CommunityApi")
    }
    catch {
        $replacementFailed = $true
    }

    Assert-TestCondition -Condition $replacementFailed -Message "The rollback scenario unexpectedly succeeded."
    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $rollbackDestination "App\old.dll") -PathType Leaf) -Message "The original App was not restored after failure."
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $rollbackDestination "App\new.dll"))) -Message "The failed App replacement was not rolled back."
    Assert-TestCondition -Condition (([System.IO.File]::ReadAllText((Join-Path $rollbackDestination "App\Data\settings.json"))) -eq "rollback-state") -Message "App\Data changed during rollback."
    Remove-AnthologySiblingWorkingDirectory -DestinationRoot $rollbackDestination -WorkingDirectory $rollbackStage

    Write-Host "Publish safety checks passed."
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    Assert-AnthologyPathWithin -Path $resolvedTestRoot -Root $resolvedTempRoot
    if ((Test-Path -LiteralPath $resolvedTestRoot) -and
        [System.IO.Path]::GetFileName($resolvedTestRoot).StartsWith("AnthologyPublishSafety-", [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
