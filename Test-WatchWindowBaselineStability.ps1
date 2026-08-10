[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(10, 100)]
    [int]$Runs = 10,

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
            if ($candidates.Count -ne 5) {
                throw "Expected 5 real-window candidates on run $run; actual=$($candidates.Count)."
            }

            $manifest = ($candidates | ForEach-Object {
                "$($_.Name)=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }) -join "`n"
            if ($null -eq $referenceManifest) {
                $referenceManifest = $manifest
            } elseif ($manifest -ne $referenceManifest) {
                throw "Real-window candidate artifacts are not byte-identical: run 1 differs from run $run."
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
    Write-Host "WATCH_WINDOW_BASELINE_CANDIDATES_STABLE: $Runs byte-identical runs; artifacts=$root"
    Write-Host "This does not approve baselines; create review proposals and obtain the required reviewers."
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
