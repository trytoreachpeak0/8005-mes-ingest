#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stop and remove the MesIngest Windows Service.
#>
[CmdletBinding()]
param(
    [string] $ServiceName = "MesIngest"
)

$ErrorActionPreference = "Stop"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

if ($svc.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
}

sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete failed for '$ServiceName' (exit $LASTEXITCODE)"
}

Write-Host "Uninstalled service '$ServiceName'."
