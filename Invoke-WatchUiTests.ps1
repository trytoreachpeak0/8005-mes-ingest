[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("all", "watch-vm-tests", "watch-xaml-visual", "watch-ui-journeys", "watch-window-visual", "watch-production-preview", "watch-window-candidate-equivalence")]
    [string]$Suite = "watch-vm-tests",

    [string]$ArtifactsDirectory,

    # Iteration-only narrowing. Re-runs part of one suite so a single scenario
    # can be exercised without paying for the whole category. A narrowed run is
    # never a gate result; see docs/agents/golden-renderer.md.
    [string[]]$Class = @(),

    [string[]]$Method = @(),

    # Reuse the restore and build output already produced for this Configuration. A stability
    # loop runs the same binaries N times looking for runtime nondeterminism, so rebuilding
    # between iterations exercises the compiler rather than the renderer. The first iteration
    # must be a normal run.
    #
    # Be honest about the size of this: measured on gpt_win11 it saves the ~30 s restore on
    # iterations 2..N and a few seconds of `dotnet run` evaluation (4.4 s -> 1.8 s; the built
    # exe starts in 0.7 s). It is not why the XAML gate is slow. In that gate an iteration
    # costs ~7.3 min, of which xUnit reports 38.6 s of tests and ~6 min is the test process
    # sitting between its last written capture and process exit - see the note in
    # Test-WatchXamlBaselineStability.ps1.
    [switch]$ReuseBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "WatchBaselineTools.ps1")

$narrowingArgument = @()
foreach ($className in $Class) { $narrowingArgument += @("-class", $className) }
foreach ($methodName in $Method) { $narrowingArgument += @("-method", $methodName) }
$isNarrowedRun = $narrowingArgument.Count -gt 0
if ($isNarrowedRun -and $Suite -in @("all", "watch-production-preview")) {
    [Console]::Error.WriteLine(
        "WATCH_UI_NARROWING_REJECTED: -Class/-Method narrow a single suite; -Suite $Suite expands to several.")
    exit 4
}

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

# Every `dotnet run` below is invoked with these. -ReuseBuild adds --no-build so an
# iteration executes the existing binaries instead of rebuilding them.
$buildArguments = if ($ReuseBuild.IsPresent) { @("--no-restore", "--no-build") } else { @("--no-restore") }

if ($ReuseBuild.IsPresent) {
    $assembly = Join-Path $PSScriptRoot `
        "MesIngest.Watch.UiTests\bin\$Configuration\net8.0-windows\MesIngest.Watch.UiTests.dll"
    if (-not (Test-Path -LiteralPath $assembly)) {
        [Console]::Error.WriteLine(
            "WATCH_UI_REUSE_BUILD_UNAVAILABLE: -ReuseBuild needs an existing $Configuration build at $assembly. Run once without it first.")
        exit 5
    }

    Write-Host "WATCH_UI_REUSE_BUILD: skipping restore and build; running the existing $Configuration output."
    "skipped: -ReuseBuild reused the existing $Configuration build" |
        Out-File -LiteralPath (Join-Path $resolvedArtifacts "restore.log") -Encoding utf8
} else {
    $restore = Invoke-DotnetCaptured -Arguments @(
        "restore", $project, "--ignore-failed-sources", "-p:NuGetAudit=false")
    $restore.Output | ForEach-Object { Write-Host $_ }
    $restore.Output | Out-File -LiteralPath (Join-Path $resolvedArtifacts "restore.log") -Encoding utf8
    if ($restore.ExitCode -ne 0) {
        [Console]::Error.WriteLine(
            "WATCH_UI_RESTORE_FAILED: offline restore failed with exitCode=$($restore.ExitCode); artifacts=$resolvedArtifacts")
        exit $restore.ExitCode
    }
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
            if ($isNarrowedRun) {
                Write-Host ("WATCH_UI_PARTIAL_RECEIVED_RETAINED: a narrowed run does not clear existing " +
                    "*.received.* files, because it does not regenerate the scenarios it skips.")
            } else {
                Get-ChildItem -LiteralPath $xamlBaselineDirectory -Filter "*.received.*" -File -ErrorAction SilentlyContinue |
                    Remove-Item -Force
            }
        }

        $requiresVisualProbe = $currentSuite -in @("watch-xaml-visual", "watch-window-visual")
        if ($requiresVisualProbe) {
            $probeVariable = "MESINGEST_WATCH_REQUIRE_VISUAL_ENVIRONMENT"
            $previousProbeValue = [Environment]::GetEnvironmentVariable($probeVariable)
            try {
                [Environment]::SetEnvironmentVariable($probeVariable, "1")
                $probeArguments = @(
                    "run", "--project", $project, "--configuration", $Configuration) +
                    $buildArguments +
                    @("--", "-trait", "Category=watch-xaml-environment", "-parallel", "none")
                $probe = Invoke-DotnetCaptured -Arguments $probeArguments
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
                $journeyProbeArguments = @(
                    "run", "--project", $project, "--configuration", $Configuration) +
                    $buildArguments +
                    @("--", "-trait", "Category=watch-ui-environment", "-parallel", "none")
                $journeyProbe = Invoke-DotnetCaptured -Arguments $journeyProbeArguments
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
        if ($isNarrowedRun) {
            Write-Host ("WATCH_UI_PARTIAL_RUN: suite=$currentSuite narrowing=" + ($narrowingArgument -join " ") +
                "; iteration only, satisfies no gate and approves no baseline.")
        }
        $suiteArguments = @(
            "run", "--project", $project, "--configuration", $Configuration) +
            $buildArguments +
            @("--") +
            $traitArgument +
            $narrowingArgument +
            @("-parallel", "none")
        $suiteInvocation = Invoke-DotnetCaptured -Arguments $suiteArguments
        $suiteOutput = $suiteInvocation.Output
        $suiteExitCode = $suiteInvocation.ExitCode
        $suiteOutput | ForEach-Object { Write-Host $_ }
        $runnerLogName = if ($isNarrowedRun) { "$currentSuite.partial.runner.log" } else { "$currentSuite.runner.log" }
        $suiteOutput | Out-File -LiteralPath (Join-Path $resolvedArtifacts $runnerLogName) -Encoding utf8
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

    if ($isNarrowedRun) {
        Write-Host "WATCH_UI_PARTIAL_PASSED: suite=$Suite artifacts=$resolvedArtifacts; narrowed run, satisfies no gate."
    } else {
        Write-Host "WATCH_UI_ALL_PASSED: suite=$Suite artifacts=$resolvedArtifacts"
    }
    exit 0
} finally {
    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_RUN_REAL_WINDOWS", $null)
    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_COMPARE_WINDOW_BASELINES", $null)
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
