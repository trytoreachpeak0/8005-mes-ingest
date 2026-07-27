#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install MesIngest.Host as a Windows Service (default name: MesIngest).

.PARAMETER InstallRoot
  MesIngest install root containing service\MesIngest.Host.exe (defaults to parent of scripts/).

.PARAMETER ServiceName
  Windows service name.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $ServiceName = "MesIngest",
    [string] $DisplayName = "MesIngest MES Task Ingest"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallRoot "service\MesIngest.Host.exe"
if (-not (Test-Path $exe)) {
    throw "Host executable not found: $exe"
}

$local = Join-Path $InstallRoot "service\appsettings.Local.json"
if (-not (Test-Path $local)) {
    Write-Warning "service\appsettings.Local.json is missing. Copy from templates\ first; service may fail to connect."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists. Stop and uninstall it first."
}

# Host sets ContentRoot to the exe directory so appsettings.Local.json / queries resolve
# even when the service process cwd is System32.
$binPath = "`"$exe`""
New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName -StartupType Automatic | Out-Null
Write-Host "Installed service '$ServiceName'. Start with: Start-Service $ServiceName"
Write-Host "Config is read from: $(Join-Path $InstallRoot 'service')"
