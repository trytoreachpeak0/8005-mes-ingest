#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot 'New-WatchPrototypeProductionComparison.ps1'
$prototype = Join-Path `
    $PSScriptRoot `
    'MesIngest.Watch.FluentPrototype\review\selected-overview-current.png'
$production = Join-Path `
    $PSScriptRoot `
    'MesIngest.Watch.FluentPrototype\review\selected-overview-current-navigation.png'

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $tool,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if (@($parseErrors).Count -ne 0) {
    throw "Comparison tool has PowerShell parse errors: $($parseErrors -join '; ')"
}

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path `
    $tempBase `
    "MesIngestWatchPrototypeComparison-$([Guid]::NewGuid().ToString('N'))"
$testRootFullPath = [System.IO.Path]::GetFullPath($testRoot)
$tempPrefix = $tempBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) `
    + [System.IO.Path]::DirectorySeparatorChar
if (-not $testRootFullPath.StartsWith(
    $tempPrefix,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an unexpected test directory: $testRootFullPath"
}

try {
    New-Item -ItemType Directory -Path $testRootFullPath | Out-Null
    $expectedManifest = Join-Path $testRootFullPath 'expected-hashes.json'
    $prototypeSha256 = (
        Get-FileHash -LiteralPath $prototype -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    $productionSha256 = (
        Get-FileHash -LiteralPath $production -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    [ordered]@{
        schemaVersion = 1
        comparisonId = 'script-self-test'
        prototypeSha256 = $prototypeSha256
        productionCandidateSha256 = $productionSha256
    } | ConvertTo-Json | Set-Content -LiteralPath $expectedManifest -Encoding utf8NoBOM

    $outputRoot = Join-Path $testRootFullPath 'comparisons'
    $runDirectory = @(
        & $tool `
            -ComparisonId 'script-self-test' `
            -PrototypePng $prototype `
            -ProductionPng $production `
            -ExpectedHashManifest $expectedManifest `
            -OutputRoot $outputRoot
    ) | Select-Object -Last 1
    if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) {
        throw "Comparison run directory was not created: $runDirectory"
    }
    if (-not [string]::Equals(
        [System.IO.Path]::GetFullPath((Split-Path -Parent $runDirectory)),
        [System.IO.Path]::GetFullPath($outputRoot),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Comparison output was not created as a unique child of OutputRoot.'
    }

    $expectedFiles = @(
        'diff.png',
        'manifest.json',
        'production-candidate.png',
        'prototype.png'
    )
    $actualFiles = @(
        Get-ChildItem -LiteralPath $runDirectory -File |
            Select-Object -ExpandProperty Name |
            Sort-Object
    )
    if (@(Compare-Object $expectedFiles $actualFiles).Count -ne 0) {
        throw "Comparison run contains the wrong files: $($actualFiles -join ', ')"
    }

    . (Join-Path $PSScriptRoot 'WatchBaselineTools.ps1')
    foreach ($imageName in @('prototype.png', 'production-candidate.png', 'diff.png')) {
        Assert-WatchPngDimensions `
            -Path (Join-Path $runDirectory $imageName) `
            -Width 1440 `
            -Height 900
    }

    $manifest = Get-Content -Raw -LiteralPath (
        Join-Path $runDirectory 'manifest.json'
    ) | ConvertFrom-Json
    if ($manifest.review.state -ne 'PENDING_USER_VISUAL_REVIEW' `
        -or $manifest.review.automaticPass -ne $false `
        -or $manifest.review.thresholdConfigured -ne $false) {
        throw 'Comparison manifest must remain human-review-only without a pass threshold.'
    }
    if ($manifest.protectedArtifactPolicy.baselineReadOrWrite `
            -ne 'FORBIDDEN_AND_NOT_PERFORMED' `
        -or $manifest.protectedArtifactPolicy.receivedReadOrWrite `
            -ne 'FORBIDDEN_AND_NOT_PERFORMED') {
        throw 'Comparison manifest did not record the protected-artifact policy.'
    }
    if ($manifest.outputs.prototype.sha256 -cne $prototypeSha256 `
        -or $manifest.outputs.productionCandidate.sha256 -cne $productionSha256 `
        -or $manifest.outputs.diff.sha256 -cne (
            Get-FileHash -LiteralPath (Join-Path $runDirectory 'diff.png') -Algorithm SHA256
        ).Hash.ToUpperInvariant()) {
        throw 'Comparison manifest output hashes do not match the emitted evidence.'
    }

    $forbiddenReadRejected = $false
    try {
        & $tool `
            -ComparisonId 'forbidden-source-test' `
            -PrototypePng (Join-Path $testRootFullPath 'WindowBaselines\never-read.png') `
            -ProductionPng $production `
            -ExpectedHashManifest $expectedManifest `
            -OutputRoot (Join-Path $testRootFullPath 'forbidden-source-output') | Out-Null
    } catch {
        if ($_.Exception.Message -notmatch 'must not be inside') {
            throw
        }
        $forbiddenReadRejected = $true
    }
    if (-not $forbiddenReadRejected) {
        throw 'Comparison tool accepted a WindowBaselines source path.'
    }

    $badHashOutput = Join-Path $testRootFullPath 'bad-hash-output'
    $badHashRejected = $false
    try {
        & $tool `
            -ComparisonId 'bad-hash-test' `
            -PrototypePng $prototype `
            -ProductionPng $production `
            -ExpectedPrototypeSha256 ('0' * 64) `
            -ExpectedProductionSha256 $productionSha256 `
            -OutputRoot $badHashOutput | Out-Null
    } catch {
        if ($_.Exception.Message -notmatch 'SHA-256 mismatch') {
            throw
        }
        $badHashRejected = $true
    }
    if (-not $badHashRejected -or (Test-Path -LiteralPath $badHashOutput)) {
        throw 'Comparison tool did not reject a bad fixed hash before writing output.'
    }

    Write-Host 'WATCH_PROTOTYPE_PRODUCTION_COMPARISON_TEST_PASSED'
} finally {
    if (Test-Path -LiteralPath $testRootFullPath) {
        $resolvedTestRoot = (Resolve-Path -LiteralPath $testRootFullPath).Path
        if (-not $resolvedTestRoot.StartsWith(
            $tempPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an unexpected test directory: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
