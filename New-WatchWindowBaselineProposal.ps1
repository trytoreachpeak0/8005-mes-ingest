[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "01-overview",
        "01e-overview-navigation-expanded",
        "01f-overview-offline-retained",
        "02-settings",
        "02v-settings-timeout-validation",
        "03-demand-series-detail",
        "04-readability-audit-detail",
        "05-area-filter-profile",
        "06-error-search-variant-a",
        "07-current-ingest-attention",
        "08-current-attention-error-drill")]
    [string]$Journey,

    [Parameter(Mandatory = $true)]
    [string]$CandidatePng,

    [Parameter(Mandatory = $true)]
    [string]$CandidateTextMask,

    [Parameter(Mandatory = $true)]
    [string]$CandidateTextMaskOverlay,

    [Parameter(Mandatory = $true)]
    [string]$EnvironmentManifest,

    [Parameter(Mandatory = $true)]
    [string]$Reason,

    [Parameter(Mandatory = $true)]
    [string]$Ticket,

    [Parameter(Mandatory = $true)]
    [string]$Submitter,

    [Parameter(Mandatory = $true)]
    [string]$Reviewer,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Initial", "RenderingOnly", "Interaction", "Copy", "Hierarchy", "StateColor")]
    [string]$ChangeType,

    [switch]$ProductOrBusinessConfirmed,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    # Must match the -Runs the candidate gate was actually driven with, so the proposal
    # records the count that produced its evidence instead of a fixed number.
    [ValidateRange(1, 100)]
    [int]$RequiredConsecutiveCandidateRuns = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "WatchBaselineTools.ps1")

if ([string]::Equals($Submitter.Trim(), $Reviewer.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Reviewer must be a non-submitter."
}

if ($ChangeType -in @("Interaction", "Copy", "Hierarchy", "StateColor") `
    -and -not $ProductOrBusinessConfirmed.IsPresent) {
    throw "ChangeType=$ChangeType requires -ProductOrBusinessConfirmed."
}

$candidate = [System.IO.Path]::GetFullPath($CandidatePng)
$candidateTextMask = [System.IO.Path]::GetFullPath($CandidateTextMask)
$candidateTextMaskOverlay = [System.IO.Path]::GetFullPath($CandidateTextMaskOverlay)
$environment = [System.IO.Path]::GetFullPath($EnvironmentManifest)
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
    throw "CandidatePng does not exist: $candidate"
}
if (-not (Test-Path -LiteralPath $environment -PathType Leaf)) {
    throw "EnvironmentManifest does not exist: $environment"
}
if (-not (Test-Path -LiteralPath $candidateTextMask -PathType Leaf)) {
    throw "CandidateTextMask does not exist: $candidateTextMask"
}
if (-not (Test-Path -LiteralPath $candidateTextMaskOverlay -PathType Leaf)) {
    throw "CandidateTextMaskOverlay does not exist: $candidateTextMaskOverlay"
}
Assert-WatchPngDimensions -Path $candidate -Width 1440 -Height 900
Assert-WatchPngDimensions -Path $candidateTextMaskOverlay -Width 1440 -Height 900

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "OutputDirectory must not already exist: $output"
}
New-Item -ItemType Directory -Path $output | Out-Null

$baseline = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\WindowBaselines\$Journey-1440x900.verified.png"
if (Test-Path -LiteralPath $baseline -PathType Leaf) {
    Copy-Item -LiteralPath $baseline -Destination (Join-Path $output "before.png")
    Write-WatchPngDiff `
        -Expected $baseline `
        -Actual $candidate `
        -Output (Join-Path $output "diff.png")
} else {
    "(none - initial baseline)" | Out-File -LiteralPath (Join-Path $output "before.txt") -Encoding utf8
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $output "diff.png")
}
Copy-Item -LiteralPath $candidate -Destination (Join-Path $output "after.png")
Copy-Item -LiteralPath $candidateTextMask -Destination (Join-Path $output "after.text-mask.json")
Copy-Item -LiteralPath $candidateTextMaskOverlay -Destination (Join-Path $output "after.text-mask-overlay.png")
Copy-Item -LiteralPath $environment -Destination (Join-Path $output "environment.txt")

@(
    "journey=$Journey",
    "reason=$Reason",
    "ticket=$Ticket",
    "submitter=$Submitter",
    "reviewer=$Reviewer",
    "changeType=$ChangeType",
    "productOrBusinessConfirmed=$($ProductOrBusinessConfirmed.IsPresent)",
    "candidateSha256=$((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash)",
    "candidateTextMaskSha256=$((Get-FileHash -LiteralPath $candidateTextMask -Algorithm SHA256).Hash)",
    "candidateTextMaskOverlaySha256=$((Get-FileHash -LiteralPath $candidateTextMaskOverlay -Algorithm SHA256).Hash)",
    "requiredConsecutiveCandidateRuns=$RequiredConsecutiveCandidateRuns",
    "approvalState=PENDING_NON_SUBMITTER_REVIEW"
) | Out-File -LiteralPath (Join-Path $output "proposal.txt") -Encoding utf8

Write-Host "WATCH_WINDOW_BASELINE_PROPOSAL_CREATED: $output"
Write-Host "No verified baseline was changed. Reviewer approval and the $RequiredConsecutiveCandidateRuns-run evidence remain required."
