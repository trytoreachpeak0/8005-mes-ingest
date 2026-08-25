#Requires -Version 5.1
<#
.SYNOPSIS
  Run Ticket 28's controlled-time contract tests and attest the exact executed test assembly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PackageRoot,
    [string] $ResultsRoot = (Join-Path $PSScriptRoot '.artifacts\ticket28-deterministic-contract')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

$resolvedPackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$hostPath = Join-Path $resolvedPackageRoot 'service\MesIngest.Host.dll'
$manifestPath = Join-Path $resolvedPackageRoot 'RELEASE-MANIFEST.json'
foreach ($requiredPath in @($hostPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Ticket 28 deterministic contract requires the complete package file: $requiredPath"
    }
}
$sourceCommitBefore = @(& git -C $PSScriptRoot rev-parse HEAD 2>$null) | Select-Object -First 1
$sourceStatusBefore = @(& git -C $PSScriptRoot status --porcelain=v1 --untracked-files=normal -- `
    . `
    ':(exclude).artifacts/**' `
    ':(exclude)MesIngest.Tests/TestResults/**' 2>$null)
if ([string]::IsNullOrWhiteSpace($sourceCommitBefore) -or $sourceStatusBefore.Count -gt 0) {
    throw 'Ticket 28 deterministic contract requires a clean source worktree before testing.'
}

$startedAt = [DateTimeOffset]::UtcNow
$runDirectory = Join-Path ([IO.Path]::GetFullPath($ResultsRoot)) `
    ('run-' + $startedAt.ToString('yyyyMMddTHHmmssZ'))
if (Test-Path -LiteralPath $runDirectory) { throw "Refusing to overwrite deterministic run: $runDirectory" }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$trxName = 'ticket28-deterministic.trx'
$trxPath = Join-Path $runDirectory $trxName
$filter = 'FullyQualifiedName~SingleFlightPollLoopTests|FullyQualifiedName~HistoryCleanupHostedServiceTests|FullyQualifiedName~HistoryRetentionStateTests.Retention_clocks_use_exact_fifteen_day_boundaries|FullyQualifiedName~WatchV2AutoRefreshTests.Settings_cover_all_five_host_data_views_and_expose_only_an_interval'

& dotnet test MesIngest.Tests `
    --configuration Release `
    --filter $filter `
    --results-directory $runDirectory `
    --logger "trx;LogFileName=$trxName"
$testExitCode = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Ticket 28 deterministic run did not produce its TRX: $trxPath"
}

[xml]$trx = Get-Content -Raw -LiteralPath $trxPath
$counters = $trx.TestRun.ResultSummary.Counters
$total = [int]$counters.total
$executed = [int]$counters.executed
$passed = [int]$counters.passed
$failed = [int]$counters.failed
$skipped = $total - $executed
$testAssemblyPaths = @($trx.TestRun.TestDefinitions.UnitTest | ForEach-Object {
    [IO.Path]::GetFullPath([string]$_.storage)
} | Sort-Object -Unique)
if ($testAssemblyPaths.Count -ne 1 -or
    -not (Test-Path -LiteralPath $testAssemblyPaths[0] -PathType Leaf)) {
    throw 'Ticket 28 deterministic TRX does not identify one available executed test assembly.'
}
$testAssemblyPath = $testAssemblyPaths[0]
$testAssemblyCopy = Join-Path $runDirectory 'MesIngest.Tests.dll'
Copy-Item -LiteralPath $testAssemblyPath -Destination $testAssemblyCopy
$testAssemblySha256 = Get-FileSha256 $testAssemblyPath
if ((Get-FileSha256 $testAssemblyCopy) -cne $testAssemblySha256) {
    throw 'Copied deterministic test assembly does not match the assembly named by the TRX.'
}

$sourceCommitAfter = @(& git -C $PSScriptRoot rev-parse HEAD 2>$null) | Select-Object -First 1
$sourceStatusAfter = @(& git -C $PSScriptRoot status --porcelain=v1 --untracked-files=normal -- `
    . `
    ':(exclude).artifacts/**' `
    ':(exclude)MesIngest.Tests/TestResults/**' 2>$null)
if ($sourceCommitAfter -cne $sourceCommitBefore -or $sourceStatusAfter.Count -gt 0) {
    throw 'Ticket 28 deterministic contract source identity changed during testing.'
}
$sourceCommit = $sourceCommitAfter
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ([string]$manifest.sourceCommit -cne $sourceCommit -or [bool]$manifest.sourceDirty) {
    throw 'Deterministic contract package is not a clean build of the current source commit.'
}

$attestation = [ordered]@{
    schemaVersion = 1
    sourceCommit = $sourceCommit
    hostSha256 = Get-FileSha256 $hostPath
    packageManifestSha256 = Get-FileSha256 $manifestPath
    testAssemblyFile = [IO.Path]::GetFileName($testAssemblyCopy)
    testAssemblySha256 = $testAssemblySha256
    executedTestAssemblyPath = $testAssemblyPath
    trxFile = $trxName
    trxSha256 = Get-FileSha256 $trxPath
    passed = $passed; failed = $failed; skipped = $skipped; total = $total
    exitCode = $testExitCode
    startedAt = $startedAt.ToString('o')
    completedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$attestationPath = Join-Path $runDirectory 'ticket28-deterministic-attestation.json'
$attestation | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $attestationPath -Encoding UTF8

Write-Output "MESINGEST_TICKET28_DETERMINISTIC: failed=$failed passed=$passed skipped=$skipped total=$total exitCode=$testExitCode"
Write-Output "TRX=$trxPath"
Write-Output "ATTESTATION=$attestationPath"
if ($testExitCode -ne 0 -or $failed -ne 0 -or $skipped -ne 0) {
    throw 'Ticket 28 deterministic contract tests did not pass without skips.'
}
