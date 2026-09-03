#Requires -Version 7
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '.artifacts\ticket04-tests'),
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 99)]
    [int] $ExpectedProductMajor,
    [Parameter(Mandatory = $true)]
    [ValidateRange(80, 999)]
    [int] $ExpectedCompatibilityLevel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$connectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_SQLSERVER')
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Set MES_INGEST_TICKET01_SQLSERVER to an explicitly approved real SQL Server instance.'
}
if ($connectionString.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Ticket 04 requires a real SQL Server; LocalDB cannot satisfy this gate.'
}

$testProject = Join-Path $PSScriptRoot 'MesIngest.Tests\MesIngest.Tests.csproj'
$resultsPath = [IO.Path]::GetFullPath($ResultsDirectory)
[IO.Directory]::CreateDirectory($resultsPath) | Out-Null
$trxName = 'ticket04-conflicts-sqlserver.trx'
$trxPath = Join-Path $resultsPath $trxName
Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

$productMajorVariable = 'MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR'
$compatibilityVariable = 'MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL'
$oldProductMajor = [Environment]::GetEnvironmentVariable(
    $productMajorVariable,
    [EnvironmentVariableTarget]::Process)
$oldCompatibility = [Environment]::GetEnvironmentVariable(
    $compatibilityVariable,
    [EnvironmentVariableTarget]::Process)

try {
    [Environment]::SetEnvironmentVariable(
        $productMajorVariable,
        $ExpectedProductMajor.ToString([Globalization.CultureInfo]::InvariantCulture),
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $compatibilityVariable,
        $ExpectedCompatibilityLevel.ToString([Globalization.CultureInfo]::InvariantCulture),
        [EnvironmentVariableTarget]::Process)

    & dotnet test $testProject `
        --configuration $Configuration `
        --filter 'FullyQualifiedName~DuplicateKeyAndMultipleWorkTypesTests' `
        --results-directory $resultsPath `
        --logger "trx;LogFileName=$trxName"
    if ($LASTEXITCODE -ne 0) {
        throw "Ticket 04 SQL Server/API tests failed with exit code $LASTEXITCODE."
    }

    [xml] $trx = Get-Content -Raw -LiteralPath $trxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    $total = [int] $counters.total
    $executed = [int] $counters.executed
    $passed = [int] $counters.passed
    $failed = [int] $counters.failed
    $notExecuted = [int] $counters.notExecuted
    if ($total -ne 4 -or $executed -ne 4 -or $passed -ne 4 -or $failed -ne 0 -or $notExecuted -ne 0) {
        throw "Ticket 04 gate requires exactly 4 passed and 0 skipped; observed total=$total executed=$executed passed=$passed failed=$failed notExecuted=$notExecuted."
    }

    Write-Output "MESINGEST_TICKET04_SQLSERVER_API_GATE_PASSED: passed=$passed skipped=$notExecuted expectedProductMajor=$ExpectedProductMajor expectedCompatibilityLevel=$ExpectedCompatibilityLevel trx=$trxPath"
}
finally {
    [Environment]::SetEnvironmentVariable(
        $productMajorVariable,
        $oldProductMajor,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $compatibilityVariable,
        $oldCompatibility,
        [EnvironmentVariableTarget]::Process)
}
