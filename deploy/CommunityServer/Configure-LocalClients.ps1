[CmdletBinding()]
param(
    [string]$CommunityUrl = "http://127.0.0.1:5249",
    [string]$ReleaserUrl = ""
)

$ErrorActionPreference = "Stop"
$serverRoot = Split-Path -Parent $PSScriptRoot
$tokenPath = Join-Path $serverRoot "Data\Config\developer-token.txt"
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
    throw "Developer token is missing. Start the server first."
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$developerToken = [System.IO.File]::ReadAllText($tokenPath, $utf8).Trim()
if ([string]::IsNullOrWhiteSpace($ReleaserUrl)) {
    $ReleaserUrl = $CommunityUrl
}

$releaserSettings = "A:\AnthologyReleaserNext\App\Data\machine-settings.json"
if (Test-Path -LiteralPath $releaserSettings -PathType Leaf) {
    $settings = [System.IO.File]::ReadAllText($releaserSettings, $utf8) | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName communityApiUrl -NotePropertyValue $ReleaserUrl -Force
    $settings | Add-Member -NotePropertyName communityDeveloperToken -NotePropertyValue $developerToken -Force
    $json = $settings | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($releaserSettings, $json, $utf8)
}

$dash = [char]0x2014
$launcherSettings = @(
    "A:\YandexDisk\S.T.A.L.K.E.R Anomaly $dash A.N.T.H.O.L.O.G.Y\Anomaly-1.5.3-Anthology 2.1\AnthologyLauncher\Data\settings.json",
    "A:\AnthologyLauncherNext\Data\settings.json"
)
foreach ($path in $launcherSettings) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        continue
    }
    $settings = [System.IO.File]::ReadAllText($path, $utf8) | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName communityApiUrl -NotePropertyValue $CommunityUrl -Force
    $json = $settings | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($path, $json, $utf8)
}
Write-Host "Launcher uses $CommunityUrl; releaser uses $ReleaserUrl"
