[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("all", "watch-vm-tests", "watch-xaml-visual", "watch-ui-journeys", "watch-window-visual", "watch-production-preview")]
    [string]$Suite = "watch-vm-tests",

    [string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "WatchBaselineTools.ps1")

function Stop-WatchUiEnvironment {
    param([Parameter(Mandatory = $true)][string]$Reason)

    [Console]::Error.WriteLine("WATCH_UI_ENVIRONMENT_UNAVAILABLE: $Reason")
    exit 2
}

function Invoke-DotnetCaptured {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    # Windows PowerShell 5.1 materializes native stderr as ErrorRecord objects and,
    # under ErrorActionPreference=Stop, can terminate before $LASTEXITCODE is read.
    # NuGet warnings must remain evidence, while the native exit code stays authoritative.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& dotnet @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    [pscustomobject]@{
        Output = $output
        ExitCode = $exitCode
    }
}

if (-not [Environment]::UserInteractive) {
    Stop-WatchUiEnvironment "The current Windows session is not interactive. Sign in to a desktop session and retry."
}

$currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
$sessionName = [Environment]::GetEnvironmentVariable("SESSIONNAME")
if ($sessionName -eq "Services") {
    Stop-WatchUiEnvironment "The test process is running in the non-interactive Services session."
}
if ([string]::IsNullOrWhiteSpace($sessionName)) {
    $sessionName = "(not exported; validated by Explorer and OpenInputDesktop)"
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
$xamlBaselineDirectory = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\Baselines\SelectedUi"
$baselineDirectory = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\WindowBaselines"
$resolvedArtifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path $PSScriptRoot ("TestResults\watch-ui\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff"))
} else {
    [System.IO.Path]::GetFullPath($ArtifactsDirectory)
}
New-Item -ItemType Directory -Path $resolvedArtifacts -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = "1"
$env:MESINGEST_WATCH_UI_ARTIFACTS = $resolvedArtifacts
$env:MESINGEST_WATCH_WINDOW_BASELINE_DIRECTORY = $baselineDirectory

$restore = Invoke-DotnetCaptured -Arguments @(
    "restore", $project, "--ignore-failed-sources", "-p:NuGetAudit=false")
$restore.Output | ForEach-Object { Write-Host $_ }
$restore.Output | Out-File -LiteralPath (Join-Path $resolvedArtifacts "restore.log") -Encoding utf8
if ($restore.ExitCode -ne 0) {
    [Console]::Error.WriteLine(
        "WATCH_UI_RESTORE_FAILED: offline restore failed with exitCode=$($restore.ExitCode); artifacts=$resolvedArtifacts")
    exit $restore.ExitCode
}

$mutex = [Threading.Mutex]::new($false, "Global\MesIngestWatchUiTests")
$hasMutex = $false
try {
    $hasMutex = $mutex.WaitOne(0)
    if (-not $hasMutex) {
        [Console]::Error.WriteLine(
            "WATCH_UI_SERIALIZATION_BUSY: another Watch UI suite owns the interactive desktop gate.")
        exit 3
    }

    $suites = if ($Suite -eq "all") {
        @("watch-vm-tests", "watch-xaml-visual", "watch-ui-journeys", "watch-window-visual")
    } elseif ($Suite -eq "watch-production-preview") {
        # Tickets 19-22 share one production-UI preview train. Keep the
        # non-pixel/UIA checks and real-window captures in one deployed payload
        # without starting either baseline comparison suite.
        @("watch-vm-tests", "watch-ui-journeys")
    } else {
        @($Suite)
    }

    foreach ($currentSuite in $suites) {
        if ($currentSuite -eq "watch-xaml-visual") {
            Get-ChildItem -LiteralPath $xamlBaselineDirectory -Filter "*.received.*" -File -ErrorAction SilentlyContinue |
                Remove-Item -Force
        }

        $requiresVisualProbe = $currentSuite -in @("watch-xaml-visual", "watch-window-visual")
        if ($requiresVisualProbe) {
            $probeVariable = "MESINGEST_WATCH_REQUIRE_VISUAL_ENVIRONMENT"
            $previousProbeValue = [Environment]::GetEnvironmentVariable($probeVariable)
            try {
                [Environment]::SetEnvironmentVariable($probeVariable, "1")
                $probe = Invoke-DotnetCaptured -Arguments @(
                    "run", "--project", $project, "--configuration", $Configuration, "--no-restore", "--",
                    "-trait", "Category=watch-xaml-environment", "-parallel", "none")
                $probeOutput = $probe.Output
                $probeExitCode = $probe.ExitCode
            } finally {
                [Environment]::SetEnvironmentVariable($probeVariable, $previousProbeValue)
            }

            $probeOutput | ForEach-Object { Write-Host $_ }
            if ($probeExitCode -ne 0) {
                $environmentUnavailable = $probeOutput | Where-Object {
                    $_.ToString().Contains("WATCH_XAML_VISUAL_ENVIRONMENT_UNAVAILABLE:")
                }
                if ($null -ne $environmentUnavailable) {
                    [Console]::Error.WriteLine(
                        "WATCH_XAML_VISUAL_ENVIRONMENT_UNAVAILABLE: authoritative C# probe rejected this desktop; visual tests were not started.")
                    exit 2
                }

                [Console]::Error.WriteLine(
                    "WATCH_XAML_VISUAL_PROBE_FAILED: build or test infrastructure failed before visual tests started.")
                exit $probeExitCode
            }
        }

        if ($currentSuite -eq "watch-ui-journeys") {
            $journeyProbeVariable = "MESINGEST_WATCH_REQUIRE_JOURNEY_ENVIRONMENT"
            $previousJourneyProbeValue = [Environment]::GetEnvironmentVariable($journeyProbeVariable)
            try {
                [Environment]::SetEnvironmentVariable($journeyProbeVariable, "1")
                $journeyProbe = Invoke-DotnetCaptured -Arguments @(
                    "run", "--project", $project, "--configuration", $Configuration, "--no-restore", "--",
                    "-trait", "Category=watch-ui-environment", "-parallel", "none")
                $journeyProbeOutput = $journeyProbe.Output
                $journeyProbeExitCode = $journeyProbe.ExitCode
            } finally {
                [Environment]::SetEnvironmentVariable($journeyProbeVariable, $previousJourneyProbeValue)
            }
            $journeyProbeOutput | ForEach-Object { Write-Host $_ }
            if ($journeyProbeExitCode -ne 0) {
                [Console]::Error.WriteLine(
                    "WATCH_UI_JOURNEY_ENVIRONMENT_UNAVAILABLE: real-window UIA journeys were not started.")
                exit 2
            }
        }

        [Environment]::SetEnvironmentVariable(
            "MESINGEST_WATCH_RUN_REAL_WINDOWS",
            $(if ($currentSuite -in @("watch-ui-journeys", "watch-window-visual")) { "1" } else { $null }))
        [Environment]::SetEnvironmentVariable(
            "MESINGEST_WATCH_COMPARE_WINDOW_BASELINES",
            $(if ($currentSuite -eq "watch-window-visual") { "1" } else { $null }))

        $traitArgument = if ($currentSuite -eq "watch-vm-tests") {
            @(
                "-trait-", "Category=watch-xaml-visual",
                "-trait-", "Category=watch-ui-journeys",
                "-trait-", "Category=watch-window-visual",
                "-trait-", "Category=watch-window-legacy",
                "-trait-", "Category=watch-window-nonbaseline")
        } else {
            @("-trait", "Category=$currentSuite")
        }

        Write-Host "WATCH_UI_ENVIRONMENT_OK: session=$($currentProcess.SessionId) name=$sessionName; suite=$currentSuite; artifacts=$resolvedArtifacts; running serial xUnit v3 UI tests."
        $suiteArguments = @(
            "run", "--project", $project, "--configuration", $Configuration, "--no-restore", "--") `
            + $traitArgument `
            + @("-parallel", "none")
        $suiteInvocation = Invoke-DotnetCaptured -Arguments $suiteArguments
        $suiteOutput = $suiteInvocation.Output
        $suiteExitCode = $suiteInvocation.ExitCode
        $suiteOutput | ForEach-Object { Write-Host $_ }
        $suiteOutput | Out-File -LiteralPath (Join-Path $resolvedArtifacts "$currentSuite.runner.log") -Encoding utf8
        if ($suiteExitCode -ne 0) {
            if ($currentSuite -eq "watch-xaml-visual") {
                $xamlEvidence = Join-Path $resolvedArtifacts "watch-xaml-visual-evidence"
                New-Item -ItemType Directory -Path $xamlEvidence -Force | Out-Null
                $receivedFiles = @(Get-ChildItem -LiteralPath $xamlBaselineDirectory -Filter "*.received.*" -File -ErrorAction SilentlyContinue)
                $receivedFiles | Copy-Item -Destination $xamlEvidence
                Get-ChildItem -LiteralPath $xamlBaselineDirectory -Filter "*.verified.*" -File -ErrorAction SilentlyContinue |
                    Copy-Item -Destination $xamlEvidence
                foreach ($receivedPng in @($receivedFiles | Where-Object Name -Like "*.received.png")) {
                    $scenario = $receivedPng.Name.Substring(
                        0,
                        $receivedPng.Name.Length - ".received.png".Length)
                    $verifiedPng = Join-Path $xamlBaselineDirectory "$scenario.verified.png"
                    if (Test-Path -LiteralPath $verifiedPng -PathType Leaf) {
                        Copy-Item -LiteralPath $verifiedPng -Destination (Join-Path $xamlEvidence "$scenario.expected.png")
                        Copy-Item -LiteralPath $receivedPng.FullName -Destination (Join-Path $xamlEvidence "$scenario.actual.png")
                        Write-WatchPngDiff `
                            -Expected $verifiedPng `
                            -Actual $receivedPng.FullName `
                            -Output (Join-Path $xamlEvidence "$scenario.diff.png")
                    }
                }
                $probeOutput | Out-File -LiteralPath (Join-Path $xamlEvidence "environment-probe.log") -Encoding utf8
            }
            [Console]::Error.WriteLine(
                "WATCH_UI_FIRST_FAILURE_RETAINED: suite=$currentSuite exitCode=$suiteExitCode artifacts=$resolvedArtifacts")
            exit $suiteExitCode
        }
    }

    Write-Host "WATCH_UI_ALL_PASSED: suite=$Suite artifacts=$resolvedArtifacts"
    exit 0
} finally {
    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_RUN_REAL_WINDOWS", $null)
    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_COMPARE_WINDOW_BASELINES", $null)
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
