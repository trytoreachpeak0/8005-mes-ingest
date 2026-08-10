[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(1, 100)]
    [int]$Runs = 50,

    [string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runner = Join-Path $PSScriptRoot "Invoke-WatchUiTests.ps1"
$root = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path $PSScriptRoot ("TestResults\watch-ui-stability\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff"))
} else {
    [System.IO.Path]::GetFullPath($ArtifactsDirectory)
}
if (Test-Path -LiteralPath $root) {
    throw "ArtifactsDirectory must not already exist: $root"
}
New-Item -ItemType Directory -Path $root | Out-Null

for ($run = 1; $run -le $Runs; $run++) {
    $runDirectory = Join-Path $root ("run-{0:D3}" -f $run)
    & $runner -Configuration $Configuration -Suite all -ArtifactsDirectory $runDirectory
    if ($LASTEXITCODE -ne 0) {
        @(
            "status=FAILED",
            "firstFailureRun=$run",
            "diagnosticRerunMayNotReplaceThisResult=true",
            "artifacts=$runDirectory"
        ) | Out-File -LiteralPath (Join-Path $root "stability-result.txt") -Encoding utf8
        exit $LASTEXITCODE
    }

    Write-Host "WATCH_UI_GATE_STABILITY_RUN: $run/$Runs"
}

@(
    "status=PASSED",
    "consecutivePasses=$Runs",
    "releaseGateEligible=$($Runs -ge 50)",
    "completedAt=$([DateTimeOffset]::Now.ToString('O'))"
) | Out-File -LiteralPath (Join-Path $root "stability-result.txt") -Encoding utf8

Write-Host "WATCH_UI_GATE_STABLE: $Runs consecutive complete-gate runs passed; artifacts=$root"
exit 0
