#Requires -Version 5.1
<#
.SYNOPSIS
  Run the exact MesIngest Tier 1 suite against an approved real SQL Server and attest the TRX.

.DESCRIPTION
  This source-worktree gate is separate from the read-only runtime collector because SQL
  integration tests create and remove isolated test databases. It rejects LocalDB and requires a
  connection aimed at master. The emitted attestation contains no connection string or credential.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateRange(1, 99)] [int] $ExpectedProductMajor,
    [Parameter(Mandatory = $true)] [ValidateRange(80, 999)] [int] $ExpectedCompatibilityLevel,
    [string] $ResultsRoot = (Join-Path $PSScriptRoot '.artifacts\runtime-feedback-tier1-attested')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$connectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_SQLSERVER')
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Set MES_INGEST_TICKET01_SQLSERVER to an explicitly approved real SQL Server master connection.'
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
if ($builder.DataSource.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Runtime feedback Tier 1 requires a real SQL Server; LocalDB is rejected.'
}
if (-not [string]::Equals($builder.InitialCatalog, 'master', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Runtime feedback Tier 1 requires a connection aimed at master so tests use isolated databases.'
}
$builder['Connect Timeout'] = 5
$builder['Application Name'] = 'MesIngest.RuntimeFeedback.Tier1Attestation'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 10
        $command.CommandText = @"
SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS product_major,
       d.compatibility_level
FROM sys.databases AS d
WHERE d.name = DB_NAME();
"@
        $reader = $command.ExecuteReader()
        try {
            if (-not $reader.Read()) { throw 'SQL Server identity query returned no row.' }
            $actualProductMajor = [Convert]::ToInt32($reader.GetValue(0), [Globalization.CultureInfo]::InvariantCulture)
            $actualCompatibilityLevel = [Convert]::ToInt32($reader.GetValue(1), [Globalization.CultureInfo]::InvariantCulture)
        } finally { $reader.Dispose() }
    } finally { $command.Dispose() }
} finally { $connection.Dispose() }

if ($actualProductMajor -ne $ExpectedProductMajor -or
    $actualCompatibilityLevel -ne $ExpectedCompatibilityLevel) {
    throw "SQL Server identity mismatch: productMajor=$actualProductMajor compatibilityLevel=$actualCompatibilityLevel."
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

$startedAt = [DateTimeOffset]::UtcNow
$runDirectory = Join-Path ([IO.Path]::GetFullPath($ResultsRoot)) ('run-' + $startedAt.ToString('yyyyMMddTHHmmssZ'))
if (Test-Path -LiteralPath $runDirectory) { throw "Refusing to overwrite Tier 1 run: $runDirectory" }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$trxName = 'runtime-feedback-tier1.trx'
$trxPath = Join-Path $runDirectory $trxName
$attestationPath = Join-Path $runDirectory 'runtime-feedback-tier1-attestation.json'
$commandPattern = 'dotnet test MesIngest.Tests --configuration Release --results-directory <path> --logger "trx;LogFileName=runtime-feedback-tier1.trx"'

$productVariable = 'MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR'
$compatibilityVariable = 'MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL'
$oldProduct = [Environment]::GetEnvironmentVariable($productVariable, [EnvironmentVariableTarget]::Process)
$oldCompatibility = [Environment]::GetEnvironmentVariable($compatibilityVariable, [EnvironmentVariableTarget]::Process)
$testExitCode = -1
try {
    [Environment]::SetEnvironmentVariable($productVariable, $ExpectedProductMajor.ToString([Globalization.CultureInfo]::InvariantCulture), [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable($compatibilityVariable, $ExpectedCompatibilityLevel.ToString([Globalization.CultureInfo]::InvariantCulture), [EnvironmentVariableTarget]::Process)

    & dotnet test MesIngest.Tests `
        --configuration Release `
        --results-directory $runDirectory `
        --logger "trx;LogFileName=$trxName"
    $testExitCode = $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable($productVariable, $oldProduct, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable($compatibilityVariable, $oldCompatibility, [EnvironmentVariableTarget]::Process)
}

if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Tier 1 did not produce its required TRX: $trxPath"
}
[xml]$trx = Get-Content -Raw -LiteralPath $trxPath
$counters = $trx.TestRun.ResultSummary.Counters
$total = [int]$counters.total
$executed = [int]$counters.executed
$passed = [int]$counters.passed
$failed = [int]$counters.failed
$skipped = $total - $executed
$storageNames = @($trx.TestRun.TestDefinitions.UnitTest | ForEach-Object { [IO.Path]::GetFileName([string]$_.storage) } | Sort-Object -Unique)
$completedAt = [DateTimeOffset]::UtcNow
$sourceCommit = @(& git -C $PSScriptRoot rev-parse HEAD 2>$null) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = 'unknown' }
$hostAssemblyPath = Join-Path $PSScriptRoot 'MesIngest.Host\bin\Release\net8.0\MesIngest.Host.dll'
$testAssemblyPath = Join-Path $PSScriptRoot 'MesIngest.Tests\bin\Release\net8.0-windows\MesIngest.Tests.dll'
if (-not (Test-Path -LiteralPath $hostAssemblyPath -PathType Leaf)) {
    throw "Tier 1 did not produce the Host assembly to attest: $hostAssemblyPath"
}
if (-not (Test-Path -LiteralPath $testAssemblyPath -PathType Leaf)) {
    throw "Tier 1 did not produce the test assembly to attest: $testAssemblyPath"
}

$attestation = [ordered]@{
    schemaVersion = 2
    commandPattern = $commandPattern
    workingDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
    startedAt = $startedAt.ToString('o')
    completedAt = $completedAt.ToString('o')
    exitCode = $testExitCode
    platform = 'VSTest'
    framework = 'xUnit v2'
    testAssembly = if ($storageNames.Count -eq 1) { $storageNames[0] } else { $storageNames -join ',' }
    testAssemblySha256 = Get-FileSha256 $testAssemblyPath
    hostAssembly = [IO.Path]::GetFileName($hostAssemblyPath)
    hostAssemblySha256 = Get-FileSha256 $hostAssemblyPath
    sourceCommit = $sourceCommit
    sdkVersion = [string](& dotnet --version)
    trxFile = $trxName
    trxSha256 = Get-FileSha256 $trxPath
    counts = [ordered]@{ total = $total; executed = $executed; passed = $passed; failed = $failed; skipped = $skipped }
    sqlTarget = [ordered]@{ dataSource = $builder.DataSource; database = $builder.InitialCatalog }
    expectedProductMajor = $ExpectedProductMajor.ToString([Globalization.CultureInfo]::InvariantCulture)
    expectedCompatibilityLevel = $ExpectedCompatibilityLevel.ToString([Globalization.CultureInfo]::InvariantCulture)
    actualProductMajor = $actualProductMajor
    actualCompatibilityLevel = $actualCompatibilityLevel
    credentialsWritten = $false
    connectionStringWritten = $false
}
$attestation | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $attestationPath -Encoding UTF8

Write-Output "MESINGEST_RUNTIME_FEEDBACK_TIER1: failed=$failed passed=$passed skipped=$skipped total=$total exitCode=$testExitCode"
Write-Output "TRX=$trxPath"
Write-Output "ATTESTATION=$attestationPath"
if ($testExitCode -ne 0 -or $failed -ne 0 -or $skipped -ne 0 -or $total -lt 700 -or
    $storageNames.Count -ne 1 -or
    -not [string]::Equals($storageNames[0], 'MesIngest.Tests.dll', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Runtime feedback Tier 1 did not satisfy the full-suite real-SQL gate.'
}
