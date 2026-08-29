[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serviceName = "AnthologyCommunityServer"
$serverRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $serverRoot "App\Anthology.Community.Api.exe"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Server executable was not found: $executable"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 800
}

$binaryPath = '"{0}"' -f $executable
& sc.exe create $serviceName binPath= $binaryPath start= auto DisplayName= "Anthology Community Server" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Windows could not create the Anthology Community Server service."
}
& sc.exe description $serviceName "A.N.T.H.O.L.O.G.Y website, chat, polls and bug reports." | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(20))
Write-Host "Anthology Community Server is installed and running."
