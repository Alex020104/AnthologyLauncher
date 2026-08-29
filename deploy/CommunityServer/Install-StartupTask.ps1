[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "Anthology Community Server"
$serverRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $serverRoot "App"
$executable = Join-Path $appRoot "Anthology.Community.Api.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Server executable was not found: $executable"
}

$action = New-ScheduledTaskAction -Execute $executable -WorkingDirectory $appRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description "Starts A.N.T.H.O.L.O.G.Y Community Server after user logon." -Force | Out-Null
Write-Host "Startup task installed. The currently running server was not interrupted."
