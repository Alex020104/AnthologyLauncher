function Resolve-AnthologyPublishDestination {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Publish destination cannot be empty."
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $trimmedPath = $fullPath.TrimEnd([char[]]"\/")
    $trimmedRoot = $rootPath.TrimEnd([char[]]"\/")
    if ($trimmedPath.Equals($trimmedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A drive or share root cannot be used as a publish destination: $fullPath"
    }

    return $trimmedPath
}

function Assert-AnthologyPathWithin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]"\/")
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its expected root: $fullPath (root: $fullRoot)"
    }
}

function Resolve-AnthologyRelativePublishPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "A publish item must be a non-empty relative path: $RelativePath"
    }

    $segments = $RelativePath -split '[\\/]'
    if ($segments | Where-Object { $_ -eq "." -or $_ -eq ".." -or [string]::IsNullOrWhiteSpace($_) }) {
        throw "Unsafe publish item path: $RelativePath"
    }

    $resolved = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    Assert-AnthologyPathWithin -Path $resolved -Root $Root
    return $resolved
}

function New-AnthologySiblingWorkingDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Za-z0-9-]+$')]
        [string]$Purpose
    )

    $destination = Resolve-AnthologyPublishDestination -Path $DestinationRoot
    $parent = [System.IO.Directory]::GetParent($destination)
    if ($null -eq $parent) {
        throw "Cannot determine the parent of publish destination: $destination"
    }

    if (Test-Path -LiteralPath $parent.FullName) {
        if (-not (Test-Path -LiteralPath $parent.FullName -PathType Container)) {
            throw "The publish destination parent is not a directory: $($parent.FullName)"
        }
    }
    else {
        New-Item -ItemType Directory -Path $parent.FullName -Force | Out-Null
    }
    $leafName = [System.IO.Path]::GetFileName($destination)
    $workingName = ".$leafName.$Purpose.$([System.Guid]::NewGuid().ToString('N'))"
    $workingPath = Join-Path $parent.FullName $workingName
    Assert-AnthologyPathWithin -Path $workingPath -Root $parent.FullName
    New-Item -ItemType Directory -Path $workingPath | Out-Null
    return [System.IO.Path]::GetFullPath($workingPath)
}

function Remove-AnthologySiblingWorkingDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $destination = Resolve-AnthologyPublishDestination -Path $DestinationRoot
    $workingPath = Resolve-AnthologyPublishDestination -Path $WorkingDirectory
    $destinationParent = [System.IO.Directory]::GetParent($destination)
    $workingParent = [System.IO.Directory]::GetParent($workingPath)
    if ($null -eq $destinationParent -or $null -eq $workingParent -or
        -not $destinationParent.FullName.Equals($workingParent.FullName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a working directory outside the publish destination parent: $workingPath"
    }

    $expectedPrefix = ".$([System.IO.Path]::GetFileName($destination))."
    $workingLeaf = [System.IO.Path]::GetFileName($workingPath)
    if (-not $workingLeaf.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unrecognized publish working directory: $workingPath"
    }

    if (Test-Path -LiteralPath $workingPath) {
        Remove-Item -LiteralPath $workingPath -Recurse -Force
    }
}

function Copy-AnthologyPreservedAppData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string]$StagingRoot
    )

    $destination = Resolve-AnthologyPublishDestination -Path $DestinationRoot
    $staging = Resolve-AnthologyPublishDestination -Path $StagingRoot
    $sourceData = Resolve-AnthologyRelativePublishPath -Root $destination -RelativePath "App\Data"
    if (-not (Test-Path -LiteralPath $sourceData -PathType Container)) {
        return
    }

    $stagedData = Resolve-AnthologyRelativePublishPath -Root $staging -RelativePath "App\Data"
    if (Test-Path -LiteralPath $stagedData) {
        throw "The clean publish unexpectedly produced App\Data; refusing to merge over user state: $stagedData"
    }

    $stagedApp = Split-Path -Parent $stagedData
    New-Item -ItemType Directory -Path $stagedApp -Force | Out-Null
    Copy-Item -LiteralPath $sourceData -Destination $stagedData -Recurse -Force
}

function Invoke-AnthologyControlledReplacement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string]$StagingRoot,

        [Parameter(Mandatory = $true)]
        [string[]]$RelativePaths
    )

    $destination = Resolve-AnthologyPublishDestination -Path $DestinationRoot
    $staging = Resolve-AnthologyPublishDestination -Path $StagingRoot
    if (-not (Test-Path -LiteralPath $staging -PathType Container)) {
        throw "Publish staging directory does not exist: $staging"
    }

    $destinationParent = [System.IO.Directory]::GetParent($destination)
    $stagingParent = [System.IO.Directory]::GetParent($staging)
    if ($null -eq $destinationParent -or $null -eq $stagingParent -or
        -not $destinationParent.FullName.Equals($stagingParent.FullName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The staging directory must be a sibling of the publish destination so replacements stay on one volume."
    }

    if ($RelativePaths.Count -eq 0) {
        throw "At least one staged publish item is required."
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($relativePath in $RelativePaths) {
        $key = $relativePath.ToLowerInvariant()
        if ($seen.ContainsKey($key)) {
            throw "Duplicate publish item: $relativePath"
        }
        $seen[$key] = $true

        $stagedPath = Resolve-AnthologyRelativePublishPath -Root $staging -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $stagedPath)) {
            throw "Required staged publish item is missing: $stagedPath"
        }

        $entries.Add([pscustomobject]@{
            RelativePath = $relativePath
            StagedPath = $stagedPath
            TargetPath = Resolve-AnthologyRelativePublishPath -Root $destination -RelativePath $relativePath
            BackupPath = $null
            ExistingMoved = $false
            StagedMoved = $false
        })
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $backup = New-AnthologySiblingWorkingDirectory -DestinationRoot $destination -Purpose "backup"
    foreach ($entry in $entries) {
        $entry.BackupPath = Resolve-AnthologyRelativePublishPath -Root $backup -RelativePath $entry.RelativePath
    }

    $touched = New-Object System.Collections.Generic.List[object]
    try {
        foreach ($entry in $entries) {
            $touched.Add($entry)
            $targetParent = Split-Path -Parent $entry.TargetPath
            New-Item -ItemType Directory -Path $targetParent -Force | Out-Null

            if (Test-Path -LiteralPath $entry.TargetPath) {
                $backupParent = Split-Path -Parent $entry.BackupPath
                New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
                Move-Item -LiteralPath $entry.TargetPath -Destination $entry.BackupPath
                $entry.ExistingMoved = $true
            }

            Move-Item -LiteralPath $entry.StagedPath -Destination $entry.TargetPath
            $entry.StagedMoved = $true
        }
    }
    catch {
        $originalError = $_
        $rollbackProblems = New-Object System.Collections.Generic.List[string]
        for ($index = $touched.Count - 1; $index -ge 0; $index--) {
            $entry = $touched[$index]
            try {
                if ($entry.StagedMoved -and (Test-Path -LiteralPath $entry.TargetPath)) {
                    $discardPath = Resolve-AnthologyRelativePublishPath -Root $staging -RelativePath ("__rollback_new\" + $entry.RelativePath)
                    $discardParent = Split-Path -Parent $discardPath
                    New-Item -ItemType Directory -Path $discardParent -Force | Out-Null
                    Move-Item -LiteralPath $entry.TargetPath -Destination $discardPath
                }

                if ($entry.ExistingMoved -and (Test-Path -LiteralPath $entry.BackupPath)) {
                    $targetParent = Split-Path -Parent $entry.TargetPath
                    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                    Move-Item -LiteralPath $entry.BackupPath -Destination $entry.TargetPath
                }
            }
            catch {
                $rollbackProblems.Add("$($entry.RelativePath): $($_.Exception.Message)")
            }
        }

        if ($rollbackProblems.Count -gt 0) {
            $message = "Publish replacement failed and rollback was incomplete. Keep these recovery directories: staging '$staging', backup '$backup'. Errors: $($rollbackProblems -join '; ')"
            $exception = New-Object System.InvalidOperationException($message, $originalError.Exception)
            $exception.Data["AnthologyKeepWorkingDirectories"] = $true
            throw $exception
        }

        Remove-AnthologySiblingWorkingDirectory -DestinationRoot $destination -WorkingDirectory $backup
        throw $originalError
    }

    Remove-AnthologySiblingWorkingDirectory -DestinationRoot $destination -WorkingDirectory $backup
}
