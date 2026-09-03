#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(1, 100)]
    [int]$Runs = 3,

    [ValidateSet("Candidate", "Promoted")]
    [string]$Mode = "Candidate",

    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runner = Join-Path $PSScriptRoot "Invoke-WatchUiTests.ps1"
$root = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path -LiteralPath $root) {
    throw "ArtifactsDirectory must not already exist: $root"
}
New-Item -ItemType Directory -Path $root | Out-Null

$referenceManifest = $null
$referenceDirectory = $null
$toleratedRuns = 0
$previousCaptureMode = [Environment]::GetEnvironmentVariable(
    "MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES")
try {
    [Environment]::SetEnvironmentVariable(
        "MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES",
        $(if ($Mode -eq "Candidate") { "1" } else { $null }))
    for ($run = 1; $run -le $Runs; $run++) {
        $runDirectory = Join-Path $root ("run-{0:D2}" -f $run)
        & $runner -Configuration $Configuration -Suite watch-window-visual -ArtifactsDirectory $runDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "watch-window-visual failed on stability run $run; first failure retained at $runDirectory"
        }

        if ($Mode -eq "Candidate") {
            $candidates = @(
                Get-ChildItem -LiteralPath $runDirectory -Recurse -Filter "*.candidate.png" -File |
                    Sort-Object Name
            )
            if ($candidates.Count -ne 11) {
                throw "Expected 11 production-workspace candidates on run $run; actual=$($candidates.Count)."
            }

            $manifest = ($candidates | ForEach-Object {
                "$($_.Name)=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }) -join "`n"
            if ($null -eq $referenceManifest) {
                $referenceManifest = $manifest
                $referenceDirectory = $runDirectory
            } elseif ($manifest -ne $referenceManifest) {
                # Byte equality is the fast path. When it fails, apply the same bounded
                # visual-equivalence predicate the promoted-baseline gate uses, so both
                # gates agree on what counts as a regression. Acceptance is logged, never
                # silent, and is capped per run by the predicate's own budget.
                Write-Host "WATCH_WINDOW_CANDIDATE_NOT_BYTE_IDENTICAL: run 1 differs from run $run; evaluating visual equivalence."
                $previousReference = [Environment]::GetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_REFERENCE")
                $previousActual = [Environment]::GetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_ACTUAL")
                try {
                    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_REFERENCE", $referenceDirectory)
                    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_ACTUAL", $runDirectory)
                    & $runner -Configuration $Configuration -Suite watch-window-candidate-equivalence -ArtifactsDirectory (Join-Path $runDirectory "equivalence")
                    $equivalenceExitCode = $LASTEXITCODE
                } finally {
                    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_REFERENCE", $previousReference)
                    [Environment]::SetEnvironmentVariable("MESINGEST_WATCH_CANDIDATE_ACTUAL", $previousActual)
                }

                if ($equivalenceExitCode -ne 0) {
                    throw "Real-window candidate artifacts are not visually equivalent: run 1 differs from run $run."
                }

                $toleratedRuns++
                Write-Host "WATCH_WINDOW_CANDIDATE_VISUALLY_EQUIVALENT: run 1 vs run $run"
            }
        } else {
            $received = @(
                Get-ChildItem -LiteralPath $runDirectory -Recurse -Filter "*.received.png" -File
            )
            if ($received.Count -ne 0) {
                throw "Promoted real-window baseline run $run produced $($received.Count) received files."
            }
        }

        Write-Host "WATCH_WINDOW_BASELINE_STABILITY_RUN: mode=$Mode $run/$Runs"
    }
} finally {
    [Environment]::SetEnvironmentVariable(
        "MESINGEST_WATCH_CAPTURE_WINDOW_CANDIDATES",
        $previousCaptureMode)
}

if ($Mode -eq "Candidate") {
    $referenceManifest | Out-File -LiteralPath (Join-Path $root "candidate-manifest.txt") -Encoding utf8
    $stability = if ($toleratedRuns -eq 0) {
        "$Runs byte-identical runs"
    } else {
        "$Runs stable runs ($($Runs - $toleratedRuns) byte-identical, $toleratedRuns accepted as visually equivalent)"
    }
    Write-Host "WATCH_WINDOW_BASELINE_CANDIDATES_STABLE: $stability; artifacts=$root"
    Write-Host "This promotes nothing. Copy run-01's *.candidate.png and *.candidate.text-mask.json to *.verified.* in a commit."
} else {
    @(
        "status=PASSED",
        "mode=Promoted",
        "consecutivePasses=$Runs",
        "receivedFiles=0"
    ) | Out-File -LiteralPath (Join-Path $root "promoted-baseline-result.txt") -Encoding utf8
    Write-Host "WATCH_WINDOW_PROMOTED_BASELINES_STABLE: $Runs runs; 0 received; artifacts=$root"
}
exit 0
