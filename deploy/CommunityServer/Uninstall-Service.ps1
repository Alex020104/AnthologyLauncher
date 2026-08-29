[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serviceName = "AnthologyCommunityServer"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "The service is already absent. Data and backups were preserved."
    exit 0
}
if ($service.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
}
& sc.exe delete $serviceName | Out-Null
Write-Host "The service was removed. The Data directory was not changed."
