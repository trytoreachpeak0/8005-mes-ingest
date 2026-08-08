[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateRange(10, 100)]
    [int]$Runs = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runner = Join-Path $PSScriptRoot "Invoke-WatchUiTests.ps1"
$baselineDirectory = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\Baselines"
$manifests = [System.Collections.Generic.List[string]]::new()

for ($run = 1; $run -le $Runs; $run++) {
    if (Test-Path -LiteralPath $baselineDirectory -PathType Container) {
        Get-ChildItem -LiteralPath $baselineDirectory -Filter "*.received.*" -File |
            Remove-Item -Force
    }

    & $runner -Configuration $Configuration -Suite watch-xaml-visual
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -eq 2) {
        exit 2
    }
    if ($testExitCode -notin @(0, 1)) {
        throw "watch-xaml-visual exited with unexpected code $testExitCode on run $run."
    }

    $received = @(
        Get-ChildItem -LiteralPath $baselineDirectory -Filter "*.received.*" -File |
            Sort-Object Name
    )
    if ($received.Count -eq 0) {
        if ($testExitCode -ne 0) {
            throw "Run $run failed without producing received artifacts."
        }
        $manifests.Add("VERIFIED")
    } else {
        $manifest = $received |
            ForEach-Object {
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                "$($_.Name)=$hash"
            }
        $manifests.Add(($manifest -join "`n"))
    }

    Write-Host "WATCH_XAML_STABILITY_RUN: $run/$Runs"
}

$first = $manifests[0]
$unstableRun = 1..($manifests.Count - 1) |
    Where-Object { $manifests[$_] -ne $first } |
    Select-Object -First 1
if ($null -ne $unstableRun) {
    throw "Visual artifacts are not deterministic: run 1 differs from run $($unstableRun + 1)."
}

if ($first -eq "VERIFIED") {
    Write-Host "WATCH_XAML_STABLE: all approved baselines matched in $Runs consecutive runs."
} else {
    Write-Host "WATCH_XAML_CANDIDATE_STABLE: received XAML/PNG were byte-identical in $Runs consecutive runs."
    Write-Host "Review before/after/diff and the environment report; this script deliberately does not accept or rename baselines."
}
