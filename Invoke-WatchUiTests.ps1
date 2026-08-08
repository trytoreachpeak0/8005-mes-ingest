[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("watch-vm-tests", "watch-xaml-visual")]
    [string]$Suite = "watch-vm-tests"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Stop-WatchUiEnvironment {
    param([Parameter(Mandatory = $true)][string]$Reason)

    [Console]::Error.WriteLine("WATCH_UI_ENVIRONMENT_UNAVAILABLE: $Reason")
    exit 2
}

if (-not [Environment]::UserInteractive) {
    Stop-WatchUiEnvironment "The current Windows session is not interactive. Sign in to a desktop session and retry."
}

$currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
$sessionName = [Environment]::GetEnvironmentVariable("SESSIONNAME")
if ([string]::IsNullOrWhiteSpace($sessionName) -or $sessionName -eq "Services") {
    Stop-WatchUiEnvironment "No logged-in desktop session is associated with the test process."
}

$shellInSession = Get-Process -Name explorer -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $currentProcess.SessionId } |
    Select-Object -First 1
if ($null -eq $shellInSession) {
    Stop-WatchUiEnvironment "Windows Explorer is not running in session $($currentProcess.SessionId)."
}

if (-not ("WatchUiDesktop.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace WatchUiDesktop
{
    public static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);
    }
}
"@
}

$desktop = [WatchUiDesktop.NativeMethods]::OpenInputDesktop(0, $false, 0x0100)
if ($desktop -eq [IntPtr]::Zero) {
    $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Stop-WatchUiEnvironment "The interactive input desktop cannot be opened (Win32 error $lastError)."
}

[void][WatchUiDesktop.NativeMethods]::CloseDesktop($desktop)

$project = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\MesIngest.Watch.UiTests.csproj"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = "1"

if ($Suite -eq "watch-xaml-visual") {
    $probeVariable = "MESINGEST_WATCH_REQUIRE_VISUAL_ENVIRONMENT"
    $previousProbeValue = [Environment]::GetEnvironmentVariable($probeVariable)
    try {
        [Environment]::SetEnvironmentVariable($probeVariable, "1")
        & dotnet run --project $project --configuration $Configuration -- `
            -trait "Category=watch-xaml-environment" -parallel none
        $probeExitCode = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable($probeVariable, $previousProbeValue)
    }

    if ($probeExitCode -ne 0) {
        [Console]::Error.WriteLine(
            "WATCH_XAML_VISUAL_ENVIRONMENT_UNAVAILABLE: authoritative C# probe failed; Verify was not started.")
        exit 2
    }
}

$traitArgument = if ($Suite -eq "watch-xaml-visual") {
    @("-trait", "Category=watch-xaml-visual")
} else {
    @("-trait-", "Category=watch-xaml-visual")
}

Write-Host "WATCH_UI_ENVIRONMENT_OK: session=$($currentProcess.SessionId) name=$sessionName; suite=$Suite; running serial xUnit v3 UI tests."
& dotnet run --project $project --configuration $Configuration -- @traitArgument -parallel none
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
