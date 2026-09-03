#Requires -Version 7.5

<#
.SYNOPSIS
Creates human-review evidence for a selected prototype and a formal production PNG.

.DESCRIPTION
Both PNG identities must be fixed either by ExpectedHashManifest or by the two
Expected*Sha256 parameters. An expected-hash manifest has this minimal shape:
{
  "comparisonId": "selected-overview-current",
  "prototypeSha256": "<64 hex characters>",
  "productionCandidateSha256": "<64 hex characters>"
}

The command creates one unique run directory containing only prototype.png,
production-candidate.png, diff.png, and manifest.json. It does not decide visual
acceptance and rejects protected baseline/received paths before file access.
#>

[CmdletBinding(DefaultParameterSetName = 'Manifest')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ComparisonId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PrototypePng,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductionPng,

    [Parameter(Mandatory = $true, ParameterSetName = 'Manifest')]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedHashManifest,

    [Parameter(Mandatory = $true, ParameterSetName = 'Hashes')]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedPrototypeSha256,

    [Parameter(Mandatory = $true, ParameterSetName = 'Hashes')]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedProductionSha256,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ComparisonPathIsOutsideProtectedVisualArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$FullPath,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $segments = @($FullPath -split '[\\/]+' | Where-Object { $_.Length -gt 0 })
    if ($segments | Where-Object { $_ -ieq 'Baselines' -or $_ -ieq 'WindowBaselines' }) {
        throw "$Role must not be inside a Baselines or WindowBaselines directory: $FullPath"
    }
    if ($segments | Where-Object { $_ -match '(?i)\.received\.' }) {
        throw "$Role must not reference a *.received.* artifact: $FullPath"
    }

    $current = $FullPath
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -Force -LiteralPath $current
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Role must not traverse a symbolic link or junction: $current"
            }
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) `
            -or [string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = $parent
    }
}

function Resolve-ComparisonInputFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Role,
        [string]$RequiredExtension
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-ComparisonPathIsOutsideProtectedVisualArtifacts -FullPath $fullPath -Role $Role
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Role does not exist: $fullPath"
    }

    $resolved = (Resolve-Path -LiteralPath $fullPath -ErrorAction Stop).Path
    Assert-ComparisonPathIsOutsideProtectedVisualArtifacts -FullPath $resolved -Role $Role
    if (-not [string]::IsNullOrWhiteSpace($RequiredExtension) `
        -and -not [string]::Equals(
            [System.IO.Path]::GetExtension($resolved),
            $RequiredExtension,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role must be a $RequiredExtension file: $resolved"
    }

    return $resolved
}

function ConvertTo-ComparisonSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $normalized = $Value.Trim()
    if ($normalized -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Role must be exactly 64 hexadecimal SHA-256 characters."
    }

    return $normalized.ToUpperInvariant()
}

function Get-RequiredManifestString {
    param(
        [Parameter(Mandatory = $true)][psobject]$Manifest,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $property = $Manifest.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "ExpectedHashManifest must contain a non-empty '$PropertyName' property."
    }

    return [string]$property.Value
}

$prototypeFullPath = [System.IO.Path]::GetFullPath($PrototypePng)
$productionFullPath = [System.IO.Path]::GetFullPath($ProductionPng)
$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
Assert-ComparisonPathIsOutsideProtectedVisualArtifacts `
    -FullPath $prototypeFullPath `
    -Role 'PrototypePng'
Assert-ComparisonPathIsOutsideProtectedVisualArtifacts `
    -FullPath $productionFullPath `
    -Role 'ProductionPng'
Assert-ComparisonPathIsOutsideProtectedVisualArtifacts `
    -FullPath $outputRootFullPath `
    -Role 'OutputRoot'

$expectedManifestFullPath = $null
$expectedManifestSha256 = $null
if ($PSCmdlet.ParameterSetName -eq 'Manifest') {
    $expectedManifestFullPath = [System.IO.Path]::GetFullPath($ExpectedHashManifest)
    Assert-ComparisonPathIsOutsideProtectedVisualArtifacts `
        -FullPath $expectedManifestFullPath `
        -Role 'ExpectedHashManifest'
}

$prototype = Resolve-ComparisonInputFile `
    -Path $prototypeFullPath `
    -Role 'PrototypePng' `
    -RequiredExtension '.png'
$production = Resolve-ComparisonInputFile `
    -Path $productionFullPath `
    -Role 'ProductionPng' `
    -RequiredExtension '.png'

if ($PSCmdlet.ParameterSetName -eq 'Manifest') {
    $expectedManifestFullPath = Resolve-ComparisonInputFile `
        -Path $expectedManifestFullPath `
        -Role 'ExpectedHashManifest'
    $expectedManifest = Get-Content -Raw -LiteralPath $expectedManifestFullPath | ConvertFrom-Json -DateKind String
    $expectedManifestComparisonId = $expectedManifest.PSObject.Properties['comparisonId']
    if ($null -ne $expectedManifestComparisonId `
        -and -not [string]::Equals(
            $ComparisonId.Trim(),
            [string]$expectedManifestComparisonId.Value,
            [StringComparison]::Ordinal)) {
        throw "ComparisonId does not match ExpectedHashManifest comparisonId."
    }

    $ExpectedPrototypeSha256 = Get-RequiredManifestString `
        -Manifest $expectedManifest `
        -PropertyName 'prototypeSha256'
    $ExpectedProductionSha256 = Get-RequiredManifestString `
        -Manifest $expectedManifest `
        -PropertyName 'productionCandidateSha256'
    $expectedManifestSha256 = (
        Get-FileHash -LiteralPath $expectedManifestFullPath -Algorithm SHA256
    ).Hash.ToUpperInvariant()
}

$expectedPrototype = ConvertTo-ComparisonSha256 `
    -Value $ExpectedPrototypeSha256 `
    -Role 'ExpectedPrototypeSha256'
$expectedProduction = ConvertTo-ComparisonSha256 `
    -Value $ExpectedProductionSha256 `
    -Role 'ExpectedProductionSha256'
$actualPrototype = (
    Get-FileHash -LiteralPath $prototype -Algorithm SHA256
).Hash.ToUpperInvariant()
$actualProduction = (
    Get-FileHash -LiteralPath $production -Algorithm SHA256
).Hash.ToUpperInvariant()
if ($actualPrototype -cne $expectedPrototype) {
    throw "PrototypePng SHA-256 mismatch. expected=$expectedPrototype actual=$actualPrototype"
}
if ($actualProduction -cne $expectedProduction) {
    throw "ProductionPng SHA-256 mismatch. expected=$expectedProduction actual=$actualProduction"
}

. (Join-Path $PSScriptRoot 'WatchBaselineTools.ps1')
Assert-WatchPngDimensions -Path $prototype -Width 1440 -Height 900
Assert-WatchPngDimensions -Path $production -Width 1440 -Height 900

if (Test-Path -LiteralPath $outputRootFullPath -PathType Leaf) {
    throw "OutputRoot must be a directory, not a file: $outputRootFullPath"
}

$safeComparisonId = $ComparisonId.Trim() -replace '[^A-Za-z0-9._-]', '-'
$safeComparisonId = $safeComparisonId.Trim([char[]]@('.', '-', '_'))
if ([string]::IsNullOrWhiteSpace($safeComparisonId)) {
    $safeComparisonId = 'comparison'
}
if ($safeComparisonId.Length -gt 64) {
    $safeComparisonId = $safeComparisonId.Substring(0, 64)
}

$stamp = [DateTimeOffset]::UtcNow.ToString(
    'yyyyMMdd-HHmmssfff',
    [CultureInfo]::InvariantCulture)
$nonce = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runName = "run-$stamp-$safeComparisonId-$nonce"
$runDirectory = Join-Path $outputRootFullPath $runName
Assert-ComparisonPathIsOutsideProtectedVisualArtifacts `
    -FullPath $runDirectory `
    -Role 'run directory'
if (Test-Path -LiteralPath $runDirectory) {
    throw "Unique comparison run directory already exists: $runDirectory"
}

if (-not (Test-Path -LiteralPath $outputRootFullPath)) {
    New-Item -ItemType Directory -Path $outputRootFullPath | Out-Null
}
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$prototypeOutput = Join-Path $runDirectory 'prototype.png'
$productionOutput = Join-Path $runDirectory 'production-candidate.png'
$diffOutput = Join-Path $runDirectory 'diff.png'
$manifestOutput = Join-Path $runDirectory 'manifest.json'

Copy-Item -LiteralPath $prototype -Destination $prototypeOutput
Copy-Item -LiteralPath $production -Destination $productionOutput
$prototypeOutputSha256 = (
    Get-FileHash -LiteralPath $prototypeOutput -Algorithm SHA256
).Hash.ToUpperInvariant()
$productionOutputSha256 = (
    Get-FileHash -LiteralPath $productionOutput -Algorithm SHA256
).Hash.ToUpperInvariant()
if ($prototypeOutputSha256 -cne $expectedPrototype) {
    throw 'PrototypePng changed while the comparison evidence was being copied.'
}
if ($productionOutputSha256 -cne $expectedProduction) {
    throw 'ProductionPng changed while the comparison evidence was being copied.'
}

Write-WatchPngDiff `
    -Expected $prototypeOutput `
    -Actual $productionOutput `
    -Output $diffOutput
$diffSha256 = (
    Get-FileHash -LiteralPath $diffOutput -Algorithm SHA256
).Hash.ToUpperInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    comparisonId = $ComparisonId.Trim()
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    purpose = 'selected-prototype-vs-formal-production-candidate'
    review = [ordered]@{
        state = 'PENDING_USER_VISUAL_REVIEW'
        automaticPass = $false
        thresholdConfigured = $false
    }
    imageContract = [ordered]@{
        width = 1440
        height = 900
        pixelDiff = 'exact BGRA inequality; magenta=changed; light-gray=equal'
    }
    inputs = [ordered]@{
        expectedHashManifest = if ($null -eq $expectedManifestFullPath) {
            $null
        } else {
            [ordered]@{
                path = $expectedManifestFullPath
                sha256 = $expectedManifestSha256
            }
        }
        prototype = [ordered]@{
            path = $prototype
            expectedSha256 = $expectedPrototype
            actualSha256 = $actualPrototype
        }
        productionCandidate = [ordered]@{
            path = $production
            expectedSha256 = $expectedProduction
            actualSha256 = $actualProduction
        }
    }
    outputs = [ordered]@{
        prototype = [ordered]@{
            file = 'prototype.png'
            sha256 = $prototypeOutputSha256
        }
        productionCandidate = [ordered]@{
            file = 'production-candidate.png'
            sha256 = $productionOutputSha256
        }
        diff = [ordered]@{
            file = 'diff.png'
            sha256 = $diffSha256
        }
    }
    protectedArtifactPolicy = [ordered]@{
        baselineReadOrWrite = 'FORBIDDEN_AND_NOT_PERFORMED'
        receivedReadOrWrite = 'FORBIDDEN_AND_NOT_PERFORMED'
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath $manifestOutput `
    -Encoding utf8NoBOM

Write-Host "WATCH_PROTOTYPE_PRODUCTION_COMPARISON_CREATED: $runDirectory"
Write-Host 'Human visual review remains required; this comparison has no automatic pass threshold.'
Write-Output $runDirectory
