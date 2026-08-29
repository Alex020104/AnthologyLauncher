[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "Anthology Community Server"
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Write-Host "Startup task removed. Server data was preserved."
