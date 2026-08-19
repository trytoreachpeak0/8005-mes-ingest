#Requires -Version 5.1
<#
.SYNOPSIS
  Runs the external Watch acceptance harness against the packaged Watch binaries.

.PARAMETER Suite
  Defaults to watch-production-preview: the non-pixel behaviour suites
  (watch-vm-tests plus watch-ui-journeys). A packaged release proves that the
  published binaries start, connect, and drive the key journeys; it does not
  regenerate the pixel candidates, stability runs, baseline promotions, or DPI
  clone that ticket 23 already accepted for this UI output.

  Use watch-xaml-visual / watch-window-visual / all only when packaging actually
  changed the UI output. That invalidates the affected ticket 23 scenarios, and
  only those gates are rerun and re-approved.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $HarnessRoot,

    [ValidateSet(
        'watch-production-preview',
        'watch-vm-tests',
        'watch-xaml-visual',
        'watch-ui-journeys',
        'watch-window-visual',
        'all')]
    [string] $Suite = 'watch-production-preview',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$watchExecutable = Join-Path $packageRoot 'watch\MesIngest.Watch.exe'
$runner = Join-Path ([IO.Path]::GetFullPath($HarnessRoot)) 'Invoke-WatchUiTests.ps1'
if (-not (Test-Path -LiteralPath $watchExecutable -PathType Leaf)) {
    throw "Packaged Watch executable not found: $watchExecutable"
}
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "External acceptance harness not found: $runner"
}

$previousExecutable = [Environment]::GetEnvironmentVariable('MESINGEST_WATCH_EXECUTABLE')
try {
    [Environment]::SetEnvironmentVariable('MESINGEST_WATCH_EXECUTABLE', $watchExecutable)
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner, '-Configuration', $Configuration, '-Suite', $Suite)
    if (-not [string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
        $arguments += @('-ArtifactsDirectory', [IO.Path]::GetFullPath($ArtifactsDirectory))
    }
    $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
    & $pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "External Watch acceptance harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable('MESINGEST_WATCH_EXECUTABLE', $previousExecutable)
}
