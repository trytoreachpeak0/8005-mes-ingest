#Requires -Version 5.1
<#
.SYNOPSIS
  Build an owned MesIngest scale database and capture fail-closed query/storage evidence.

.DESCRIPTION
  This destructive validation tool is intentionally separate from the daily Host. It creates only
  a new, explicitly named MesIngest_Scale_* database on a real SQL Server master connection,
  marks ownership with a database extended property, drives the packaged production Host through
  its V2 HTTP API, captures actual plans and statement metrics with Extended Events, writes an
  immutable evidence bundle, and removes only the database whose ownership marker matches this run.

  The 0/7/30 profiles use the calibrated 14-second round cadence and 600 observations per round.
  Profile 0 retains one current round but no historical rounds. Profiles 7 and 30 retain the same
  current state and add the exact equivalent historical round count.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(0, 7, 30)]
    [int] $ProfileDays,

    [Parameter(Mandatory = $true)]
    [string] $DatabaseName,

    [Parameter(Mandatory = $true)]
    [string] $ConfirmIsolatedDatabase,

    [string] $ServiceRoot = '',
    [string] $OutputRoot = '',
    [string] $SqlConnectionStringEnvironmentVariable = 'MES_INGEST_SCALE_EVIDENCE_SQLSERVER',
    [string] $SqlTier1AttestationPath = '',
    [string] $BaselineEvidencePath = '',
    [string] $ValidateEvidenceFixturePath = '',
    [string] $ValidatePercentileFixture = '',
    [string] $ValidateShowPlanFixturePath = '',
    [ValidateSet(
        'All',
        'DemandSeries',
        'ExternallyReadableDemandCatalog',
        'CurrentIngestAttention',
        'Overview',
        'ReadabilityAudit',
        'ErrorSearch',
        'RawEvidence')]
    [string] $QuerySurface = 'All',
    [string] $ConfirmFullScaleEscalation = '',
    [ValidateSet('Release', 'Debug', 'Published')] [string] $BuildConfiguration = 'Release',
    [ValidateRange(1, 10000)] [int] $SeriesCount = 600,
    [ValidateRange(1, 10000)] [int] $ObservationsPerRound = 600,
    [ValidateRange(1, 3600)] [int] $RoundIntervalSeconds = 14,
    [ValidateRange(1, 10000)] [int] $RoundBatchSize = 250,
    [ValidateRange(0, 100000)] [long] $RepresentativeHistoryRounds = 0,
    [ValidateRange(1, 100)] [int] $WarmupCount = 2,
    [ValidateRange(1, 1000)] [int] $MeasurementCount = 5,
    [ValidateRange(1, 600)] [int] $RequestTimeoutSeconds = 120,
    [DateTimeOffset] $AnchorUtc = [DateTimeOffset]::Parse('2026-08-01T00:00:00Z'),
    [switch] $KeepDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredConfirmation = 'MESINGEST_SCALE_EVIDENCE_ONLY'
$databasePrefix = 'MesIngest_Scale_'
$systemDatabases = @('master', 'model', 'msdb', 'tempdb')

# Validate destructive identity before reading a connection string, contacting SQL, or creating output.
if ($ConfirmIsolatedDatabase -cne $requiredConfirmation) {
    throw "ConfirmIsolatedDatabase must equal $requiredConfirmation."
}
if ($systemDatabases -contains $DatabaseName -or
    $DatabaseName -notmatch '^MesIngest_Scale_[A-Za-z0-9_]{1,64}$' -or
    [string]::Equals($DatabaseName, 'MesIngest_V2', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unsafe scale database name '$DatabaseName'. Use a new $databasePrefix name."
}
if ($RepresentativeHistoryRounds -gt 0 -and $ProfileDays -ne 0) {
    throw 'RepresentativeHistoryRounds is available only with ProfileDays 0.'
}
if ($RepresentativeHistoryRounds -gt 0 -and [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    throw 'Representative history evidence requires BaselineEvidencePath from a passing empty run.'
}
if ($RepresentativeHistoryRounds -eq 0 -and -not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    throw 'BaselineEvidencePath is valid only for representative history evidence.'
}
if ($ProfileDays -in @(7, 30) -and
    $ConfirmFullScaleEscalation -cne 'MESINGEST_FULL_SCALE_ESCALATION') {
    throw 'ProfileDays 7/30 requires ConfirmFullScaleEscalation=MESINGEST_FULL_SCALE_ESCALATION.'
}

$profiles = @{
    0 = [ordered]@{ historyDays = 0; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
    7 = [ordered]@{ historyDays = 7; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
    30 = [ordered]@{ historyDays = 30; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
}
$profile = $profiles[$ProfileDays]
$canonicalScaleProfile = $SeriesCount -eq 600 -and
    $ObservationsPerRound -eq 600 -and
    $RoundIntervalSeconds -eq 14
$historyRoundCount = if ($RepresentativeHistoryRounds -gt 0) {
    $RepresentativeHistoryRounds
} elseif ($ProfileDays -eq 0) { 0L } else {
    [long][Math]::Floor(($ProfileDays * 86400.0) / $RoundIntervalSeconds)
}
$historyObservationCount = $historyRoundCount * [long]$ObservationsPerRound
$expectedTotalObservationCount = $historyObservationCount + [long]$SeriesCount

if ([string]::IsNullOrWhiteSpace($ServiceRoot)) {
    $ServiceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'service'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'scale-evidence-runs'
}

Add-Type -AssemblyName System.Data
Add-Type -AssemblyName System.Net.Http

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream] $Stream)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try { return Get-StreamSha256 $stream } finally { $stream.Dispose() }
}

function Get-Sha256String {
    param([Parameter(Mandatory = $true)][string] $Value)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-NearestRankPercentile {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]] $Values,
        [Parameter(Mandatory = $true)][ValidateRange(0.0, 1.0)][double] $Percentile
    )
    if ($Values.Count -eq 0) { throw 'A percentile requires at least one sample.' }
    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling($Percentile * $sorted.Count)
    $index = [Math]::Max(0, [int]$rank - 1)
    return [double]$sorted[$index]
}

function Read-XEventEnvelope {
    param([Parameter(Mandatory = $true)][string] $XmlText)
    [xml]$eventXml = $XmlText
    $values = @{}
    foreach ($data in @($eventXml.SelectNodes('/event/data'))) {
        $valueNode = $data.SelectSingleNode('value')
        if ($null -ne $valueNode) {
            $values[[string]$data.name] = if ([string]$data.name -eq 'showplan_xml') {
                [string]$valueNode.InnerXml
            } else {
                [string]$valueNode.InnerText
            }
        }
    }
    return [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Parse(
            [string]$eventXml.event.timestamp,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        Values = $values
    }
}

function Get-XmlInt64Attribute {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement] $Node,
        [Parameter(Mandatory = $true)][string] $Name
    )
    $value = 0L
    if ([long]::TryParse(
        $Node.GetAttribute($Name),
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)) {
        return $value
    }
    return 0L
}

function Read-ShowPlanRuntimeIo {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $PlanXml)
    if ([string]::IsNullOrWhiteSpace($PlanXml)) { return @() }
    [xml]$plan = $PlanXml
    $items = New-Object System.Collections.ArrayList
    foreach ($relOp in @($plan.SelectNodes("//*[local-name()='RelOp']"))) {
        $object = $relOp.SelectSingleNode(".//*[local-name()='Object']")
        foreach ($counter in @($relOp.SelectNodes(".//*[local-name()='RunTimeCountersPerThread']"))) {
            if ($null -eq $object) { continue }
            [void]$items.Add([pscustomobject][ordered]@{
                database = $object.GetAttribute('Database'); schema = $object.GetAttribute('Schema')
                table = $object.GetAttribute('Table'); index = $object.GetAttribute('Index')
                physicalOperation = $relOp.GetAttribute('PhysicalOp'); thread = $counter.GetAttribute('Thread')
                actualRows = Get-XmlInt64Attribute $counter 'ActualRows'
                actualScans = Get-XmlInt64Attribute $counter 'ActualScans'
                actualLogicalReadsPresent = $counter.HasAttribute('ActualLogicalReads')
                actualLogicalReads = Get-XmlInt64Attribute $counter 'ActualLogicalReads'
                actualPhysicalReads = Get-XmlInt64Attribute $counter 'ActualPhysicalReads'
                actualReadAheads = Get-XmlInt64Attribute $counter 'ActualReadAheads'
                actualElapsedMs = Get-XmlInt64Attribute $counter 'ActualElapsedms'
                actualCpuMs = Get-XmlInt64Attribute $counter 'ActualCPUms'
            })
        }
    }
    return @($items)
}

function Get-TotalActualLogicalReads {
    param([AllowEmptyCollection()][object[]] $RuntimeIo)
    $total = 0L
    foreach ($item in @($RuntimeIo)) {
        $total += [long]$item.actualLogicalReads
    }
    return $total
}

function Get-TotalActualScans {
    param([AllowEmptyCollection()][object[]] $RuntimeIo)
    $total = 0L
    foreach ($item in @($RuntimeIo)) {
        $total += [long]$item.actualScans
    }
    return $total
}

function Convert-DataTableRows {
    param([Parameter(Mandatory = $true)][Data.DataTable] $Table)
    $rows = New-Object System.Collections.ArrayList
    foreach ($row in $Table.Rows) {
        $values = [ordered]@{}
        foreach ($column in $Table.Columns) {
            $value = $row[$column.ColumnName]
            $values[$column.ColumnName] = if ($value -is [DBNull]) { $null } else { $value }
        }
        [void]$rows.Add([pscustomobject]$values)
    }
    return @($rows)
}

function New-SqlConnection {
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $connection = [Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $CommandText,
        [hashtable] $Parameters = @{},
        [int] $CommandTimeout = 0
    )
    $connection = New-SqlConnection $ConnectionString
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout = $CommandTimeout
            $command.CommandText = $CommandText
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }
            return $command.ExecuteNonQuery()
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Invoke-SqlTable {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $CommandText,
        [hashtable] $Parameters = @{},
        [int] $CommandTimeout = 120
    )
    $connection = New-SqlConnection $ConnectionString
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout = $CommandTimeout
            $command.CommandText = $CommandText
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }
            $table = [Data.DataTable]::new()
            $reader = $command.ExecuteReader()
            try { $table.Load($reader) } finally { $reader.Dispose() }
            return @(Convert-DataTableRows $table)
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-EvidenceHost {
    param(
        [Parameter(Mandatory = $true)][string] $DatabaseConnectionString,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $ApplicationName
    )
    $hostExe = Join-Path $ServiceRoot 'MesIngest.Host.exe'
    $hostDll = Join-Path $ServiceRoot 'MesIngest.Host.dll'
    $start = [Diagnostics.ProcessStartInfo]::new()
    if (Test-Path -LiteralPath $hostExe -PathType Leaf) {
        $start.FileName = $hostExe
        $start.WorkingDirectory = $ServiceRoot
    } elseif (Test-Path -LiteralPath $hostDll -PathType Leaf) {
        $start.FileName = 'dotnet'
        $start.Arguments = '"' + $hostDll.Replace('"', '\"') + '"'
        $start.WorkingDirectory = $ServiceRoot
    } else {
        throw "Packaged Host not found under ServiceRoot: $ServiceRoot"
    }
    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($DatabaseConnectionString)
    $builder['Application Name'] = $ApplicationName
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Development'
    $start.EnvironmentVariables['MesIngest__Urls'] = $baseUrl
    $start.EnvironmentVariables['MesIngest__SnapshotSource'] = 'None'
    $start.EnvironmentVariables['MesIngest__NewSqlServerConnectionString'] = $builder.ConnectionString
    $start.EnvironmentVariables['MesIngest__SharedSecret'] = $Secret
    $start.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] = 'false'
    $start.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] = 'false'
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Packaged Host did not start.' }

    $client = [Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(2)
        $client.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Secret)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($process.HasExited) { throw "Packaged Host exited during startup with code $($process.ExitCode)." }
            try {
                $response = $client.GetAsync($baseUrl + '/api/v2/contract').GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) {
                    return [pscustomobject]@{ Process = $process; BaseUrl = $baseUrl }
                }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        throw 'Packaged Host did not expose /api/v2/contract within 45 seconds.'
    } catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
        $process.Dispose()
        throw
    } finally { $client.Dispose() }
}

function Stop-EvidenceHost {
    param([AllowNull()][object] $HostRun)
    if ($null -eq $HostRun) { return }
    $process = $HostRun.Process
    try {
        if (-not $process.HasExited) {
            $process.Kill()
            [void]$process.WaitForExit(10000)
        }
    } finally { $process.Dispose() }
}

function Invoke-HostGet {
    param(
        [Parameter(Mandatory = $true)][object] $HostRun,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Path
    )
    $client = [Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
        $client.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Secret)
        $started = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $response = $client.GetAsync($HostRun.BaseUrl + $Path).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $timer.Stop()
        $completed = [DateTimeOffset]::UtcNow
        if (-not $response.IsSuccessStatusCode) {
            throw "$Name returned HTTP $([int]$response.StatusCode)."
        }
        return [pscustomobject][ordered]@{
            name = $Name; path = $Path; startedAt = $started.ToString('o'); completedAt = $completed.ToString('o')
            statusCode = [int]$response.StatusCode; latencyMs = $timer.Elapsed.TotalMilliseconds
            responseBytes = [Text.Encoding]::UTF8.GetByteCount($body); body = $body
        }
    } finally { $client.Dispose() }
}

function Get-EvidenceGateFailures {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $QueryEvidence,
        [int] $ActualPlanCount,
        [int] $StatementMetricCount,
        [Parameter(Mandatory = $true)][object] $RowCounts,
        [bool] $Tier1Satisfied,
        [AllowEmptyString()][string] $SourceCommit,
        [bool] $CanonicalScaleProfile,
        [string] $QuerySurface = 'All'
    )
    $failures = New-Object System.Collections.ArrayList
    if ($ActualPlanCount -eq 0) { [void]$failures.Add('MISSING_ACTUAL_PLAN') }
    if ($StatementMetricCount -eq 0) { [void]$failures.Add('MISSING_STATEMENT_METRICS') }
    $incompleteSurfaces = @($QueryEvidence | Where-Object { $_.statementCount -eq 0 -or $_.actualPlanCount -eq 0 })
    if ($incompleteSurfaces.Count -gt 0) {
        [void]$failures.Add('MISSING_QUERY_SURFACE_EVIDENCE:' + (($incompleteSurfaces | Select-Object -ExpandProperty name) -join ','))
    }
    if ([long]$RowCounts.series_count -eq 0 -or [long]$RowCounts.raw_observations -eq 0) {
        [void]$failures.Add('EMPTY_SCALE_DATABASE')
    }
    if (-not $Tier1Satisfied) { [void]$failures.Add('SQL_TIER1_SKIPPED_OR_UNPROVEN') }
    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or $SourceCommit -eq 'unknown') {
        [void]$failures.Add('MISSING_BUILD_IDENTITY')
    }
    if (-not $CanonicalScaleProfile) { [void]$failures.Add('NON_CANONICAL_SCALE_PROFILE') }
    if ($QuerySurface -eq 'DemandSeries') {
        $demandSeriesSurfaces = @($QueryEvidence | Where-Object { $_.name -like 'DemandSeries*' })
        if ($demandSeriesSurfaces.Count -eq 0) {
            [void]$failures.Add('MISSING_DEMAND_SERIES_EVIDENCE')
        } else {
            if (@($demandSeriesSurfaces | Where-Object {
                    [long]$_.rawObservationPlanOperators -ne 0 -or
                    [long]$_.rawObservationLogicalReads -ne 0 }).Count -gt 0) {
                [void]$failures.Add('DEMAND_SERIES_RAW_HISTORY_READ')
            }
            if (@($demandSeriesSurfaces | Where-Object { -not [bool]$_.runtimeIoComplete }).Count -gt 0) {
                [void]$failures.Add('DEMAND_SERIES_RUNTIME_IO_INCOMPLETE')
            }
            if (@($demandSeriesSurfaces | Where-Object { -not [bool]$_.memoryGrantEvidenceComplete }).Count -gt 0) {
                [void]$failures.Add('DEMAND_SERIES_MEMORY_GRANT_EVIDENCE_INCOMPLETE')
            }
            if ([long](($demandSeriesSurfaces | Measure-Object -Property spillCount -Sum).Sum) -ne 0) {
                [void]$failures.Add('DEMAND_SERIES_SPILL')
            }
            if ([long](($demandSeriesSurfaces | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum) -gt 8192) {
                [void]$failures.Add('DEMAND_SERIES_ABNORMAL_MEMORY_GRANT')
            }
        }
    }
    return @($failures)
}

if (-not [string]::IsNullOrWhiteSpace($ValidatePercentileFixture)) {
    $percentileValues = @($ValidatePercentileFixture.Split(',') | ForEach-Object {
        [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture)
    })
    Write-Output (
        'MESINGEST_PERCENTILE_FIXTURE: p50={0} p95={1} p99={2}' -f
        (Get-NearestRankPercentile $percentileValues 0.50).ToString([Globalization.CultureInfo]::InvariantCulture),
        (Get-NearestRankPercentile $percentileValues 0.95).ToString([Globalization.CultureInfo]::InvariantCulture),
        (Get-NearestRankPercentile $percentileValues 0.99).ToString([Globalization.CultureInfo]::InvariantCulture))
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($ValidateShowPlanFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateShowPlanFixturePath -PathType Leaf)) {
        throw "ShowPlan fixture not found: $ValidateShowPlanFixturePath"
    }
    $runtimeIo = @(Read-ShowPlanRuntimeIo (Get-Content -Raw -LiteralPath $ValidateShowPlanFixturePath))
    Write-Output (
        'MESINGEST_SHOWPLAN_FIXTURE: operators={0} scans={1} logicalReads={2}' -f
        $runtimeIo.Count,
        (Get-TotalActualScans $runtimeIo),
        (Get-TotalActualLogicalReads $runtimeIo))
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($ValidateEvidenceFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateEvidenceFixturePath -PathType Leaf)) {
        throw "Evidence fixture not found: $ValidateEvidenceFixturePath"
    }
    $fixture = Get-Content -Raw -LiteralPath $ValidateEvidenceFixturePath | ConvertFrom-Json
    $fixtureFailures = @(Get-EvidenceGateFailures `
        -QueryEvidence @($fixture.queries) `
        -ActualPlanCount @($fixture.actualPlans).Count `
        -StatementMetricCount @($fixture.statementMetrics).Count `
        -RowCounts $fixture.data `
        -Tier1Satisfied ([bool]$fixture.tests.satisfied) `
        -SourceCommit ([string]$fixture.build.sourceCommit) `
        -CanonicalScaleProfile ([bool]$fixture.profile.canonical) `
        -QuerySurface $QuerySurface)
    if ($fixtureFailures.Count -gt 0) {
        throw "Scale evidence fixture failed: $($fixtureFailures -join ', ')"
    }
    Write-Output 'MESINGEST_SCALE_EVIDENCE_FIXTURE: passed=True'
    exit 0
}

$masterConnectionString = [Environment]::GetEnvironmentVariable($SqlConnectionStringEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($masterConnectionString)) {
    throw "Set $SqlConnectionStringEnvironmentVariable to an approved real SQL Server master connection."
}
$masterBuilder = [Data.SqlClient.SqlConnectionStringBuilder]::new($masterConnectionString)
if ($masterBuilder.DataSource.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Scale evidence requires a real SQL Server; LocalDB is rejected.'
}
if (-not [string]::Equals($masterBuilder.InitialCatalog, 'master', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Scale evidence connection must explicitly target master.'
}
$masterBuilder['Connect Timeout'] = 5
$masterBuilder['Application Name'] = 'MesIngest.ScaleEvidence.Control'
$masterConnectionString = $masterBuilder.ConnectionString

$serverIdentity = @(Invoke-SqlTable $masterConnectionString @"
SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
       CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS product_major,
       CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engine_edition,
       CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')) AS can_create_database,
       CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION')) AS can_create_xevent,
       CONVERT(int, (SELECT value_in_use FROM sys.configurations WHERE name = N'max server memory (MB)')) AS max_server_memory_mb,
       CONVERT(nvarchar(4000), SERVERPROPERTY('ErrorLogFileName')) AS error_log_path;
"@) | Select-Object -First 1
if ($null -eq $serverIdentity -or $serverIdentity.can_create_database -ne 1 -or $serverIdentity.can_create_xevent -ne 1) {
    throw 'Scale evidence requires CREATE ANY DATABASE and ALTER ANY EVENT SESSION on a real SQL Server.'
}
$existing = @(Invoke-SqlTable $masterConnectionString 'SELECT name FROM sys.databases WHERE name = @databaseName;' @{ '@databaseName' = $DatabaseName })
if ($existing.Count -ne 0) {
    throw "Refusing to reuse existing or unverifiable database '$DatabaseName'."
}

$startedAt = [DateTimeOffset]::UtcNow
$runId = 'scale-{0}-{1}' -f $startedAt.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $runId
if (Test-Path -LiteralPath $runDirectory) { throw "Refusing to overwrite scale evidence run: $runDirectory" }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$ownerProperty = 'MesIngestScaleEvidenceRunId'
$databaseCreated = $false
$databaseOwned = $false
$hostRun = $null
$xeventSession = ('MesIngestScale_' + $runId.Replace('-', '_'))
$xeventStarted = $false
$xelBase = ''
$secretBytes = [byte[]]::new(32)
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
$secret = [Convert]::ToBase64String($secretBytes)

$databaseBuilder = [Data.SqlClient.SqlConnectionStringBuilder]::new($masterConnectionString)
$databaseBuilder['Initial Catalog'] = $DatabaseName
$databaseConnectionString = $databaseBuilder.ConnectionString

try {
    [void](Invoke-SqlNonQuery $masterConnectionString "CREATE DATABASE [$DatabaseName];")
    $databaseCreated = $true
    [void](Invoke-SqlNonQuery $masterConnectionString "ALTER DATABASE [$DatabaseName] SET RECOVERY SIMPLE;")

    # The durable owner marker precedes schema bootstrap so any later failure can still clean up
    # only this exact run's new database without trusting an in-memory created flag.
    [void](Invoke-SqlNonQuery $databaseConnectionString @"
EXEC sys.sp_addextendedproperty @name = N'$ownerProperty', @value = @runId;
"@ @{ '@runId' = $runId })
    $databaseOwned = $true

    # Bootstrap only through the production Host so schema/contract identity cannot drift.
    $hostRun = Start-EvidenceHost $databaseConnectionString $secret "MesIngest.ScaleEvidence.$runId.bootstrap"
    Stop-EvidenceHost $hostRun
    $hostRun = $null

    $schemaIdentity = @(Invoke-SqlTable $databaseConnectionString @"
SELECT SchemaVersion AS schemaVersion, ContractVersion AS contractVersion,
       CONVERT(nvarchar(36), HistoryEpoch) AS historyEpoch
FROM mesingest.SchemaInfo WHERE Id = 1;
"@) | Select-Object -First 1
    if ($null -eq $schemaIdentity) { throw 'Production Host did not bootstrap SchemaInfo.' }
    $databaseConfiguration = @(Invoke-SqlTable $masterConnectionString @"
SELECT compatibility_level AS compatibilityLevel, recovery_model_desc AS recoveryModel
FROM sys.databases WHERE name = @databaseName;
"@ @{ '@databaseName' = $DatabaseName }) | Select-Object -First 1

    $hostSessionId = [string](@(Invoke-SqlTable $databaseConnectionString @"
SELECT TOP (1) HostSessionId FROM mesingest.HostSessions ORDER BY StartedAt DESC, HostSessionId DESC;
"@) | Select-Object -First 1).HostSessionId
    $baselineSequence = $historyRoundCount + 1

    # Create deterministic poll/commit history in bounded batches. Explicit identity values put the
    # baseline current projection after every historical round even though domain rows reference it.
    for ($roundStart = 1L; $roundStart -le $historyRoundCount; $roundStart += $RoundBatchSize) {
        $roundCount = [Math]::Min([long]$RoundBatchSize, $historyRoundCount - $roundStart + 1)
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
SET IDENTITY_INSERT mesingest.PollTraces ON;
;WITH n AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.PollTraces
    (PollTraceId, PollTraceSequence, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
SELECT N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       @roundStart + n, N'MES_TASK_UNION/scale-v1', N'SUCCESS',
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds), @anchorUtc),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds) + 1, @anchorUtc),
       @observationsPerRound, REPLICATE('0', 64)
FROM n;
SET IDENTITY_INSERT mesingest.PollTraces OFF;

SET IDENTITY_INSERT mesingest.ProjectionCommits ON;
;WITH n AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.ProjectionCommits
    (ProjectionCommitId, ProjectionSequence, PollTraceId, CommittedAt, HostSessionId,
     RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority, CatalogRevision, HistoryEpoch)
SELECT N'scale-commit-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       @roundStart + n,
       N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds), @anchorUtc),
       @hostSessionId, N'NORMAL', N'NORMAL', 1, 0,
       CONVERT(uniqueidentifier, @historyEpoch)
FROM n;
SET IDENTITY_INSERT mesingest.ProjectionCommits OFF;
"@ @{
                '@roundStart' = $roundStart; '@roundCount' = $roundCount
                '@historyRoundCount' = $historyRoundCount; '@roundSeconds' = $RoundIntervalSeconds
                '@anchorUtc' = $AnchorUtc; '@observationsPerRound' = $ObservationsPerRound
                '@hostSessionId' = $hostSessionId; '@historyEpoch' = [string]$schemaIdentity.historyEpoch
            })
    }

    [void](Invoke-SqlNonQuery $databaseConnectionString @"
SET IDENTITY_INSERT mesingest.PollTraces ON;
INSERT mesingest.PollTraces
    (PollTraceId, PollTraceSequence, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
VALUES (N'scale-baseline-poll', @baselineSequence, N'MES_TASK_UNION/scale-v1', N'SUCCESS',
        DATEADD(second, -1, @anchorUtc), @anchorUtc, @seriesCount, REPLICATE('1', 64));
SET IDENTITY_INSERT mesingest.PollTraces OFF;
SET IDENTITY_INSERT mesingest.ProjectionCommits ON;
INSERT mesingest.ProjectionCommits
    (ProjectionCommitId, ProjectionSequence, PollTraceId, CommittedAt, HostSessionId,
     RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority, CatalogRevision, HistoryEpoch)
VALUES (N'scale-baseline-commit', @baselineSequence, N'scale-baseline-poll', @anchorUtc,
        @hostSessionId, N'NORMAL', N'NORMAL', 1, 1, CONVERT(uniqueidentifier, @historyEpoch));
SET IDENTITY_INSERT mesingest.ProjectionCommits OFF;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeries
    (SeriesId, KeyToken, WorkType, Sublot, Lifecycle, CurrentPresence, StartedAt, ArchivedAt,
     CreatedPollTraceId, CreatedProjectionCommitId, LatestProjectionCommitId, CurrentDemandId, LastSeriesSequence)
SELECT N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       LOWER(CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT(N'SUBLOT-', n, N'|WT-', n % 4)), 2)),
       N'WT-' + CONVERT(nvarchar(10), n % 4), N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       CASE WHEN n < @activeCount THEN N'TRACKING' ELSE N'ARCHIVED' END,
       CASE WHEN n < @activeCount THEN N'VISIBLE' ELSE N'GONE' END,
       DATEADD(minute, -n, @anchorUtc), CASE WHEN n < @activeCount THEN NULL ELSE @anchorUtc END,
       N'scale-baseline-poll', N'scale-baseline-commit', N'scale-baseline-commit', NULL,
       CASE WHEN n < @errorCount THEN 2 ELSE 1 END
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.TransportDemands
    (DemandId, SeriesId, Generation, PredecessorDemandId, Status, CreatedAt, DemandLastSeenAt,
     GoneConfirmedAt, CreatedPollTraceId, CreatedProjectionCommitId, LatestProjectionCommitId,
     LatestObservationProjectionCommitId, DemandRevision, ValueObservedAt, Area, Eqp, Step, MesSourceDate, Package)
SELECT N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), 1, NULL,
       CASE WHEN n < @activeCount THEN N'VISIBLE' ELSE N'GONE' END,
       DATEADD(minute, -n, @anchorUtc), @anchorUtc,
       CASE WHEN n < @activeCount THEN NULL ELSE @anchorUtc END,
       N'scale-baseline-poll', N'scale-baseline-commit', N'scale-baseline-commit',
       N'scale-baseline-commit', 1, @anchorUtc,
       CASE WHEN n < @errorCount THEN N'INVALID AREA' ELSE N'A' + CONVERT(nvarchar(10), (n % 9) + 1) + N'-' + CONVERT(nvarchar(10), (n % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), n % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), n % 12), @anchorUtc,
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6)
FROM n;

UPDATE s SET CurrentDemandId = N'scale-demand-' + RIGHT(REPLICATE('0', 6) + SUBSTRING(s.SeriesId, LEN(s.SeriesId) - 5, 6), 6)
FROM mesingest.DemandSeries s;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandRawObservations
    (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId, WorkType, Sublot,
     Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
SELECT N'scale-baseline-poll', CONVERT(int, n), N'scale-baseline-commit',
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'WT-' + CONVERT(nvarchar(10), n % 4), N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       CASE WHEN n < @errorCount THEN N'INVALID AREA' ELSE N'A' + CONVERT(nvarchar(10), (n % 9) + 1) + N'-' + CONVERT(nvarchar(10), (n % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), n % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), n % 12), @anchorUtc,
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), CONVERT(nvarchar(33), @anchorUtc, 127)
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesEvents
    (EventId, SeriesId, SeriesSequence, EventType, OccurredAt, SubjectKind, SubjectId,
     PollTraceId, ProjectionCommitId, PayloadVersion, Payload)
SELECT N'scale-start-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       1, N'SERIES_STARTED', DATEADD(minute, -n, @anchorUtc), N'SERIES',
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-baseline-poll', N'scale-baseline-commit', 1, N'{}'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesEvents
    (EventId, SeriesId, SeriesSequence, EventType, OccurredAt, SubjectKind, SubjectId,
     PollTraceId, ProjectionCommitId, PayloadVersion, Payload)
SELECT N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       2, N'ERROR_PERIOD_STARTED', @anchorUtc, N'DEMAND',
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-baseline-poll', N'scale-baseline-commit', 1, N'{}'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesErrorPeriods
    (PeriodId, SeriesId, ErrorCode, Category, Severity, Target, SubjectKind, StartReason,
     StartedAt, EndedAt, EndReason, OpenedEventId, ClosedEventId)
SELECT N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID_MES_FIELD_FORMAT', N'DATA_FORMAT', N'ERROR', N'AREA', N'DEMAND', N'OBSERVED',
       @anchorUtc, NULL, NULL,
       N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), NULL
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.SeriesErrorPeriodEvidence
    (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId, ProjectionCommitId,
     DemandId, ObservedValue, ExpectedRule)
SELECT N'scale-evidence-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'FIELD_VALIDATION', @anchorUtc, N'scale-baseline-poll', N'scale-baseline-commit',
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID AREA', N'AREA must match the MES AREA domain format.'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesCurrentConditions
    (SeriesId, ErrorCode, Target, SubjectKind, PeriodId, LatestEvidenceId)
SELECT N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID_MES_FIELD_FORMAT', N'AREA', N'DEMAND',
       N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-evidence-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6)
FROM n;

INSERT mesingest.CatalogItems
    (DemandId, SeriesId, WorkType, Sublot, Generation, DemandRevision, CreatedAt, ValueObservedAt,
     ValuePollTraceId, ValueProjectionCommitId, Area, Eqp, Step, MesSourceDate, Package)
SELECT d.DemandId, d.SeriesId, s.WorkType, s.Sublot, d.Generation, d.DemandRevision,
       d.CreatedAt, d.ValueObservedAt, N'scale-baseline-poll', N'scale-baseline-commit',
       d.Area, d.Eqp, d.Step, d.MesSourceDate, d.Package
FROM mesingest.TransportDemands d
JOIN mesingest.DemandSeries s ON s.SeriesId = d.SeriesId
WHERE s.Lifecycle = N'TRACKING'
  AND NOT EXISTS (SELECT 1 FROM mesingest.DemandSeriesCurrentConditions c WHERE c.SeriesId = s.SeriesId);
UPDATE mesingest.CatalogState SET CatalogRevision = 1, ProjectionCommitId = N'scale-baseline-commit' WHERE Id = 1;
INSERT mesingest.ProjectionCommitUnassignedObservationFacts (ProjectionCommitId, ObservationCount, ContentDigest)
VALUES (N'scale-baseline-commit', 0, NULL);
"@ @{
            '@baselineSequence' = $baselineSequence; '@anchorUtc' = $AnchorUtc
            '@hostSessionId' = $hostSessionId; '@seriesCount' = $SeriesCount
            '@historyEpoch' = [string]$schemaIdentity.historyEpoch
            '@activeCount' = [int][Math]::Round($SeriesCount * 0.70, 0, [MidpointRounding]::AwayFromZero)
            '@errorCount' = [Math]::Max(1, [int][Math]::Round($SeriesCount * 0.10, 0, [MidpointRounding]::AwayFromZero))
        })

    # Inflate history after the coherent current graph exists; every batch is independently committed.
    for ($roundStart = 1L; $roundStart -le $historyRoundCount; $roundStart += $RoundBatchSize) {
        $roundCount = [Math]::Min([long]$RoundBatchSize, $historyRoundCount - $roundStart + 1)
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
;WITH rounds AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), observations AS
(
    SELECT TOP (CONVERT(int, @observationsPerRound)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandRawObservations
    (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId, WorkType, Sublot,
     Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
SELECT N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + r.n), 10),
       CONVERT(int, o.n),
       N'scale-commit-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + r.n), 10),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       N'WT-' + CONVERT(nvarchar(10), (o.n % @seriesCount) % 4),
       N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       CASE WHEN (o.n % @seriesCount) < @errorCount THEN N'INVALID AREA'
            ELSE N'A' + CONVERT(nvarchar(10), ((o.n % @seriesCount) % 9) + 1) + N'-' + CONVERT(nvarchar(10), ((o.n % @seriesCount) % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), (o.n % @seriesCount) % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), (o.n % @seriesCount) % 12),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + r.n) + 1) * @roundSeconds), @anchorUtc),
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       CONVERT(nvarchar(33), DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + r.n) + 1) * @roundSeconds), @anchorUtc), 127)
FROM rounds r CROSS JOIN observations o;
"@ @{
                '@roundStart' = $roundStart; '@roundCount' = $roundCount
                '@observationsPerRound' = $ObservationsPerRound; '@seriesCount' = $SeriesCount
                '@errorCount' = [Math]::Max(1, [int][Math]::Round($SeriesCount * 0.10))
                '@historyRoundCount' = $historyRoundCount; '@roundSeconds' = $RoundIntervalSeconds; '@anchorUtc' = $AnchorUtc
            })
    }

    [void](Invoke-SqlNonQuery $databaseConnectionString 'EXEC sys.sp_updatestats;' @{} 0)
    $rowCounts = @(Invoke-SqlTable $databaseConnectionString @"
SELECT (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations) AS raw_observations,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries) AS series_count,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE Lifecycle = N'TRACKING') AS active_series,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE Lifecycle = N'ARCHIVED') AS archived_series,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesErrorPeriods) AS error_periods;
"@) | Select-Object -First 1
    if ($null -eq $rowCounts -or [long]$rowCounts.raw_observations -ne $expectedTotalObservationCount -or [long]$rowCounts.series_count -eq 0) {
        throw 'EMPTY_SCALE_DATABASE: generated row counts do not match the selected profile.'
    }

    $errorLogDirectory = Split-Path -Parent ([string]$serverIdentity.error_log_path)
    $xelBase = Join-Path $errorLogDirectory ($xeventSession + '.xel')
    $escapedXel = $xelBase.Replace("'", "''")
    $escapedSession = $xeventSession.Replace(']', ']]')
    $appName = "MesIngest.ScaleEvidence.$runId.workload"
    $escapedApp = $appName.Replace("'", "''")
    [void](Invoke-SqlNonQuery $masterConnectionString @"
CREATE EVENT SESSION [$escapedSession] ON SERVER
ADD EVENT sqlserver.sql_statement_completed
(
    ACTION(sqlserver.client_app_name, sqlserver.database_id, sqlserver.session_id, sqlserver.sql_text)
    WHERE (sqlserver.client_app_name = N'$escapedApp')
),
ADD EVENT sqlserver.query_post_execution_showplan
(
    ACTION(sqlserver.client_app_name, sqlserver.database_id, sqlserver.session_id, sqlserver.sql_text)
    WHERE (sqlserver.client_app_name = N'$escapedApp')
)
ADD TARGET package0.event_file(SET filename = N'$escapedXel', max_file_size = 1024, max_rollover_files = 4)
WITH (MAX_MEMORY = 16384 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
      MAX_DISPATCH_LATENCY = 1 SECONDS, TRACK_CAUSALITY = ON, STARTUP_STATE = OFF);
ALTER EVENT SESSION [$escapedSession] ON SERVER STATE = START;
"@)
    $xeventStarted = $true

    $hostRun = Start-EvidenceHost $databaseConnectionString $secret $appName
    $surfaceCatalog = @(
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesDefault'; path = '/api/v2/demand-series?pageSize=100' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesVisible'; path = '/api/v2/demand-series?pageSize=100&presence=VISIBLE' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesGone'; path = '/api/v2/demand-series?pageSize=100&presence=GONE' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesArchived'; path = '/api/v2/demand-series?pageSize=100&lifecycle=ARCHIVED' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesArea'; path = '/api/v2/demand-series?pageSize=100&area=A1-1' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesWorkType'; path = '/api/v2/demand-series?pageSize=100&workType=WT-1' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesSublot'; path = '/api/v2/demand-series?pageSize=100&sublot=SUBLOT-0001' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesSeriesId'; path = '/api/v2/demand-series?pageSize=100&seriesId=scale-series-000001' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesDemandId'; path = '/api/v2/demand-series?pageSize=100&demandId=scale-demand-000001' },
        [ordered]@{ scope = 'ExternallyReadableDemandCatalog'; kind = 'http'; name = 'ExternallyReadableDemandCatalog'; path = '/api/v2/externally-readable-demand-catalog' },
        [ordered]@{ scope = 'CurrentIngestAttention'; kind = 'http'; name = 'CurrentIngestAttention'; path = '/api/v2/current-ingest-attention?pageSize=100' },
        [ordered]@{ scope = 'Overview'; kind = 'http'; name = 'Overview'; path = '/api/v2/watch-overview' },
        [ordered]@{ scope = 'ReadabilityAudit'; kind = 'http'; name = 'ReadabilityAudit'; path = '/api/v2/readability-audit?pageSize=100' },
        [ordered]@{ scope = 'ErrorSearch'; kind = 'http'; name = 'ErrorSearch'; path = '/api/v2/error-search?window=ALL_HISTORY&pageSize=100' },
        [ordered]@{ scope = 'RawEvidence'; kind = 'raw-evidence'; name = 'RawEvidence'; path = '/raw-observations' }
    )
    $selectedCatalog = if ($QuerySurface -eq 'All') {
        @($surfaceCatalog)
    } else {
        @($surfaceCatalog | Where-Object { $_.scope -eq $QuerySurface })
    }
    $surfaces = @($selectedCatalog | Where-Object { $_.kind -eq 'http' })
    $measurements = New-Object System.Collections.ArrayList
    foreach ($surface in $surfaces) {
        for ($i = 0; $i -lt ($WarmupCount + $MeasurementCount); $i++) {
            $sample = Invoke-HostGet $hostRun $secret $surface.name $surface.path
            if ($i -ge $WarmupCount) { [void]$measurements.Add($sample) }
        }
    }

    $measureRawEvidence = @($selectedCatalog | Where-Object { $_.kind -eq 'raw-evidence' }).Count -gt 0
    if ($measureRawEvidence) {
        $errorList = Invoke-HostGet $hostRun $secret 'ErrorSearchDiscovery' '/api/v2/error-search?window=ALL_HISTORY&pageSize=100'
        $errorJson = $errorList.body | ConvertFrom-Json
        if (@($errorJson.items).Count -eq 0) { throw 'ErrorSearch scale seed produced no error item.' }
        $seriesId = [string]$errorJson.items[0].seriesId
        $snapshotReference = [Uri]::EscapeDataString([string]$errorJson.snapshotReference)
        $detail = Invoke-HostGet $hostRun $secret 'ErrorSearchDetail' ("/api/v2/error-search/$seriesId`?snapshot=$snapshotReference")
        $detailJson = $detail.body | ConvertFrom-Json
        $evidenceId = [string]$detailJson.periods[0].evidence[0].evidenceId
        $rawPath = "/api/v2/error-search/$seriesId/evidence/$evidenceId/raw-observations?fields=area&maxItems=20&snapshot=$snapshotReference"
        for ($i = 0; $i -lt ($WarmupCount + $MeasurementCount); $i++) {
            $sample = Invoke-HostGet $hostRun $secret 'RawEvidence' $rawPath
            if ($i -ge $WarmupCount) { [void]$measurements.Add($sample) }
        }
    }
    Stop-EvidenceHost $hostRun
    $hostRun = $null
    Start-Sleep -Seconds 2
    [void](Invoke-SqlNonQuery $masterConnectionString "ALTER EVENT SESSION [$escapedSession] ON SERVER STATE = STOP;")
    $xeventStarted = $false

    $xelPattern = $xelBase.Substring(0, $xelBase.Length - 4) + '*.xel'
    $events = @(Invoke-SqlTable $masterConnectionString @"
SELECT object_name,
       file_name,
       CONVERT(nvarchar(max), event_data) AS event_xml
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL)
ORDER BY file_name, file_offset;
"@ @{ '@xelPattern' = $xelPattern } 300)
    $statementEvents = @($events | Where-Object { $_.object_name -eq 'sql_statement_completed' })
    $planEvents = @($events | Where-Object { $_.object_name -eq 'query_post_execution_showplan' })
    if ($planEvents.Count -eq 0) { throw 'MISSING_ACTUAL_PLAN: query_post_execution_showplan returned no workload plan.' }

    $statementMetrics = foreach ($event in $statementEvents) {
        $envelope = Read-XEventEnvelope ([string]$event.event_xml)
        $values = $envelope.Values
        [pscustomobject][ordered]@{
            timestamp = $envelope.Timestamp
            logical_reads = if ($values.ContainsKey('logical_reads')) { [long]$values['logical_reads'] } else { 0L }
            duration = if ($values.ContainsKey('duration')) { [long]$values['duration'] } else { 0L }
            cpu_time = if ($values.ContainsKey('cpu_time')) { [long]$values['cpu_time'] } else { 0L }
            spills = if ($values.ContainsKey('spills')) { [long]$values['spills'] } else { 0L }
            row_count = if ($values.ContainsKey('row_count')) { [long]$values['row_count'] } else { 0L }
            statement = if ($values.ContainsKey('statement')) { [string]$values['statement'] } else { '' }
        }
    }

    $planSummaries = foreach ($event in $planEvents) {
        $envelope = Read-XEventEnvelope ([string]$event.event_xml)
        $values = $envelope.Values
        $planText = if ($values.ContainsKey('showplan_xml')) { [string]$values['showplan_xml'] } else { '' }
        [pscustomobject][ordered]@{
            timestamp = $envelope.Timestamp
            memoryGrantCaptured = $planText.IndexOf('<MemoryGrantInfo', [StringComparison]::OrdinalIgnoreCase) -ge 0
            granted_memory_kb = if ($values.ContainsKey('granted_memory_kb')) { [long]$values['granted_memory_kb'] } else { 0L }
            requested_memory_kb = if ($values.ContainsKey('requested_memory_kb')) { [long]$values['requested_memory_kb'] } else { 0L }
            used_memory_kb = if ($values.ContainsKey('used_memory_kb')) { [long]$values['used_memory_kb'] } else { 0L }
            spillToTempDb = $planText.IndexOf('SpillToTempDb', [StringComparison]::OrdinalIgnoreCase) -ge 0
            planSha256 = Get-Sha256String $planText
            runtimeIo = @(Read-ShowPlanRuntimeIo $planText)
            planXml = $planText
        }
    }

    $queryEvidence = New-Object System.Collections.ArrayList
    $surfaceNames = @($surfaces | ForEach-Object { [string]$_.name })
    if ($measureRawEvidence) { $surfaceNames += 'RawEvidence' }
    foreach ($surfaceName in $surfaceNames) {
        $surfaceSamples = @($measurements | Where-Object { $_.name -eq $surfaceName })
        if ($surfaceSamples.Count -ne $MeasurementCount) { throw "Missing measurement samples for $surfaceName." }
        $sampleWindows = @($surfaceSamples | ForEach-Object {
            [pscustomobject]@{
                Start = [DateTimeOffset]::Parse([string]$_.startedAt).ToUniversalTime()
                End = [DateTimeOffset]::Parse([string]$_.completedAt).ToUniversalTime()
            }
        })
        $surfaceStatements = @($statementMetrics | Where-Object {
            $eventTime = $_.timestamp
            @($sampleWindows | Where-Object { $eventTime -ge $_.Start -and $eventTime -le $_.End }).Count -gt 0
        })
        $surfacePlans = @($planSummaries | Where-Object {
            $eventTime = $_.timestamp
            @($sampleWindows | Where-Object { $eventTime -ge $_.Start -and $eventTime -le $_.End }).Count -gt 0
        })
        $surfaceRuntimeIo = @($surfacePlans | ForEach-Object { @($_.runtimeIo) })
        $rawObservationRuntimeIo = @($surfaceRuntimeIo | Where-Object {
            ([string]$_.table).Trim('[', ']') -eq 'DemandRawObservations'
        })
        $rawObservationLogicalReads = Get-TotalActualLogicalReads $rawObservationRuntimeIo
        $runtimeIoCarriers = @($surfaceRuntimeIo | Where-Object {
            [string]$_.physicalOperation -match '(Scan|Seek|Lookup)'
        })
        $sortedLatency = @($surfaceSamples | ForEach-Object { [double]$_.latencyMs } | Sort-Object)
        [void]$queryEvidence.Add([pscustomobject][ordered]@{
            name = $surfaceName
            samples = $surfaceSamples.Count
            p50LatencyMs = Get-NearestRankPercentile $sortedLatency 0.50
            p95LatencyMs = Get-NearestRankPercentile $sortedLatency 0.95
            p99LatencyMs = Get-NearestRankPercentile $sortedLatency 0.99
            statisticsSource = 'sql_statement_completed plus actual-plan per-object runtime IO/TIME counters'
            actualPlanSource = 'query_post_execution_showplan'
            statementCount = $surfaceStatements.Count
            actualPlanCount = $surfacePlans.Count
            runtimeIoComplete = $runtimeIoCarriers.Count -gt 0 -and
                @($runtimeIoCarriers | Where-Object { -not [bool]$_.actualLogicalReadsPresent }).Count -eq 0
            memoryGrantEvidenceComplete = $surfacePlans.Count -gt 0 -and
                @($surfacePlans | Where-Object { -not [bool]$_.memoryGrantCaptured }).Count -eq 0
            logicalReads = [long](($surfaceStatements | Measure-Object -Property logical_reads -Sum).Sum)
            durationMicroseconds = [long](($surfaceStatements | Measure-Object -Property duration -Sum).Sum)
            cpuMicroseconds = [long](($surfaceStatements | Measure-Object -Property cpu_time -Sum).Sum)
            maxGrantedMemoryKb = [long](($surfacePlans | Measure-Object -Property granted_memory_kb -Maximum).Maximum)
            spillCount = [long](($surfaceStatements | Measure-Object -Property spills -Sum).Sum) +
                @($surfacePlans | Where-Object { $_.spillToTempDb }).Count
            rawObservationPlanOperators = $rawObservationRuntimeIo.Count
            rawObservationLogicalReads = $rawObservationLogicalReads
            planSha256 = @($surfacePlans | Select-Object -ExpandProperty planSha256 -Unique)
        })
    }

    $physicalFiles = @(Invoke-SqlTable $databaseConnectionString @"
SELECT DB_NAME() AS database_name, name AS logical_name, type_desc, physical_name,
       size * 8.0 / 1024 AS physical_size_mb,
       FILEPROPERTY(name, 'SpaceUsed') * 8.0 / 1024 AS logical_used_mb
FROM sys.database_files
ORDER BY type, file_id;
"@ @{} 300)
    $allocations = @(Invoke-SqlTable $databaseConnectionString @"
SELECT s.name AS schema_name, t.name AS table_name, i.name AS index_name,
       CASE WHEN i.index_id = 0 THEN N'HEAP' WHEN i.index_id = 1 THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END AS allocation_kind,
       p.data_compression_desc,
       SUM(ps.row_count) AS row_count,
       SUM(ps.reserved_page_count) * 8.0 / 1024 AS reserved_mb,
       SUM(ps.used_page_count) * 8.0 / 1024 AS logical_used_mb
FROM sys.dm_db_partition_stats ps
JOIN sys.partitions p ON p.partition_id = ps.partition_id
JOIN sys.tables t ON t.object_id = ps.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE s.name = N'mesingest'
GROUP BY s.name, t.name, i.name, i.index_id, p.data_compression_desc
ORDER BY t.name, i.index_id;
"@  @{} 300)
    $storage = [ordered]@{
        physicalFiles = @($physicalFiles)
        allocations = @($allocations)
        physicalDataMb = [double](($physicalFiles | Where-Object { $_.type_desc -eq 'ROWS' } | Measure-Object -Property physical_size_mb -Sum).Sum)
        ldfMb = [double](($physicalFiles | Where-Object { $_.type_desc -eq 'LOG' } | Measure-Object -Property physical_size_mb -Sum).Sum)
        logicalUsedMb = [double](($allocations | Measure-Object -Property logical_used_mb -Sum).Sum)
    }

    $attestation = $null
    $tier1 = [ordered]@{
        provided = $false; attestationSchemaVersion = $null
        failed = $null; passed = $null; sqlSkippedTests = $null; total = $null
        exitCode = $null; trxVerified = $false; buildBound = $false
        baseSatisfied = $false; satisfied = $false; diagnosticCode = 'SQL_TIER1_ATTESTATION_NOT_PROVIDED'
    }
    if (-not [string]::IsNullOrWhiteSpace($SqlTier1AttestationPath) -and (Test-Path -LiteralPath $SqlTier1AttestationPath -PathType Leaf)) {
        try {
            $attestation = Get-Content -Raw -LiteralPath $SqlTier1AttestationPath | ConvertFrom-Json
            $tier1.provided = $true
            $tier1.attestationSchemaVersion = [int]$attestation.schemaVersion
            $tier1.failed = [int]$attestation.counts.failed
            $tier1.passed = [int]$attestation.counts.passed
            $tier1.sqlSkippedTests = [int]$attestation.counts.skipped
            $tier1.total = [int]$attestation.counts.total
            $tier1.exitCode = [int]$attestation.exitCode
            $attestedTrxPath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($SqlTier1AttestationPath))) ([string]$attestation.trxFile)
            if (Test-Path -LiteralPath $attestedTrxPath -PathType Leaf) {
                [xml]$attestedTrx = Get-Content -Raw -LiteralPath $attestedTrxPath
                $counters = $attestedTrx.TestRun.ResultSummary.Counters
                $trxTotal = [int]$counters.total
                $trxExecuted = [int]$counters.executed
                $trxPassed = [int]$counters.passed
                $trxFailed = [int]$counters.failed
                $trxSkipped = $trxTotal - $trxExecuted
                $trxAssemblies = @($attestedTrx.TestRun.TestDefinitions.UnitTest | ForEach-Object {
                    [IO.Path]::GetFileName([string]$_.storage)
                } | Sort-Object -Unique)
                $tier1.trxVerified = (Get-FileSha256 $attestedTrxPath) -ceq [string]$attestation.trxSha256 -and
                    $trxTotal -eq $tier1.total -and $trxPassed -eq $tier1.passed -and
                    $trxFailed -eq $tier1.failed -and $trxSkipped -eq $tier1.sqlSkippedTests -and
                    $trxAssemblies.Count -eq 1 -and
                    [string]::Equals($trxAssemblies[0], 'MesIngest.Tests.dll', [StringComparison]::OrdinalIgnoreCase)
            }
            $tier1.baseSatisfied = $tier1.attestationSchemaVersion -ge 2 -and
                $tier1.exitCode -eq 0 -and $tier1.failed -eq 0 -and
                $tier1.sqlSkippedTests -eq 0 -and $tier1.total -ge 700 -and $tier1.trxVerified -and
                [string]::Equals(
                    [string]$attestation.commandPattern,
                    'dotnet test MesIngest.Tests --configuration Release --results-directory <path> --logger "trx;LogFileName=runtime-feedback-tier1.trx"',
                    [StringComparison]::Ordinal) -and
                [int]$attestation.actualProductMajor -eq [int]$serverIdentity.product_major -and
                [string]::Equals([string]$attestation.sqlTarget.dataSource, $masterBuilder.DataSource, [StringComparison]::OrdinalIgnoreCase)
            $tier1.diagnosticCode = if ($tier1.baseSatisfied) { 'SQL_TIER1_AWAITING_BUILD_BINDING' } else { 'SQL_TIER1_ATTESTATION_INVALID' }
        } catch {
            $tier1.diagnosticCode = 'SQL_TIER1_ATTESTATION_INVALID'
        }
    }

    $sourceCommit = @(& git -C $ServiceRoot rev-parse HEAD 2>$null) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($sourceCommit)) {
        $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
        $sourceCommit = if (Test-Path $versionFile) {
            ([string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceCommit=*' } | Select-Object -First 1))).Substring('SourceCommit='.Length)
        } else { 'unknown' }
    }
    $sourceStatus = @(& git -C $ServiceRoot status --porcelain=v1 --untracked-files=normal 2>$null)
    $sourceDirty = if ($LASTEXITCODE -eq 0) {
        $sourceStatus.Count -gt 0
    } else {
        $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
        $dirtyLine = if (Test-Path $versionFile) { [string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceDirty=*' } | Select-Object -First 1)) } else { '' }
        if ($dirtyLine -eq 'SourceDirty=True') { $true } elseif ($dirtyLine -eq 'SourceDirty=False') { $false } else { $null }
    }
    $hostArtifactPath = if (Test-Path -LiteralPath (Join-Path $ServiceRoot 'MesIngest.Host.dll') -PathType Leaf) {
        Join-Path $ServiceRoot 'MesIngest.Host.dll'
    } else {
        Join-Path $ServiceRoot 'MesIngest.Host.exe'
    }
    $hostArtifact = Get-Item -LiteralPath $hostArtifactPath
    $hostSha256 = Get-FileSha256 $hostArtifact.FullName
    $hostFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostArtifact.FullName).FileVersion
    if ($null -ne $attestation) {
        $tier1.buildBound = [string]::Equals(
                [string]$attestation.sourceCommit,
                $sourceCommit,
                [StringComparison]::Ordinal) -and
            [string]::Equals(
                [string]$attestation.hostAssemblySha256,
                $hostSha256,
                [StringComparison]::OrdinalIgnoreCase)
    }
    $tier1.satisfied = $tier1.baseSatisfied -and $tier1.buildBound
    $tier1.diagnosticCode = if ($tier1.satisfied) {
        'SQL_TIER1_ATTESTATION_VERIFIED'
    } elseif ($tier1.baseSatisfied) {
        'SQL_TIER1_BUILD_MISMATCH'
    } else {
        [string]$tier1.diagnosticCode
    }
    $gateFailures = New-Object System.Collections.ArrayList
    @(Get-EvidenceGateFailures `
        -QueryEvidence @($queryEvidence) `
        -ActualPlanCount $planEvents.Count `
        -StatementMetricCount $statementEvents.Count `
        -RowCounts $rowCounts `
        -Tier1Satisfied ([bool]$tier1.satisfied) `
        -SourceCommit $sourceCommit `
        -CanonicalScaleProfile $canonicalScaleProfile `
        -QuerySurface $QuerySurface) | ForEach-Object { [void]$gateFailures.Add($_) }

    $growthComparison = [ordered]@{
        provided = $false
        baselineEvidencePath = $null
        baselineLogicalReads = $null
        observedLogicalReads = $null
        allowedLogicalReads = $null
        baselineMaxGrantedMemoryKb = $null
        observedMaxGrantedMemoryKb = $null
        surfaceComparisons = @()
        passed = $null
    }
    if (-not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
        $growthComparison.provided = $true
        $growthComparison.baselineEvidencePath = [IO.Path]::GetFullPath($BaselineEvidencePath)
        try {
            if (-not (Test-Path -LiteralPath $BaselineEvidencePath -PathType Leaf)) {
                throw 'Baseline evidence file does not exist.'
            }
            $baselineReport = Get-Content -Raw -LiteralPath $BaselineEvidencePath | ConvertFrom-Json
            $baselineDemandSeries = @($baselineReport.queries | Where-Object { $_.name -like 'DemandSeries*' })
            $observedDemandSeries = @($queryEvidence | Where-Object { $_.name -like 'DemandSeries*' })
            if ($baselineDemandSeries.Count -eq 0 -or $observedDemandSeries.Count -eq 0 -or
                -not [bool]$baselineReport.gate.passed -or
                [string]$baselineReport.profile.querySurface -ne 'DemandSeries' -or
                [string]$baselineReport.profile.evidenceScale -ne 'empty' -or
                [string]$baselineReport.sourceCommit -ne $sourceCommit -or
                [string]$baselineReport.build.hostSha256 -ne $hostSha256 -or
                [long]$baselineReport.profile.seriesCount -ne $SeriesCount -or
                [long]$baselineReport.profile.observationsPerRound -ne $ObservationsPerRound -or
                [long]$baselineReport.profile.roundIntervalSeconds -ne $RoundIntervalSeconds -or
                [long]$baselineReport.replay.warmupCount -ne $WarmupCount -or
                [long]$baselineReport.replay.measurementCount -ne $MeasurementCount -or
                [string]$baselineReport.sqlServer.dataSource -ne [string]$masterBuilder.DataSource -or
                [string]$baselineReport.sqlServer.productVersion -ne [string]$serverIdentity.product_version -or
                [long]$baselineReport.sqlServer.maxServerMemoryMb -ne [long]$serverIdentity.max_server_memory_mb -or
                [long]$baselineReport.contract.compatibilityLevel -ne [long]$databaseConfiguration.compatibilityLevel -or
                [string]$baselineReport.contract.recoveryModel -ne [string]$databaseConfiguration.recoveryModel) {
                throw 'Baseline evidence is not a passing DemandSeries run for this source commit.'
            }
            $surfaceComparisons = New-Object System.Collections.ArrayList
            foreach ($observedSurface in $observedDemandSeries) {
                $baselineSurface = @($baselineDemandSeries | Where-Object { $_.name -eq $observedSurface.name }) |
                    Select-Object -First 1
                if ($null -eq $baselineSurface) { throw "Baseline is missing $($observedSurface.name)." }
                $surfaceAllowed = [long][Math]::Max(
                    [Math]::Ceiling([long]$baselineSurface.logicalReads * 1.10),
                    [long]$baselineSurface.logicalReads + 200L)
                [void]$surfaceComparisons.Add([pscustomobject][ordered]@{
                    name = [string]$observedSurface.name
                    baselineLogicalReads = [long]$baselineSurface.logicalReads
                    observedLogicalReads = [long]$observedSurface.logicalReads
                    allowedLogicalReads = $surfaceAllowed
                    passed = [long]$observedSurface.logicalReads -le $surfaceAllowed
                })
            }
            $baselineLogicalReads = [long](($baselineDemandSeries | Measure-Object -Property logicalReads -Sum).Sum)
            $observedLogicalReads = [long](($observedDemandSeries | Measure-Object -Property logicalReads -Sum).Sum)
            $allowedLogicalReads = [long][Math]::Max(
                [Math]::Ceiling($baselineLogicalReads * 1.10),
                $baselineLogicalReads + 1000L)
            $growthComparison.baselineLogicalReads = $baselineLogicalReads
            $growthComparison.observedLogicalReads = $observedLogicalReads
            $growthComparison.allowedLogicalReads = $allowedLogicalReads
            $growthComparison.baselineMaxGrantedMemoryKb = [long](
                ($baselineDemandSeries | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum)
            $growthComparison.observedMaxGrantedMemoryKb = [long](
                ($observedDemandSeries | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum)
            $growthComparison.surfaceComparisons = @($surfaceComparisons)
            $logicalReadGrowthPassed = $observedLogicalReads -le $allowedLogicalReads -and
                @($surfaceComparisons | Where-Object { -not [bool]$_.passed }).Count -eq 0
            $memoryGrantGrowthPassed = $growthComparison.observedMaxGrantedMemoryKb -le
                ($growthComparison.baselineMaxGrantedMemoryKb + 1024L)
            $growthComparison.passed = $logicalReadGrowthPassed -and $memoryGrantGrowthPassed
            if (-not $logicalReadGrowthPassed) {
                [void]$gateFailures.Add('DEMAND_SERIES_LOGICAL_READ_GROWTH')
            }
            if (-not $memoryGrantGrowthPassed) {
                [void]$gateFailures.Add('DEMAND_SERIES_MEMORY_GRANT_GROWTH')
            }
        } catch {
            $growthComparison.passed = $false
            [void]$gateFailures.Add('INVALID_DEMAND_SERIES_BASELINE')
        }
    }

    $report = [ordered]@{
        schemaVersion = 1
        runId = $runId
        startedAt = $startedAt.ToString('o')
        completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        sourceCommit = $sourceCommit
        build = [ordered]@{
            sourceCommit = $sourceCommit; sourceDirty = $sourceDirty; configuration = $BuildConfiguration
            hostFile = $hostArtifact.Name; hostFileVersion = $hostFileVersion; hostSha256 = $hostSha256
        }
        database = [ordered]@{ name = $DatabaseName; ownerRunId = $runId; removedAfterEvidence = -not $KeepDatabase }
        sqlServer = [ordered]@{
            dataSource = $masterBuilder.DataSource; productVersion = $serverIdentity.product_version
            productMajor = $serverIdentity.product_major; engineEdition = $serverIdentity.engine_edition
            maxServerMemoryMb = $serverIdentity.max_server_memory_mb
        }
        contract = [ordered]@{
            schemaVersion = $schemaIdentity.schemaVersion; contractVersion = $schemaIdentity.contractVersion
            historyEpoch = $schemaIdentity.historyEpoch
            compatibilityLevel = $databaseConfiguration.compatibilityLevel; recoveryModel = $databaseConfiguration.recoveryModel
        }
        profile = [ordered]@{
            historyDays = $profile.historyDays
            distribution = if ($canonicalScaleProfile) { $profile.distribution } else { 'diagnostic-override' }
            canonical = $canonicalScaleProfile; querySurface = $QuerySurface; seed = 8005
            evidenceScale = if ($RepresentativeHistoryRounds -gt 0) { 'representative-history' } elseif ($ProfileDays -eq 0) { 'empty' } else { 'full-profile' }
            anchorUtc = $AnchorUtc.ToUniversalTime().ToString('o'); roundIntervalSeconds = $RoundIntervalSeconds
            observationsPerRound = $ObservationsPerRound; historyRoundCount = $historyRoundCount
            expectedRawObservationCount = $expectedTotalObservationCount; seriesCount = $SeriesCount
            activeRatio = $profile.activeRatio; archivedRatio = $profile.archivedRatio; errorRatio = $profile.errorRatio
        }
        data = $rowCounts
        replay = [ordered]@{
            warmupCount = $WarmupCount; measurementCount = $MeasurementCount
            queryParameters = @($surfaces | ForEach-Object { [ordered]@{ name = $_.name; path = $_.path } }) + $(
                if ($measureRawEvidence) { @([ordered]@{ name = 'RawEvidence'; path = '/raw-observations' }) } else { @() })
        }
        queries = @($queryEvidence)
        growthComparison = $growthComparison
        statementMetrics = @($statementMetrics)
        actualPlans = @($planSummaries)
        storage = $storage
        tests = $tier1
        gate = [ordered]@{ passed = $gateFailures.Count -eq 0; failures = @($gateFailures) }
        safety = [ordered]@{ isolatedDatabaseOnly = $true; productionDatabaseRead = $false; credentialsWritten = $false; connectionStringWritten = $false }
    }
    $jsonPath = Join-Path $runDirectory 'scale-query-evidence.json'
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $manifestPath = Join-Path $runDirectory 'replay-manifest.json'
    [ordered]@{
        schemaVersion = 1; runId = $runId; sourceCommit = $sourceCommit; build = $report.build; schema = $schemaIdentity.schemaVersion
        contract = $schemaIdentity.contractVersion; historyEpoch = $schemaIdentity.historyEpoch
        sqlProductVersion = $serverIdentity.product_version
        profile = $report.profile; replay = $report.replay
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $markdownPath = Join-Path $runDirectory 'scale-query-evidence.md'
    @(
        '# MesIngest scale and query evidence'
        ''
        "- Run: $runId"
        "- Profile: $ProfileDays days; raw observations: $($rowCounts.raw_observations)"
        "- Contract/schema: $($schemaIdentity.contractVersion) / $($schemaIdentity.schemaVersion)"
        "- HistoryEpoch: $($schemaIdentity.historyEpoch)"
        "- SQL Server: $($serverIdentity.product_version)"
        "- Actual plans: $($planEvents.Count); statement metrics: $($statementEvents.Count)"
        "- SQL Tier 1: Failed=$($tier1.failed), Passed=$($tier1.passed), Skipped=$($tier1.sqlSkippedTests), Total=$($tier1.total)"
        "- Gate passed: $($report.gate.passed)"
        "- Failures: $(@($gateFailures) -join ', ')"
    ) | Set-Content -LiteralPath $markdownPath -Encoding UTF8
    $inventoryPath = Join-Path $runDirectory 'sha256-inventory.json'
    @($jsonPath, $manifestPath, $markdownPath) | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject][ordered]@{ file = $item.Name; length = $item.Length; sha256 = Get-FileSha256 $_ }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding UTF8

    Write-Output "MESINGEST_SCALE_EVIDENCE: passed=$($report.gate.passed) run=$runDirectory"
    Write-Output "JSON=$jsonPath"
    if (-not $report.gate.passed) { throw "Scale evidence gate failed: $(@($gateFailures) -join ', ')" }
}
finally {
    $cleanupFailure = $null
    Stop-EvidenceHost $hostRun
    if ($xeventStarted) {
        try { [void](Invoke-SqlNonQuery $masterConnectionString "ALTER EVENT SESSION [$($xeventSession.Replace(']', ']]'))] ON SERVER STATE = STOP;") } catch { }
    }
    try {
        $exists = @(Invoke-SqlTable $masterConnectionString 'SELECT name FROM sys.server_event_sessions WHERE name = @name;' @{ '@name' = $xeventSession })
        if ($exists.Count -eq 1) { [void](Invoke-SqlNonQuery $masterConnectionString "DROP EVENT SESSION [$($xeventSession.Replace(']', ']]'))] ON SERVER;") }
    } catch { }
    if (-not [string]::IsNullOrWhiteSpace($xelBase)) {
        try {
            $xelPattern = $xelBase.Substring(0, $xelBase.Length - 4) + '*.xel'
            $xelFiles = @(Invoke-SqlTable $masterConnectionString @"
SELECT DISTINCT file_name
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL);
"@ @{ '@xelPattern' = $xelPattern } 300)
            $expectedPrefix = [IO.Path]::GetFullPath($xelBase.Substring(0, $xelBase.Length - 4))
            foreach ($xelFile in $xelFiles) {
                $resolvedXel = [IO.Path]::GetFullPath([string]$xelFile.file_name)
                if (-not $resolvedXel.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals([IO.Path]::GetExtension($resolvedXel), '.xel', [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing unsafe XEvent cleanup target: $resolvedXel"
                }
                [void](Invoke-SqlNonQuery $masterConnectionString 'EXEC master.sys.xp_delete_files @filePath;' @{ '@filePath' = $resolvedXel })
            }
        } catch { $cleanupFailure = $_ }
    }
    if ($databaseCreated -and -not $KeepDatabase) {
        $owned = $false
        if ($databaseOwned) {
            try {
                $marker = @(Invoke-SqlTable $databaseConnectionString @"
SELECT CONVERT(nvarchar(128), value) AS owner_run_id
FROM sys.extended_properties WHERE class = 0 AND name = N'$ownerProperty';
"@) | Select-Object -First 1
                $owned = $null -ne $marker -and [string]$marker.owner_run_id -ceq $runId
            } catch { $owned = $false }
        }
        if (-not $owned) {
            throw "Refusing cleanup because database ownership cannot be proven for '$DatabaseName'."
        }
        [void](Invoke-SqlNonQuery $masterConnectionString @"
IF DB_ID(@databaseName) IS NOT NULL
BEGIN
    ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$DatabaseName];
END
"@ @{ '@databaseName' = $DatabaseName })
    }
    if ($null -ne $cleanupFailure) { throw $cleanupFailure }
}
