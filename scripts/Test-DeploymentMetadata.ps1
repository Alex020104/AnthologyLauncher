[CmdletBinding()]
param(
    [string]$StageAnchor = "A:\AnthologyDeployStage\DeploymentMetadataValidation"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Publish-Safety.ps1")

$sourceRoot = Split-Path -Parent $PSScriptRoot
$stageRoot = New-AnthologySiblingWorkingDirectory -DestinationRoot $StageAnchor -Purpose "metadata-test"
try {
    $integratedOutput = Join-Path $stageRoot "Integrated"
    $releaserOutput = Join-Path $stageRoot "Releaser"
    & (Join-Path $PSScriptRoot "Build-IntegratedLauncher.ps1") -OutputDirectory $integratedOutput
    & (Join-Path $PSScriptRoot "Build-ReleaserBootstrap.ps1") -OutputDirectory $releaserOutput

    $integratedInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $integratedOutput "AnomalyLauncher.exe"))
    $releaserInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $releaserOutput "AnthologyReleaser.Next.exe"))

    if ($integratedInfo.FileVersion -ne "0.7.0-alpha.20" -or $integratedInfo.ProductVersion -ne "0.7.0-alpha.20") {
        throw "Unexpected integrated launcher string version: file='$($integratedInfo.FileVersion)', product='$($integratedInfo.ProductVersion)'"
    }
    if ($integratedInfo.FileMajorPart -ne 0 -or $integratedInfo.FileMinorPart -ne 7 -or
        $integratedInfo.FileBuildPart -ne 0 -or $integratedInfo.FilePrivatePart -ne 20) {
        throw "Unexpected integrated launcher numeric version: $($integratedInfo.FileMajorPart).$($integratedInfo.FileMinorPart).$($integratedInfo.FileBuildPart).$($integratedInfo.FilePrivatePart)"
    }

    if ($releaserInfo.FileVersion -ne "0.2.0-alpha.2" -or $releaserInfo.ProductVersion -ne "0.2.0-alpha.2") {
        throw "Unexpected releaser bootstrap string version: file='$($releaserInfo.FileVersion)', product='$($releaserInfo.ProductVersion)'"
    }
    if ($releaserInfo.FileMajorPart -ne 0 -or $releaserInfo.FileMinorPart -ne 2 -or
        $releaserInfo.FileBuildPart -ne 0 -or $releaserInfo.FilePrivatePart -ne 2) {
        throw "Unexpected releaser bootstrap numeric version: $($releaserInfo.FileMajorPart).$($releaserInfo.FileMinorPart).$($releaserInfo.FileBuildPart).$($releaserInfo.FilePrivatePart)"
    }

    [pscustomobject]@{
        IntegratedStringVersion = $integratedInfo.FileVersion
        IntegratedNumericVersion = "$($integratedInfo.FileMajorPart).$($integratedInfo.FileMinorPart).$($integratedInfo.FileBuildPart).$($integratedInfo.FilePrivatePart)"
        ReleaserStringVersion = $releaserInfo.FileVersion
        ReleaserNumericVersion = "$($releaserInfo.FileMajorPart).$($releaserInfo.FileMinorPart).$($releaserInfo.FileBuildPart).$($releaserInfo.FilePrivatePart)"
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-AnthologySiblingWorkingDirectory -DestinationRoot $StageAnchor -WorkingDirectory $stageRoot
    }
}
