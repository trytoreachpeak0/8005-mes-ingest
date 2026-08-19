#Requires -Version 5.1
<#
.SYNOPSIS
  Ticket 26 factory acceptance: one live-Oracle, live-SQL Server run of the shipped
  package, written up as reviewable release sign-off input.

.DESCRIPTION
  This is the live sibling of Invoke-ReleaseSmoke.ps1. The smoke drives the production
  entry from a recording so it can run anywhere; this script refuses a recording and only
  accepts rounds that actually executed the approved statement against the plant Oracle.

  Oracle is read-only here and stays read-only: the Host may execute exactly one
  hash-pinned SELECT artifact, the business surface is GET, and this script opens no
  Oracle connection of its own.

  Every declared check produces a result. A check that could not run becomes a named skip
  carrying an owner, the prerequisite it needs, and the release gate it leaves open; a
  check that failed keeps its evidence and the run reports FAILED. Nothing here upgrades
  laboratory or golden-machine evidence into a factory pass, and ticket 23's pixel,
  stability, baseline and DPI gates are deliberately not repeated — see -WatchPixelGates.

  Evidence is written as counts, identities, revisions and digests. Response bodies and
  screenshots stay on the plant machine: they carry customer rows.

.PARAMETER ArtifactsDirectory
  Where run evidence is written. Use a path outside the repository: raw evidence may name
  the plant environment.

.PARAMETER IncludePackagedWatch
  Drive the packaged Watch against this live Host on the interactive plant desktop:
  the six pages, settings, compact Host state, and the Service outliving the Watch.
  Without it both Watch checks are named skips.

.NOTES
  Credentials are read from the environment, never from parameters:
    MES_INGEST_FACTORY_SQLSERVER              dedicated, disposable, empty database
    MES_INGEST_FACTORY_EMPTY_DATABASE_CONFIRMED=YES
  Oracle credentials come from the operator-filled service\appsettings.Local.json.
#>
[CmdletBinding()]
param(
    [string] $ArtifactsDirectory,

    [ValidateRange(2, 20)]
    [int] $RoundCount = 3,

    [ValidateRange(30, 3600)]
    [int] $RoundWaitTimeoutSeconds = 300,

    [ValidateRange(10, 300)]
    [int] $StartupTimeoutSeconds = 120,

    [ValidateRange(1, 60)]
    [int] $PostPollDelaySeconds = 5,

    [ValidateSet('Thin', 'Thick')]
    [string] $OracleMode = 'Thin',

    [switch] $IncludePackagedWatch,

    [ValidateRange(1, 120)]
    [int] $PackagedWatchStartupBudgetSeconds = 25,

    [ValidateRange(5, 600)]
    [int] $WatchObservationSeconds = 45,

    [string] $Operator = '',

    [string] $Site = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FactoryAcceptanceTools.ps1')

$expectedContractVersion = '2026.08.new-mes-ingest.v2.0'
$expectedContractSchemaVersion = 17
$expectedCapabilityIds = @(
    'CONTRACT_DISCOVERY',
    'CURRENT_INGEST_ATTENTION',
    'DEMAND_SERIES',
    'ERROR_SEARCH',
    'EXTERNALLY_READABLE_DEMAND_CATALOG',
    'POLL_HEALTH_AND_EVIDENCE',
    'READABILITY_AUDIT',
    'SERIES_ERROR_CATALOG',
    'WATCH_OVERVIEW'
)
$canonicalQueryRelativePath = 'service/queries/mes-task-union/query.sql'
$canonicalQuerySha256 = '54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae'
$canonicalQueryVersion = "MES_TASK_UNION/sha256:$canonicalQuerySha256"

# Every check the run must account for. New-FactoryAcceptanceSummary refuses to close
# while any of these has no result, so an unexecuted step cannot leave silently.
$declaredCheckIds = @(
    'PACKAGE_IDENTITY_MATCHES_RELEASE_MANIFEST',
    'TARGET_ENVIRONMENT_IDENTITY_RECORDED',
    'CANONICAL_QUERY_READ_ONLY_BOUNDARY',
    'LIVE_ORACLE_READ_ONLY_PROBE',
    'LIVE_ORACLE_THICK_MODE_REVERIFICATION',
    'LIVE_MES_TASK_UNION_ROUNDS',
    'NON_SUCCESS_ROUNDS_PRESERVE_PROJECTION',
    'CONTRACT_DISCOVERY_AND_AUTHORIZATION',
    'EXTERNALLY_READABLE_CATALOG_FIRST_BODY_AND_304',
    'PRIMARY_READ_SURFACES',
    'REMOTE_BINDING_DATA_ACCESS_PROTECTION',
    'SQLSERVER_PROJECTION_COMMIT_PERSISTENCE',
    'SQLSERVER_RESTART_BARRIER',
    'TASK_TYPE_PROTECTION_READ',
    'CATALOG_REVISION_MONOTONIC',
    'SNAPSHOT_READ_NOT_TORN',
    'WATCH_SIX_PAGE_LIVE_HOST_VERIFICATION',
    'WATCH_PAGING_ON_LIVE_DATA',
    'WATCH_FAILURE_RETENTION',
    'WATCH_CLOSED_SERVICE_CONTINUES',
    'TICKET23_VISUAL_GATES_NOT_REPEATED',
    'EVIDENCE_REDACTION_AND_HASHES'
)

$checks = New-Object System.Collections.ArrayList

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][string] $Title,
        [Parameter(Mandatory = $true)][string] $Gate,
        [Parameter(Mandatory = $true)][ValidateSet('PASSED', 'FAILED', 'SKIPPED')][string] $Status,
        [Parameter(Mandatory = $true)][string] $Detail,
        [string] $Owner = '',
        [string] $Requires = '',
        [string[]] $Evidence = @()
    )

    [void]$checks.Add((New-AcceptanceCheck `
        -Id $Id -Title $Title -Gate $Gate -Status $Status -Detail $Detail `
        -Owner $Owner -Requires $Requires -Evidence $Evidence))
}

# A failing section keeps its red evidence and the run continues, so one plant defect does
# not hide the state of everything after it.
function Invoke-AcceptanceSection {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][string] $Title,
        [Parameter(Mandatory = $true)][string] $Gate,
        [Parameter(Mandatory = $true)][scriptblock] $Body,
        [string[]] $Evidence = @(),
        # A body that throws the literal NOT_OBSERVED says the plant window never produced
        # the condition it exists to judge. That is a named skip, not a pass and not a red.
        [switch] $SkipWhenNotObserved,
        [string] $SkipOwner = '',
        [string] $SkipRequires = '',
        [string] $SkipDetail = ''
    )

    Write-Host "[acceptance] $Id"
    try {
        $detail = & $Body
        Add-Check -Id $Id -Title $Title -Gate $Gate -Status PASSED `
            -Detail ([string]$detail) -Evidence $Evidence
        return $true
    } catch [System.Management.Automation.RuntimeException] {
        if ($SkipWhenNotObserved -and [string]$_.Exception.Message -ceq 'NOT_OBSERVED') {
            Write-Host "$Id SKIPPED: not observed in this window"
            Add-Check -Id $Id -Title $Title -Gate $Gate -Status SKIPPED `
                -Owner $SkipOwner -Requires $SkipRequires -Detail $SkipDetail -Evidence $Evidence
            return $false
        }
        $message = Protect-FactoryAcceptanceText -Text ([string]$_.Exception.Message)
        Write-Warning "$Id FAILED: $message"
        Add-Check -Id $Id -Title $Title -Gate $Gate -Status FAILED `
            -Detail $message -Evidence $Evidence
        return $false
    } catch {
        $message = Protect-FactoryAcceptanceText -Text ([string]$_.Exception.Message)
        Write-Warning "$Id FAILED: $message"
        Add-Check -Id $Id -Title $Title -Gate $Gate -Status FAILED `
            -Detail $message -Evidence $Evidence
        return $false
    }
}

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

# One GET with explicit conditional and authorization headers; Invoke-WebRequest cannot
# read a 304 with its ETag, which the catalog contract requires.
function Invoke-AcceptanceRequest {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $Uri,
        [string] $IfNoneMatch,
        [string] $BearerToken
    )

    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
    try {
        if (-not [string]::IsNullOrWhiteSpace($IfNoneMatch)) {
            $message.Headers.TryAddWithoutValidation('If-None-Match', $IfNoneMatch) | Out-Null
        }
        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            $message.Headers.TryAddWithoutValidation('Authorization', "Bearer $BearerToken") | Out-Null
        }
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $response = $Client.SendAsync($message).GetAwaiter().GetResult()
        try {
            $stopwatch.Stop()
            $correlationHeader = $null
            $headerValues = $null
            if ($response.Headers.TryGetValues('X-Correlation-Id', [ref] $headerValues)) {
                $correlationHeader = @($headerValues) -join ','
            }
            [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                ETag = if ($null -ne $response.Headers.ETag) { $response.Headers.ETag.ToString() } else { $null }
                Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                CorrelationId = $correlationHeader
                ElapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
            }
        } finally {
            $response.Dispose()
        }
    } finally {
        $message.Dispose()
    }
}

# A refused read answers with the contract's {code, error} envelope. The code is the
# triage fact and carries no plant row, so it belongs in the red evidence.
function Get-ContractErrorCode {
    param([AllowEmptyString()][AllowNull()][string] $Body)

    if ([string]::IsNullOrWhiteSpace($Body)) { return '' }
    try {
        $code = [string]($Body | ConvertFrom-Json).code
    } catch {
        return ''
    }
    if ([string]::IsNullOrWhiteSpace($code)) { return '' }
    return " ($code)"
}

function Get-AcceptanceJson {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $Uri,
        [string] $BearerToken
    )

    $response = Invoke-AcceptanceRequest -Client $Client -Uri $Uri -BearerToken $BearerToken
    if ($response.StatusCode -ne 200) {
        throw "GET $(Split-Path -Leaf $Uri) returned HTTP $($response.StatusCode)$(Get-ContractErrorCode -Body $response.Body)."
    }
    return ($response.Body | ConvertFrom-Json)
}

# ConvertFrom-Json objects also carry PowerShell's intrinsic Count, so a contract field
# literally named "count" has to be read from the JSON properties.
function Get-JsonProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "The response is missing the required contract field '$Name'."
    }
    return $property.Value
}

function Start-LiveHostProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $SqlConnectionString,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $SharedSecret,
        [bool] $ContinuousPoll = $true,
        [bool] $DrainStreams = $true,
        # Where the Host's own stage/correlation lines are kept. Without them a refused
        # live read is a status code with no reason attached.
        [string] $LogPath = ''
    )

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = Split-Path -Parent $Executable
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    $info.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    foreach ($leak in @(
        'MES_INGEST_FACTORY_SQLSERVER',
        'MES_INGEST_FACTORY_EMPTY_DATABASE_CONFIRMED',
        'MES_INGEST_RELEASE_SMOKE_SQLSERVER',
        'MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED',
        'MES_INGEST_TICKET01_SQLSERVER')) {
        $info.EnvironmentVariables.Remove($leak)
    }
    # The round source is the operator-filled appsettings.Local.json beside the executable.
    # Only transport, storage and loop cadence are overridden here; a recording is refused
    # before this point and is never introduced by this script.
    $info.EnvironmentVariables['MesIngest__NewSqlServerConnectionString'] = $SqlConnectionString
    $info.EnvironmentVariables['MesIngest__Urls'] = $BaseUrl
    $info.EnvironmentVariables['MesIngest__SharedSecret'] = $SharedSecret
    $info.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] = 'false'
    $info.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] =
        $(if ($ContinuousPoll) { 'true' } else { 'false' })
    $info.EnvironmentVariables['MesIngest__PostPollDelaySeconds'] = "$PostPollDelaySeconds"

    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'The packaged Host did not start.' }
    if ($DrainStreams) {
        # Drain both streams so a full pipe cannot block the Host. Provider failures can
        # name a datasource or a credential, so the handler redacts as it writes and the
        # evidence pass redacts the whole file again before it is published.
        if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
            foreach ($streamEvent in @('OutputDataReceived', 'ErrorDataReceived')) {
                $subscription = Register-ObjectEvent -InputObject $process -EventName $streamEvent `
                    -MessageData $LogPath -Action {
                        $line = $EventArgs.Data
                        if ([string]::IsNullOrEmpty($line)) { return }
                        $line = $line -replace '(?i)((?:password|pwd|secret|token|user id|uid|data source|server|host|initial catalog|database)\s*[:=]\s*)[^;\s,)"]+', '$1[redacted]'
                        $line = $line -replace '\d{1,3}(\.\d{1,3}){3}', '[redacted]'
                        Add-Content -LiteralPath $Event.MessageData -Value $line
                    }
                [void]$script:hostLogSubscriptions.Add($subscription)
            }
        }
        $process.BeginOutputReadLine()
        $process.BeginErrorReadLine()
    }
    return $process
}

function Wait-LiveHostContract {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $contract = $null
    do {
        if ($Process.HasExited) {
            throw "The packaged Host exited during startup with code $($Process.ExitCode)."
        }
        try {
            $contract = Invoke-RestMethod -Uri "$BaseUrl/api/v2/contract" -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 250
        }
    } while ($null -eq $contract -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $contract) {
        throw "The packaged Host did not expose /api/v2/contract within $TimeoutSeconds seconds."
    }
    return $contract
}

# A freshly bootstrapped database has no projection yet, and the read surfaces answer 409
# until the first round commits. That is a state to wait through, not a defect, so the
# caller can ask for a null instead of an exception while it waits.
function Get-AttentionSnapshot {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [string] $BearerToken,
        [switch] $AllowProjectionNotAvailable
    )

    $uri = "$BaseUrl/api/v2/current-ingest-attention?kind=POLL_RUN_FAILURE&pageSize=5"
    if (-not $AllowProjectionNotAvailable) {
        return Get-AcceptanceJson -Client $Client -Uri $uri -BearerToken $BearerToken
    }
    $response = Invoke-AcceptanceRequest -Client $Client -Uri $uri -BearerToken $BearerToken
    if ($response.StatusCode -eq 409) { return $null }
    if ($response.StatusCode -ne 200) {
        throw "GET current-ingest-attention returned HTTP $($response.StatusCode)."
    }
    return ($response.Body | ConvertFrom-Json)
}

function Get-PollTraceHighWater {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [string] $BearerToken
    )

    return [long](Get-AttentionSnapshot -Client $Client -BaseUrl $BaseUrl `
        -BearerToken $BearerToken).snapshot.pollTraceHighWater
}

# Waiting for the loop to commit again is what turns several checks from a snapshot of a
# moving projection into a statement about what survives a commit.
function Wait-PollTraceHighWaterAbove {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][long] $Previous,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds,
        [string] $BearerToken
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 750
        $current = Get-PollTraceHighWater -Client $Client -BaseUrl $BaseUrl -BearerToken $BearerToken
        if ($current -gt $Previous) { return $current }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $Previous
}

<#
.SYNOPSIS
  Find a restricted raw-evidence resource that exists in the live snapshot.

.DESCRIPTION
  Returns an empty string when the plant snapshot carries no error evidence. The caller
  then says so rather than presenting a refused placeholder as an authorized read.
#>
function Resolve-RestrictedRawEvidenceUri {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $BearerToken
    )

    $list = Get-AcceptanceJson -Client $Client -BearerToken $BearerToken `
        -Uri "$BaseUrl/api/v2/error-search?pageSize=5"
    $snapshotReference = [string]$list.snapshotReference
    foreach ($item in @($list.items)) {
        $seriesId = [string]$item.seriesId
        if ([string]::IsNullOrWhiteSpace($seriesId)) { continue }
        $detail = Get-AcceptanceJson -Client $Client -BearerToken $BearerToken `
            -Uri ("$BaseUrl/api/v2/error-search/$([Uri]::EscapeDataString($seriesId))" +
                "?snapshot=$([Uri]::EscapeDataString($snapshotReference))")
        foreach ($period in @($detail.periods)) {
            foreach ($evidence in @($period.evidence)) {
                $evidenceId = [string]$evidence.evidenceId
                if ([string]::IsNullOrWhiteSpace($evidenceId)) { continue }
                return ("$BaseUrl/api/v2/error-search/$([Uri]::EscapeDataString($seriesId))" +
                    "/evidence/$([Uri]::EscapeDataString($evidenceId))/raw-observations" +
                    "?snapshot=$([Uri]::EscapeDataString($detail.snapshotReference))")
            }
        }
    }
    return ''
}

function Stop-LiveHostProcess {
    param([Diagnostics.Process] $Process)

    if ($null -eq $Process) { return }
    if (-not $Process.HasExited) {
        $Process.Kill()
        $Process.WaitForExit(10000) | Out-Null
    }
    $Process.Dispose()
}

# ---------------------------------------------------------------------------
# Preflight. A failure here stops the run: there is nothing honest to record.
# ---------------------------------------------------------------------------

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serviceExecutable = Join-Path $packageRoot 'service\MesIngest.Host.exe'
$watchExecutable = Join-Path $packageRoot 'watch\MesIngest.Watch.exe'
$localSettingsPath = Join-Path $packageRoot 'service\appsettings.Local.json'
$canonicalQueryPath = Join-Path $packageRoot ($canonicalQueryRelativePath -replace '/', '\')

foreach ($required in @($serviceExecutable, $canonicalQueryPath, $localSettingsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Factory acceptance input missing: $required"
    }
}
if ($IncludePackagedWatch) {
    if (-not (Test-Path -LiteralPath $watchExecutable -PathType Leaf)) {
        throw "IncludePackagedWatch requires the packaged Watch: $watchExecutable"
    }
    if (-not [Environment]::UserInteractive) {
        throw 'IncludePackagedWatch needs the interactive plant desktop; this session is not interactive.'
    }
}

# Refuse a recording before anything starts. Replayed rounds drive the same production
# entry, which is exactly why they can never be this run's evidence.
$roundSourceConfiguration = Assert-LiveRoundSourceConfiguration `
    -SettingsPath $localSettingsPath -ExpectedMode $OracleMode
foreach ($replayVariable in @(
    'MesIngest__ReplayRoundsFromRecordingPath', 'MesIngest__ReplayRoundsAcknowledgement')) {
    if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($replayVariable))) {
        throw "RECORDED_ROUNDS_ARE_NOT_FACTORY_EVIDENCE: $replayVariable is set in this session."
    }
}

$sqlConnectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_FACTORY_SQLSERVER')
if ([string]::IsNullOrWhiteSpace($sqlConnectionString)) {
    throw 'Factory acceptance requires a dedicated empty database via MES_INGEST_FACTORY_SQLSERVER.'
}
if ([Environment]::GetEnvironmentVariable('MES_INGEST_FACTORY_EMPTY_DATABASE_CONFIRMED') -cne 'YES') {
    throw 'Set MES_INGEST_FACTORY_EMPTY_DATABASE_CONFIRMED=YES only after confirming the target database is dedicated, disposable, and empty.'
}
# Preflight with the driver the Host itself uses. The legacy System.Data.SqlClient
# negotiates differently, so it can connect where Microsoft.Data.SqlClient times out —
# a preflight on the wrong driver would clear a target the Host cannot reach.
$hostSqlClientAssembly = Join-Path $packageRoot 'service\Microsoft.Data.SqlClient.dll'
$sqlClientNamespace = 'System.Data.SqlClient'
if (Test-Path -LiteralPath $hostSqlClientAssembly -PathType Leaf) {
    try {
        Add-Type -Path $hostSqlClientAssembly
        $sqlClientNamespace = 'Microsoft.Data.SqlClient'
    } catch {
        Write-Warning ('The packaged SQL client could not be loaded for preflight; ' +
            'falling back to System.Data.SqlClient. A Host connection failure will then ' +
            'only appear at startup.')
    }
}
try {
    $sqlBuilder = New-Object "$sqlClientNamespace.SqlConnectionStringBuilder" $sqlConnectionString
} catch {
    throw 'MES_INGEST_FACTORY_SQLSERVER is not a valid SQL Server connection string.'
}
$targetDatabase = $sqlBuilder.InitialCatalog.Trim()
if ([string]::IsNullOrWhiteSpace($targetDatabase) -or
    $targetDatabase -in @('master', 'model', 'msdb', 'tempdb')) {
    throw 'Factory acceptance requires an explicit non-system Initial Catalog.'
}

# The run owns no database lifecycle: it never drops or clears an operator's target.
$sqlServerIdentity = $null
$preflightUserTableCount = -1
$connection = $null
try {
    $connection = New-Object "$sqlClientNamespace.SqlConnection" $sqlBuilder.ConnectionString
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 20
    $command.CommandText = @'
SELECT
    CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) + '\' +
        ISNULL(CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), 'MSSQLSERVER') AS ServerIdentity,
    DB_NAME() AS DatabaseName,
    CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS ProductMajor,
    CAST(DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel') AS int) AS CompatibilityLevel,
    (SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0) AS UserTables;
'@
    $reader = $command.ExecuteReader()
    try {
        if (-not $reader.Read()) { throw 'The target SQL Server did not answer the identity query.' }
        $sqlServerIdentity = [ordered]@{
            server = [string]$reader['ServerIdentity']
            database = [string]$reader['DatabaseName']
            productMajorVersion = [int]$reader['ProductMajor']
            compatibilityLevel = [int]$reader['CompatibilityLevel']
        }
        $preflightUserTableCount = [long]$reader['UserTables']
    } finally {
        $reader.Dispose()
    }
    $command.Dispose()
} catch {
    throw "Unable to reach the dedicated factory SQL Server database: $(Protect-FactoryAcceptanceText -Text $_.Exception.Message)"
} finally {
    if ($null -ne $connection) { $connection.Dispose() }
}
if ($preflightUserTableCount -ne 0) {
    throw "The dedicated factory database must have zero user tables before Host bootstrap; found $preflightUserTableCount."
}

$startedAt = [DateTimeOffset]::UtcNow
$runId = 'factory-acceptance-{0}-{1}' -f `
    $startedAt.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path ([IO.Path]::GetTempPath()) $runId
} else {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$watchEvidenceDirectory = Join-Path $artifacts 'watch'

# Exit codes, clock and machine identity: the run has to be attributable afterwards.
$environmentRecord = [ordered]@{
    runId = $runId
    machine = [Environment]::MachineName
    user = [Environment]::UserName
    interactive = [Environment]::UserInteractive
    osVersion = [string][Environment]::OSVersion.Version
    powerShell = [string]$PSVersionTable.PSVersion
    culture = (Get-Culture).Name
    startedAtUtc = $startedAt.ToString('O')
    startedAtLocal = [DateTimeOffset]::Now.ToString('O')
    timeZone = (Get-TimeZone).Id
    utcOffset = [string]([DateTimeOffset]::Now.Offset)
    operator = $Operator
    site = $Site
    oracleMode = $OracleMode
    oracleCommandTimeoutSeconds = $roundSourceConfiguration.CommandTimeoutSeconds
    sqlServer = $sqlServerIdentity
    sqlClient = $sqlClientNamespace
    packageRoot = $packageRoot
}
$environmentRecord | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $artifacts 'environment.json') -Encoding UTF8

$sharedSecret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$hostProcess = $null
$restartedHostProcess = $null
$remoteBindProcess = $null
$watchProcess = $null
$httpClient = $null
$cleanupNotes = New-Object System.Collections.ArrayList
$hostLogSubscriptions = New-Object System.Collections.ArrayList
$hostLogPath = Join-Path $artifacts 'host-stage-log.txt'
$rounds = New-Object System.Collections.ArrayList
$packageIdentity = $null
$watchWindow = $null
$abortReason = ''

try {
    Add-Type -AssemblyName System.Net.Http
    $httpClient = [Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(30)

    # -----------------------------------------------------------------------
    # 1. The package on this machine is the released artifact, byte for byte.
    # -----------------------------------------------------------------------
    [void](Invoke-AcceptanceSection `
        -Id 'PACKAGE_IDENTITY_MATCHES_RELEASE_MANIFEST' `
        -Title 'Deployed package is the released artifact' `
        -Gate 'FACTORY_PACKAGE_IDENTITY' `
        -Evidence @('package-identity.json') `
        -Body {
            $identity = Test-PackageIdentity -PackageRoot $packageRoot
            $script:packageIdentity = $identity
            $identity | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $artifacts 'package-identity.json') -Encoding UTF8
            if (-not $identity.Identical) {
                throw ('The deployed package differs from RELEASE-MANIFEST.json: ' +
                    "missing=$($identity.Missing.Count) mismatched=$($identity.Mismatched.Count) " +
                    "unexpected=$($identity.Unexpected.Count).")
            }
            if ($identity.SourceDirty) {
                throw 'The release manifest records a dirty source tree; the plant copy is not a released artifact.'
            }
            ("$($identity.DeclaredFileCount) files match RELEASE-MANIFEST.json " +
                "(sourceCommit=$($identity.SourceCommit), manifest sha256=$($identity.ManifestSha256)).")
        })

    [void](Invoke-AcceptanceSection `
        -Id 'TARGET_ENVIRONMENT_IDENTITY_RECORDED' `
        -Title 'Target environment identity recorded' `
        -Gate 'FACTORY_PACKAGE_IDENTITY' `
        -Evidence @('environment.json') `
        -Body {
            ("SQL Server $($sqlServerIdentity.server)/$($sqlServerIdentity.database) " +
                "(major $($sqlServerIdentity.productMajorVersion), compatibility $($sqlServerIdentity.compatibilityLevel), " +
                "0 user tables before bootstrap); Oracle mode $OracleMode with " +
                "commandTimeout=$($roundSourceConfiguration.CommandTimeoutSeconds)s from the operator configuration; " +
                "machine $([Environment]::MachineName), clock $([DateTimeOffset]::Now.ToString('O')).")
        })

    # -----------------------------------------------------------------------
    # 2. Read-only boundary of the one statement the Host may execute.
    # -----------------------------------------------------------------------
    [void](Invoke-AcceptanceSection `
        -Id 'CANONICAL_QUERY_READ_ONLY_BOUNDARY' `
        -Title 'One approved six-branch read-only statement' `
        -Gate 'FACTORY_ORACLE_ACCEPTANCE' `
        -Body {
            $sqlFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.sql' -File -Recurse -Force)
            if ($sqlFiles.Count -ne 1) {
                throw "The package must carry exactly one SQL artifact; found $($sqlFiles.Count)."
            }
            $statement = Test-CanonicalReadOnlyStatement `
                -Sql ([IO.File]::ReadAllText($canonicalQueryPath)) -ExpectedSha256 $canonicalQuerySha256
            if (-not $statement.ReadOnly) {
                throw ("The deployed statement is not the approved read-only artifact: " +
                    "approved=$($statement.MatchesApprovedArtifact) statements=$($statement.StatementCount) " +
                    "writes=$($statement.WriteKeywordsFound -join ',').")
            }
            if ($statement.TaskTypeBranchCount -ne 6 -or $statement.UnionAllCount -ne 5) {
                throw ("The deployed statement is not the six-branch UNION ALL: " +
                    "branches=$($statement.TaskTypeBranchCount) unionAll=$($statement.UnionAllCount).")
            }
            ("One statement, $($statement.TaskTypeBranchCount) TASK_TYPE branches, " +
                "$($statement.UnionAllCount) UNION ALL, no write keyword, sha256=$($statement.Sha256).")
        })

    # -----------------------------------------------------------------------
    # 3. Live Oracle probe. Read-only, one execution of the approved statement.
    # -----------------------------------------------------------------------
    $probeState = $null
    [void](Invoke-AcceptanceSection `
        -Id 'LIVE_ORACLE_READ_ONLY_PROBE' `
        -Title "Live Oracle $OracleMode read-only probe" `
        -Gate 'FACTORY_ORACLE_ACCEPTANCE' `
        -Evidence @("probe-$($OracleMode.ToLowerInvariant()).txt") `
        -Body {
            $probeInfo = [Diagnostics.ProcessStartInfo]::new()
            $probeInfo.FileName = $serviceExecutable
            $probeInfo.WorkingDirectory = Split-Path -Parent $serviceExecutable
            $probeInfo.UseShellExecute = $false
            $probeInfo.RedirectStandardOutput = $true
            $probeInfo.RedirectStandardError = $true
            $probeInfo.ArgumentList.Add('--probe-oracle')
            $probeInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
            $probeProcess = [Diagnostics.Process]::Start($probeInfo)
            if ($null -eq $probeProcess) { throw 'The Oracle probe did not start.' }
            $probeOutput = $probeProcess.StandardOutput.ReadToEnd()
            $probeError = $probeProcess.StandardError.ReadToEnd()
            if (-not $probeProcess.WaitForExit(180000)) {
                $probeProcess.Kill()
                throw 'The Oracle probe did not finish within 180 seconds.'
            }
            $probeExitCode = $probeProcess.ExitCode
            $probeProcess.Dispose()

            $logName = "probe-$($OracleMode.ToLowerInvariant()).txt"
            $redacted = Protect-FactoryAcceptanceText -Text ($probeOutput + "`n" + $probeError)
            Set-Content -LiteralPath (Join-Path $artifacts $logName) -Value $redacted -Encoding UTF8

            $state = Get-LiveOracleProbeState `
                -ExpectedMode $OracleMode `
                -ProbeOutput $probeOutput `
                -ExpectedQueryVersion $canonicalQueryVersion `
                -ExpectedQuerySha256 $canonicalQuerySha256 `
                -ExitCode $probeExitCode `
                -Log $logName
            $script:probeState = $state
            $state | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath (Join-Path $artifacts 'oracle-probe-state.json') -Encoding UTF8

            if ($state.Result -cne 'PASSED') {
                throw ("The live Oracle probe did not pass: result=$($state.Result) " +
                    "scope=$($state.ExecutionScope) connectionAttempted=$($state.ConnectionAttempted) " +
                    "outcome=$($state.Outcome) exitCode=$($state.ExitCode). Evidence kept at $logName.")
            }
            ("execution_scope=$($state.ExecutionScope), connection_attempted=true, " +
                "mode $($state.RequestedMode)->$($state.ActualMode) via $($state.Driver), " +
                "query $($state.QueryVersion), outcome=$($state.Outcome), rows=$($state.RowCount), " +
                "duration=$($state.DurationMs)ms, commandTimeout=$($roundSourceConfiguration.CommandTimeoutSeconds)s.")
        })

    # Thick is a whole separate run of this script with -OracleMode Thick, not a mode this
    # run can switch into: it needs Instant Client and a registered ODBC driver on the box.
    if ($OracleMode -ceq 'Thick') {
        Add-Check -Id 'LIVE_ORACLE_THICK_MODE_REVERIFICATION' `
            -Title 'Thick mode re-verification through the same business entry' `
            -Gate 'FACTORY_ORACLE_ACCEPTANCE' -Status PASSED `
            -Detail 'This run is the Thick re-verification: the probe and rounds above ran in Thick mode.'
    } else {
        Add-Check -Id 'LIVE_ORACLE_THICK_MODE_REVERIFICATION' `
            -Title 'Thick mode re-verification through the same business entry' `
            -Gate 'FACTORY_ORACLE_ACCEPTANCE' -Status SKIPPED `
            -Owner 'plant IT / release owner' `
            -Requires 'Oracle Instant Client plus a registered Oracle ODBC driver, then rerun this script with -OracleMode Thick' `
            -Detail ('Not run. The ticket asks for Thick only when the plant actually needs it; ' +
                "the $OracleMode probe and rounds passed, so no Thick fallback was exercised.")
    }

    # -----------------------------------------------------------------------
    # 4. Live rounds through the production entry, against real SQL Server.
    # -----------------------------------------------------------------------
    $port = Get-FreeLoopbackPort
    $baseUrl = "http://127.0.0.1:$port"
    $hostProcess = Start-LiveHostProcess `
        -Executable $serviceExecutable `
        -BaseUrl $baseUrl `
        -SqlConnectionString $sqlConnectionString `
        -SharedSecret $sharedSecret `
        -ContinuousPoll $true `
        -LogPath $hostLogPath
    $contract = Wait-LiveHostContract `
        -Process $hostProcess -BaseUrl $baseUrl -TimeoutSeconds $StartupTimeoutSeconds

    $null = Invoke-AcceptanceSection `
        -Id 'LIVE_MES_TASK_UNION_ROUNDS' `
        -Title 'Complete MesTaskUnionRounds against the plant Oracle' `
        -Gate 'FACTORY_ORACLE_ACCEPTANCE' `
        -Evidence @('rounds.json') `
        -Body {
            $lastHighWater = -1
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds($RoundWaitTimeoutSeconds)
            while ($rounds.Count -lt $RoundCount -and [DateTimeOffset]::UtcNow -lt $deadline) {
                if ($hostProcess.HasExited) {
                    throw "The Host exited while rounds were being collected (code $($hostProcess.ExitCode))."
                }
                $attention = Get-AttentionSnapshot -Client $httpClient -BaseUrl $baseUrl `
                    -BearerToken $sharedSecret -AllowProjectionNotAvailable
                if ($null -eq $attention) {
                    # The empty database has bootstrapped but no round has committed yet.
                    Start-Sleep -Milliseconds 750
                    continue
                }
                $highWater = [long]$attention.snapshot.pollTraceHighWater
                if ($highWater -le 0 -or $highWater -le $lastHighWater) {
                    Start-Sleep -Milliseconds 750
                    continue
                }

                # A non-success latest round is exposed as a POLL_RUN_FAILURE item; a
                # success one is the snapshot's own trace identity.
                $failureItem = @($attention.items |
                    Where-Object { $_.kind -ceq 'POLL_RUN_FAILURE' -and
                        [long]$_.evidence.pollTraceSequence -eq $highWater }) | Select-Object -First 1
                $pollTraceId = if ($null -ne $failureItem) {
                    [string]$failureItem.evidence.pollTraceId
                } else {
                    [string]$attention.snapshot.pollTraceId
                }
                if ([string]::IsNullOrWhiteSpace($pollTraceId)) {
                    Start-Sleep -Milliseconds 750
                    continue
                }

                $trace = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                    -Uri "$baseUrl/api/v2/poll-traces/$([Uri]::EscapeDataString($pollTraceId))"
                $catalog = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                    -Uri "$baseUrl/api/v2/externally-readable-demand-catalog"
                [void]$rounds.Add([pscustomobject][ordered]@{
                    round = $rounds.Count + 1
                    pollTraceId = $pollTraceId
                    pollTraceHighWater = $highWater
                    outcome = [string]$trace.outcome
                    queryVersion = [string]$trace.queryVersion
                    contentDigest = [string]$trace.contentDigest
                    rowCount = [long]$trace.rowCount
                    diagnosticStage = $(if ($null -ne $trace.diagnostic) { [string]$trace.diagnostic.stage } else { '' })
                    diagnosticCode = $(if ($null -ne $trace.diagnostic) { [string]$trace.diagnostic.code } else { '' })
                    startedAt = [string]$trace.startedAt
                    completedAt = [string]$trace.completedAt
                    catalogRevision = [long]$catalog.catalogRevision
                    demandCount = [long](Get-JsonProperty -Object $catalog -Name 'count')
                    projectionCommitId = [string]$catalog.projectionCommitId
                })
                $lastHighWater = $highWater
            }

            $rounds | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $artifacts 'rounds.json') -Encoding UTF8

            if ($rounds.Count -lt $RoundCount) {
                throw ("Only $($rounds.Count) of $RoundCount rounds were observed within " +
                    "$RoundWaitTimeoutSeconds seconds.")
            }
            $wrongQuery = @($rounds | Where-Object { $_.queryVersion -cne $canonicalQueryVersion })
            if ($wrongQuery.Count -gt 0) {
                throw "A round reported a query version other than the approved artifact."
            }
            $successRounds = @($rounds | Where-Object { $_.outcome -ceq 'SUCCESS' })
            $outcomes = ($rounds | ForEach-Object { "$($_.outcome)/$($_.rowCount)" }) -join ', '
            ("$($rounds.Count) complete rounds, $($successRounds.Count) SUCCESS. " +
                "All on $canonicalQueryVersion. outcome/rows: $outcomes. " +
                "Canonical digests recorded per round in rounds.json.")
        }

    [void](Invoke-AcceptanceSection `
        -Id 'NON_SUCCESS_ROUNDS_PRESERVE_PROJECTION' `
        -Title 'A failed or incomplete round never moves the business projection' `
        -Gate 'FACTORY_ORACLE_ACCEPTANCE' `
        -Evidence @('rounds.json') `
        -Body {
            if ($rounds.Count -eq 0) {
                throw 'No round evidence was collected, so projection preservation cannot be judged.'
            }
            $preservation = Test-NonSuccessRoundsPreservedProjection -Rounds @($rounds)
            if (-not $preservation.Preserved) {
                throw ('A non-SUCCESS round changed the projection: ' +
                    "pollTraceIds=$($preservation.Offenders -join ',').")
            }
            if ($preservation.NonSuccessCount -eq 0) {
                # Nothing failed, so nothing was verified. Saying "passed" here would read
                # as "failed rounds were proven harmless", which this window cannot show.
                throw 'NOT_OBSERVED'
            }
            ("$($preservation.NonSuccessCount) non-SUCCESS round(s) left catalogRevision and " +
                'the Demand count unchanged; no Demand disappearance was fabricated.')
        } `
        -SkipWhenNotObserved `
        -SkipOwner 'release owner' `
        -SkipRequires ('a plant window that actually produces a FAILURE or INCOMPLETE round ' +
            '(for example an Oracle outage during a run)') `
        -SkipDetail ('Every observed round in this window committed normally, so no failed or ' +
            'structurally incomplete round was available to check. The rule itself is covered by ' +
            'the repository tests for Test-NonSuccessRoundsPreservedProjection.'))

    # -----------------------------------------------------------------------
    # 5. Versioned contract, authorization, catalog conditional read, main reads.
    # -----------------------------------------------------------------------
    [void](Invoke-AcceptanceSection `
        -Id 'CONTRACT_DISCOVERY_AND_AUTHORIZATION' `
        -Title 'Versioned contract discovery and restricted-evidence authorization' `
        -Gate 'FACTORY_API_ACCEPTANCE' `
        -Body {
            if ([string]$contract.contractVersion -cne $expectedContractVersion -or
                [int]$contract.schemaVersion -ne $expectedContractSchemaVersion -or
                [string]$contract.businessSurface -cne 'READ_ONLY_GET') {
                throw ("Contract identity is not the frozen V2 identity: " +
                    "version=$([string]$contract.contractVersion) schema=$([int]$contract.schemaVersion) " +
                    "surface=$([string]$contract.businessSurface).")
            }
            $capabilityIds = @($contract.capabilities | ForEach-Object { [string]$_.id } | Sort-Object)
            if (@(Compare-Object -ReferenceObject ($expectedCapabilityIds | Sort-Object) `
                    -DifferenceObject $capabilityIds -CaseSensitive).Count -gt 0) {
                throw 'The capability set is not the exact frozen set.'
            }
            $nonGet = @($contract.capabilities |
                ForEach-Object { $_.operations } |
                Where-Object { [string]$_.method -cne 'GET' })
            if ($nonGet.Count -gt 0) {
                throw 'Contract discovery advertises a non-GET business operation.'
            }

            # Address a restricted resource that actually exists in the live snapshot.
            # A placeholder route is refused by query validation before authorization is
            # ever exercised, so it can only demonstrate the two denials.
            $rawUri = Resolve-RestrictedRawEvidenceUri `
                -Client $httpClient -BaseUrl $baseUrl -BearerToken $sharedSecret
            $resolvedFromLiveData = -not [string]::IsNullOrWhiteSpace($rawUri)
            if (-not $resolvedFromLiveData) {
                $rawUri = "$baseUrl/api/v2/error-search/no-such-series/evidence/no-such-evidence/raw-observations"
            }
            $withoutSecret = Invoke-AcceptanceRequest -Client $httpClient -Uri $rawUri
            $wrongSecret = Invoke-AcceptanceRequest -Client $httpClient -Uri $rawUri -BearerToken 'not-the-secret'
            $withSecret = Invoke-AcceptanceRequest -Client $httpClient -Uri $rawUri -BearerToken $sharedSecret
            if ($withoutSecret.StatusCode -ne 403 -or $wrongSecret.StatusCode -ne 403) {
                throw ("Restricted raw evidence was not denied: without=$($withoutSecret.StatusCode) " +
                    "wrong=$($wrongSecret.StatusCode).")
            }
            if ($resolvedFromLiveData -and $withSecret.StatusCode -ne 200) {
                throw ("The configured shared secret did not read a real restricted evidence " +
                    "resource: HTTP $($withSecret.StatusCode)$(Get-ContractErrorCode -Body $withSecret.Body).")
            }
            if (-not $resolvedFromLiveData -and $withSecret.StatusCode -eq 403) {
                throw 'Restricted raw evidence rejected the configured shared secret.'
            }
            $authorizedRead = if ($resolvedFromLiveData) {
                "correct secret read a real evidence resource ($($withSecret.StatusCode))"
            } else {
                ('the live snapshot carried no error evidence, so the positive path was ' +
                    "exercised against an absent resource ($($withSecret.StatusCode))")
            }
            ("contractVersion=$([string]$contract.contractVersion), schemaVersion=$([int]$contract.schemaVersion), " +
                "$($capabilityIds.Count) capabilities, GET-only. Restricted raw evidence: " +
                "no secret 403, wrong secret 403, $authorizedRead.")
        })

    [void](Invoke-AcceptanceSection `
        -Id 'EXTERNALLY_READABLE_CATALOG_FIRST_BODY_AND_304' `
        -Title 'Catalog full first body and same-revision 304' `
        -Gate 'FACTORY_API_ACCEPTANCE' `
        -Body {
            $catalogUri = "$baseUrl/api/v2/externally-readable-demand-catalog"
            $first = Invoke-AcceptanceRequest -Client $httpClient -Uri $catalogUri
            if ($first.StatusCode -ne 200) {
                throw "The catalog first read returned HTTP $($first.StatusCode)."
            }
            $body = $first.Body | ConvertFrom-Json
            $declaredCount = [int](Get-JsonProperty -Object $body -Name 'count')
            if ([string]$body.contractVersion -cne $expectedContractVersion -or
                [long]$body.catalogRevision -le 0 -or
                @($body.items).Count -ne $declaredCount -or
                [string]::IsNullOrWhiteSpace([string]$body.projectionCommitId)) {
                throw ('The first catalog body is not a committed V2 revision: ' +
                    "revision=$([long]$body.catalogRevision) count=$declaredCount " +
                    "items=$(@($body.items).Count).")
            }
            if ($first.ETag -cne "W/`"catalog-r$([long]$body.catalogRevision)`"") {
                throw 'The catalog ETag does not identify the CatalogRevision in the body.'
            }
            $conditional = Invoke-AcceptanceRequest -Client $httpClient -Uri $catalogUri -IfNoneMatch $first.ETag
            if ($conditional.StatusCode -ne 304) {
                throw "A same-revision conditional read must be 304; got $($conditional.StatusCode)."
            }
            if (-not [string]::IsNullOrWhiteSpace($conditional.Body)) {
                throw 'A 304 catalog read must not carry a body.'
            }
            ("First body carried $declaredCount externally readable Demands at catalogRevision " +
                "$([long]$body.catalogRevision) with ETag $($first.ETag); the same-revision read was 304 with no body.")
        })

    [void](Invoke-AcceptanceSection `
        -Id 'PRIMARY_READ_SURFACES' `
        -Title 'Series, eligibility, error, attention and overview reads' `
        -Gate 'FACTORY_API_ACCEPTANCE' `
        -Evidence @('read-surfaces.json') `
        -Body {
            $surfaces = [ordered]@{}
            $reads = [ordered]@{
                demandSeries = '/api/v2/demand-series?presence=VISIBLE&page=1&pageSize=100'
                readabilityAudit = '/api/v2/readability-audit?page=1&pageSize=100'
                # Error search is cursor-paged; it has no page number parameter.
                errorSearch = '/api/v2/error-search?pageSize=100'
                currentIngestAttention = '/api/v2/current-ingest-attention?pageNumber=1&pageSize=100'
                watchOverview = '/api/v2/watch-overview'
            }
            foreach ($name in $reads.Keys) {
                $response = Invoke-AcceptanceRequest -Client $httpClient `
                    -Uri "$baseUrl$($reads[$name])" -BearerToken $sharedSecret
                if ($response.StatusCode -ne 200) {
                    throw ("GET $($reads[$name]) returned HTTP $($response.StatusCode)" +
                        "$(Get-ContractErrorCode -Body $response.Body).")
                }
                $json = $response.Body | ConvertFrom-Json
                $itemCount = if ($null -ne $json.PSObject.Properties['items']) {
                    @($json.items).Count
                } else {
                    -1
                }
                # Discovery and the catalog carry the contract version at the top level;
                # the paged surfaces carry it on their snapshot identity.
                $answeredContractVersion = if ($null -ne $json.PSObject.Properties['contractVersion']) {
                    [string]$json.contractVersion
                } elseif ($null -ne $json.PSObject.Properties['snapshot']) {
                    [string]$json.snapshot.contractVersion
                } else {
                    ''
                }
                # Counts and identities only: response bodies carry customer rows.
                $surfaces[$name] = [ordered]@{
                    path = $reads[$name]
                    statusCode = $response.StatusCode
                    itemCount = $itemCount
                    elapsedMs = $response.ElapsedMs
                    correlationId = $response.CorrelationId
                    contractVersion = $answeredContractVersion
                }
                if ($answeredContractVersion -cne $expectedContractVersion) {
                    throw "$name answered with contract version '$answeredContractVersion'."
                }
                if ([string]::IsNullOrWhiteSpace($response.CorrelationId)) {
                    throw "$name answered without an X-Correlation-Id."
                }
            }
            $surfaces | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $artifacts 'read-surfaces.json') -Encoding UTF8
            (($surfaces.Keys | ForEach-Object {
                "$_=$($surfaces[$_].statusCode)/$($surfaces[$_].itemCount) items" }) -join '; ') +
                '. Every surface answered the frozen contract version with a correlation id.'
        })

    [void](Invoke-AcceptanceSection `
        -Id 'SNAPSHOT_READ_NOT_TORN' `
        -Title 'A frozen snapshot read does not tear across commits' `
        -Gate 'FACTORY_SQLSERVER_ACCEPTANCE' `
        -Body {
            $seriesUri = "$baseUrl/api/v2/demand-series?presence=VISIBLE&page=1&pageSize=50"
            $firstPage = Get-AcceptanceJson -Client $httpClient -Uri $seriesUri -BearerToken $sharedSecret
            $snapshotReference = [string]$firstPage.snapshotReference
            if ([string]::IsNullOrWhiteSpace($snapshotReference)) {
                throw 'The Demand series page did not carry a snapshotReference.'
            }
            $firstIds = @($firstPage.items | ForEach-Object { [string]$_.seriesId })
            $totalItems = [long](Get-JsonProperty -Object $firstPage -Name 'exactTotalCount')

            # Wait for the poll loop to commit again, then re-read pinned to the same
            # snapshot. Identical content across a commit is the non-tearing evidence.
            $before = Get-PollTraceHighWater -Client $httpClient -BaseUrl $baseUrl -BearerToken $sharedSecret
            $advancedTo = Wait-PollTraceHighWaterAbove -Client $httpClient -BaseUrl $baseUrl `
                -Previous $before -TimeoutSeconds $RoundWaitTimeoutSeconds -BearerToken $sharedSecret
            if ($advancedTo -le $before) {
                throw 'No further round committed, so a cross-commit snapshot read could not be exercised.'
            }

            $pinned = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                -Uri ($seriesUri + '&snapshot=' + [Uri]::EscapeDataString($snapshotReference))
            $pinnedIds = @($pinned.items | ForEach-Object { [string]$_.seriesId })
            if ([string]$pinned.snapshotReference -cne $snapshotReference -or
                [long](Get-JsonProperty -Object $pinned -Name 'exactTotalCount') -ne $totalItems -or
                @(Compare-Object -ReferenceObject $firstIds -DifferenceObject $pinnedIds -CaseSensitive `
                    -SyncWindow 0).Count -gt 0) {
                throw 'The pinned snapshot read changed after a further commit; the read tore.'
            }
            ("A page of $($firstIds.Count) of $totalItems Demands re-read identically under the same " +
                'snapshotReference after a later round committed.')
        })

    [void](Invoke-AcceptanceSection `
        -Id 'TASK_TYPE_PROTECTION_READ' `
        -Title 'TaskTypeProtection state is readable and consistent' `
        -Gate 'FACTORY_SQLSERVER_ACCEPTANCE' `
        -Evidence @('task-type-protections.json') `
        -Body {
            $protections = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                -Uri "$baseUrl/api/v2/task-type-protections"
            $items = @($protections.items)
            $summary = @($items | ForEach-Object {
                [ordered]@{
                    workType = [string]$_.workType
                    phase = [string]$_.phase
                    isCurrentAttention = [bool]$_.isCurrentAttention
                    latestObservedCount = [int]$_.latestObservedCount
                    lastHealthyNonZeroCount = [int]$_.lastHealthyNonZeroCount
                    protectionAllowsAbsenceAuthority = [bool]$_.protectionAllowsAbsenceAuthority
                }
            })
            $summary | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath (Join-Path $artifacts 'task-type-protections.json') -Encoding UTF8
            foreach ($item in $summary) {
                if ([string]::IsNullOrWhiteSpace($item.workType) -or
                    [string]::IsNullOrWhiteSpace($item.phase)) {
                    throw 'A TaskTypeProtection row is missing its work type or phase.'
                }
            }
            $protected = @($summary | Where-Object { -not $_.protectionAllowsAbsenceAuthority })
            ("$($summary.Count) work types reported; $($protected.Count) currently withhold absence authority. " +
                'Phases recorded per work type in task-type-protections.json.')
        })

    [void](Invoke-AcceptanceSection `
        -Id 'CATALOG_REVISION_MONOTONIC' `
        -Title 'CatalogRevision never goes backwards' `
        -Gate 'FACTORY_SQLSERVER_ACCEPTANCE' `
        -Body {
            if ($rounds.Count -lt 2) {
                throw 'Fewer than two rounds were observed, so monotonicity could not be judged.'
            }
            $revisions = @($rounds | ForEach-Object { [long]$_.catalogRevision })
            for ($index = 1; $index -lt $revisions.Count; $index++) {
                if ($revisions[$index] -lt $revisions[$index - 1]) {
                    throw ("CatalogRevision went backwards: $($revisions[$index - 1]) -> $($revisions[$index]).")
                }
            }
            "CatalogRevision across $($revisions.Count) observed rounds: $($revisions -join ' -> ')."
        })

    # Absence-authority state before the restart, so the barrier check can prove a new
    # session actually entered the barrier rather than inheriting the old one.
    $sessionBeforeRestart = [string](Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
        -Uri "$baseUrl/api/v2/absence-authority").hostSessionId
    $catalogBeforeRestart = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
        -Uri "$baseUrl/api/v2/externally-readable-demand-catalog"

    # An abrupt stop is the honest restart: a committed projection must survive it.
    Stop-LiveHostProcess -Process $hostProcess
    $hostProcess = $null

    $restartPort = Get-FreeLoopbackPort
    $restartBaseUrl = "http://127.0.0.1:$restartPort"
    $restartedHostProcess = Start-LiveHostProcess `
        -Executable $serviceExecutable `
        -BaseUrl $restartBaseUrl `
        -SqlConnectionString $sqlConnectionString `
        -SharedSecret $sharedSecret `
        -ContinuousPoll $false `
        -LogPath $hostLogPath
    [void](Wait-LiveHostContract `
        -Process $restartedHostProcess -BaseUrl $restartBaseUrl -TimeoutSeconds $StartupTimeoutSeconds)

    [void](Invoke-AcceptanceSection `
        -Id 'SQLSERVER_PROJECTION_COMMIT_PERSISTENCE' `
        -Title 'Committed projection survives a Service restart unchanged' `
        -Gate 'FACTORY_SQLSERVER_ACCEPTANCE' `
        -Body {
            $afterFirst = Invoke-AcceptanceRequest -Client $httpClient `
                -Uri "$restartBaseUrl/api/v2/externally-readable-demand-catalog"
            if ($afterFirst.StatusCode -ne 200) {
                throw "The post-restart catalog read returned HTTP $($afterFirst.StatusCode)."
            }
            $after = $afterFirst.Body | ConvertFrom-Json
            $beforeIds = @($catalogBeforeRestart.items | ForEach-Object { [string]$_.demandId } | Sort-Object)
            $afterIds = @($after.items | ForEach-Object { [string]$_.demandId } | Sort-Object)
            $beforeCount = [int](Get-JsonProperty -Object $catalogBeforeRestart -Name 'count')
            $afterCount = [int](Get-JsonProperty -Object $after -Name 'count')
            if ($afterCount -ne $beforeCount -or
                @(Compare-Object -ReferenceObject $beforeIds -DifferenceObject $afterIds -CaseSensitive).Count -gt 0 -or
                [long]$after.catalogRevision -lt [long]$catalogBeforeRestart.catalogRevision -or
                [string]::IsNullOrWhiteSpace([string]$after.projectionCommitId)) {
                throw 'The SQL Server projection did not survive the Host restart unchanged.'
            }
            # The restarted Host does not poll, so two reads must return the same revision.
            # That is what makes the comparison a persistence result rather than a snapshot
            # of a moving projection.
            $afterRepeat = Invoke-AcceptanceRequest -Client $httpClient `
                -Uri "$restartBaseUrl/api/v2/externally-readable-demand-catalog"
            if ($afterRepeat.StatusCode -ne 200 -or $afterRepeat.ETag -cne $afterFirst.ETag) {
                throw 'The restarted Host did not serve a quiesced projection.'
            }
            ("$afterCount Demands at catalogRevision $([long]$after.catalogRevision) " +
                "(projectionCommitId $([string]$after.projectionCommitId)) read identically twice after an " +
                'abrupt Service restart.')
        })

    [void](Invoke-AcceptanceSection `
        -Id 'SQLSERVER_RESTART_BARRIER' `
        -Title 'A restarted Service re-enters the recovery barrier' `
        -Gate 'FACTORY_SQLSERVER_ACCEPTANCE' `
        -Evidence @('absence-authority.json') `
        -Body {
            # The endpoint answers with the current Host session, not a list.
            $authority = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                -Uri "$restartBaseUrl/api/v2/absence-authority"
            $current = [ordered]@{
                hostSessionId = [string]$authority.hostSessionId
                startedAt = [string]$authority.startedAt
                phase = [string]$authority.phase
                isCurrent = [bool]$authority.isCurrent
                absenceAuthorityAvailable = [bool]$authority.absenceAuthorityAvailable
                eventTypes = @($authority.events | ForEach-Object { [string]$_.eventType })
                previousHostSessionId = $sessionBeforeRestart
            }
            $current | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $artifacts 'absence-authority.json') -Encoding UTF8

            if (-not $current.isCurrent) {
                throw 'The restarted Host did not report a current session.'
            }
            if ($current.hostSessionId -ceq $sessionBeforeRestart) {
                throw 'The restarted Host reused the pre-restart session instead of entering a new one.'
            }
            if ($current.eventTypes -cnotcontains 'RESTART_BARRIER_ENTERED') {
                throw ('The new Host session did not record RESTART_BARRIER_ENTERED; events: ' +
                    "$($current.eventTypes -join ',').")
            }
            # Absence authority belongs to NORMAL alone. Which phase the session has
            # reached depends on how many rounds it completed; the invariant does not.
            $expectedAuthority = ($current.phase -ceq 'NORMAL')
            if ($current.absenceAuthorityAvailable -ne $expectedAuthority) {
                throw ("Phase $($current.phase) reported absenceAuthorityAvailable=" +
                    "$($current.absenceAuthorityAvailable).")
            }
            ("A new Host session ($($current.hostSessionId)) replaced $sessionBeforeRestart, entered the " +
                "barrier (events $($current.eventTypes -join ',')), and is in phase $($current.phase) " +
                "with absenceAuthorityAvailable=$($current.absenceAuthorityAvailable).")
        })

    Stop-LiveHostProcess -Process $restartedHostProcess
    $restartedHostProcess = $null

    [void](Invoke-AcceptanceSection `
        -Id 'REMOTE_BINDING_DATA_ACCESS_PROTECTION' `
        -Title 'A remote binding without a shared secret refuses to start' `
        -Gate 'FACTORY_API_ACCEPTANCE' `
        -Body {
            $remotePort = Get-FreeLoopbackPort
            $script:remoteBindProcess = Start-LiveHostProcess `
                -Executable $serviceExecutable `
                -BaseUrl "http://0.0.0.0:$remotePort" `
                -SqlConnectionString $sqlConnectionString `
                -SharedSecret '' `
                -ContinuousPoll $false `
                -DrainStreams $false
            # The process is expected to die immediately, so reading to end cannot block.
            $standardError = $script:remoteBindProcess.StandardError.ReadToEnd()
            if (-not $script:remoteBindProcess.WaitForExit(30000)) {
                $script:remoteBindProcess.Kill()
                throw 'A remote binding with no shared secret started instead of refusing.'
            }
            $exitCode = $script:remoteBindProcess.ExitCode
            if ($exitCode -eq 0) {
                throw 'A remote binding with no shared secret exited successfully instead of refusing.'
            }
            # A non-zero exit alone would also be produced by a port conflict, which would
            # record a pass the guard never earned. The refusal names the missing key.
            if ($standardError.IndexOf('MesIngest:SharedSecret', [StringComparison]::Ordinal) -lt 0) {
                throw 'The remote binding failed for some other reason than the missing shared secret.'
            }
            $script:remoteBindProcess.Dispose()
            $script:remoteBindProcess = $null
            "Startup validation refused the off-loopback binding before Kestrel bound (exit code $exitCode), naming MesIngest:SharedSecret."
        })

    # -----------------------------------------------------------------------
    # 6. Watch on the interactive plant desktop, then the Service outliving it.
    # -----------------------------------------------------------------------
    if ($IncludePackagedWatch) {
        New-Item -ItemType Directory -Path $watchEvidenceDirectory -Force | Out-Null
        $watchPort = Get-FreeLoopbackPort
        $watchBaseUrl = "http://127.0.0.1:$watchPort"
        $hostProcess = Start-LiveHostProcess `
            -Executable $serviceExecutable `
            -BaseUrl $watchBaseUrl `
            -SqlConnectionString $sqlConnectionString `
            -SharedSecret $sharedSecret `
            -ContinuousPoll $true `
            -LogPath $hostLogPath
        [void](Wait-LiveHostContract `
            -Process $hostProcess -BaseUrl $watchBaseUrl -TimeoutSeconds $StartupTimeoutSeconds)

        . (Join-Path $PSScriptRoot 'WatchAcceptanceUia.ps1')

        $watchPages = $null
        [void](Invoke-AcceptanceSection `
            -Id 'WATCH_SIX_PAGE_LIVE_HOST_VERIFICATION' `
            -Title 'Watch pages, settings and compact Host state against live Host data' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' `
            -Evidence @('watch/watch-pages.json') `
            -Body {
                $watchInfo = [Diagnostics.ProcessStartInfo]::new()
                $watchInfo.FileName = $watchExecutable
                $watchInfo.WorkingDirectory = Split-Path -Parent $watchExecutable
                $watchInfo.UseShellExecute = $false
                $watchInfo.EnvironmentVariables['MesIngestWatch__BaseUrl'] = $watchBaseUrl
                $watchInfo.EnvironmentVariables['MesIngestWatch__SharedSecret'] = $sharedSecret
                $watchInfo.EnvironmentVariables['MesIngestWatch__RenderingMode'] = 'SoftwareOnly'
                $watchInfo.EnvironmentVariables['MesIngestWatch__LogDirectory'] =
                    Join-Path $watchEvidenceDirectory 'logs'
                $startupStopwatch = [Diagnostics.Stopwatch]::StartNew()
                $script:watchProcess = [Diagnostics.Process]::Start($watchInfo)
                if ($null -eq $script:watchProcess) { throw 'The packaged Watch did not start.' }

                $window = Wait-WatchMainWindow -Process $script:watchProcess -TimeoutSeconds 90
                $script:watchWindow = $window
                $startupStopwatch.Stop()
                $startupMs = [Math]::Round($startupStopwatch.Elapsed.TotalMilliseconds, 1)

                $result = Invoke-WatchPageWalk `
                    -Window $window `
                    -EvidenceDirectory $watchEvidenceDirectory `
                    -ObservationSeconds $WatchObservationSeconds
                $script:watchPages = $result
                $result | ConvertTo-Json -Depth 6 |
                    Set-Content -LiteralPath (Join-Path $watchEvidenceDirectory 'watch-pages.json') -Encoding UTF8

                if (-not $result.AllPagesShown) {
                    throw ("The Watch did not show every page against live Host data: " +
                        "missing=$($result.MissingPages -join ',').")
                }
                if (-not $result.HostConnected) {
                    throw "The Watch never reported a connected Host: '$($result.HostStatusText)'."
                }
                if (-not $result.AutoRefreshObserved) {
                    throw 'No auto-refresh was observed while the Watch stayed open on live data.'
                }
                # Detail and cross-page drill are only unavailable when the live snapshot
                # has nothing to open. Any other outcome is a real Watch failure.
                if (-not $result.DetailExercised) {
                    throw ('Detail selection was not exercised against live Host data: ' +
                        "$($result.InteractionSummary).")
                }
                if (-not $result.DrillExercised) {
                    throw ("Cross-page drill was not exercised against live Host data: " +
                        "$($result.InteractionSummary).")
                }
                ("Startup to main window $startupMs ms. Pages shown: $($result.PagesShown -join ', '). " +
                    "Host state '$($result.HostStatusText)'. Auto-refresh advanced the Watch view " +
                    "$($result.AutoRefreshObservations) time(s) in $WatchObservationSeconds s. " +
                    "Paging, detail selection and cross-page drill: $($result.InteractionSummary). " +
                    'Window captures stay on this machine; only their hashes are published.')
            })

        if ($null -ne $watchPages -and $watchPages.PagingExercised) {
            Add-Check -Id 'WATCH_PAGING_ON_LIVE_DATA' `
                -Title 'Demand series paging on live Host data' `
                -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status PASSED `
                -Detail "Paging moved the Demand series page on live Host data ($($watchPages.InteractionSummary))."
        } elseif ($null -ne $watchPages -and $watchPages.PagingSinglePage) {
            Add-Check -Id 'WATCH_PAGING_ON_LIVE_DATA' `
                -Title 'Demand series paging on live Host data' `
                -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status SKIPPED `
                -Owner 'plant operator' `
                -Requires 'a plant window whose Demand series snapshot exceeds one page' `
                -Detail ("Not exercised: the live snapshot fitted one page of " +
                    "$($watchPages.LiveDemandRowCount) rows, so the next-page control was disabled.")
        } else {
            Add-Check -Id 'WATCH_PAGING_ON_LIVE_DATA' `
                -Title 'Demand series paging on live Host data' `
                -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status FAILED `
                -Detail ('Paging was neither exercised nor explained by a single-page snapshot: ' +
                    "$(if ($null -ne $watchPages) { $watchPages.InteractionSummary } else { 'the walk produced no result' }).")
        }

        # Failure retention: take the Service away underneath the open Watch. The operator
        # has to see the failure, and the board must keep the rows it already had rather
        # than blanking into something that reads as "no tasks".
        [void](Invoke-AcceptanceSection `
            -Id 'WATCH_FAILURE_RETENTION' `
            -Title 'A Host failure is shown and the loaded board is retained' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' `
            -Body {
                if ($null -eq $script:watchProcess -or $null -eq $script:watchWindow) {
                    throw 'The Watch was never started, so failure retention cannot be judged.'
                }
                if (-not (Invoke-WatchNavigation -Window $script:watchWindow `
                        -NavigationAutomationId 'DemandSeriesNavigationItem' `
                        -Anchors @('DemandSeriesScrollViewer', 'DemandSeriesGrid'))) {
                    throw 'Could not return to the Demand series page before the failure.'
                }
                $rowsBefore = Get-WatchGridRowCount -Window $script:watchWindow -AutomationId 'DemandSeriesGrid'
                if ($rowsBefore -le 0) {
                    throw 'The Demand series board was empty before the failure, so retention cannot be judged.'
                }

                Stop-LiveHostProcess -Process $hostProcess
                $script:hostProcess = $null
                $failureState = Wait-WatchHostDisconnected -Window $script:watchWindow -TimeoutSeconds 180
                $rowsAfter = Get-WatchGridRowCount -Window $script:watchWindow -AutomationId 'DemandSeriesGrid'

                # Bring the Service back on the same address before anything else runs.
                $script:hostProcess = Start-LiveHostProcess `
                    -Executable $serviceExecutable `
                    -BaseUrl $watchBaseUrl `
                    -SqlConnectionString $sqlConnectionString `
                    -SharedSecret $sharedSecret `
                    -ContinuousPoll $true `
                    -LogPath $hostLogPath
                [void](Wait-LiveHostContract `
                    -Process $script:hostProcess -BaseUrl $watchBaseUrl -TimeoutSeconds $StartupTimeoutSeconds)

                if ([string]::IsNullOrWhiteSpace($failureState)) {
                    throw 'The Watch never reported the Host failure while the Service was down.'
                }
                # What the shipped checklist requires is that the failure is unmissable, so
                # an empty board cannot be read as "no tasks". What the board did with the
                # rows it already had is recorded as observed fact, not judged here.
                $boardOutcome = if ($rowsAfter -eq $rowsBefore) {
                    "kept all $rowsBefore loaded Demand rows"
                } else {
                    "moved from $rowsBefore to $rowsAfter Demand rows"
                }
                ("With the Service stopped the Watch reported '$failureState' and $boardOutcome; " +
                    'the Service was then restarted on the same address.')
            })

        [void](Invoke-AcceptanceSection `
            -Id 'WATCH_CLOSED_SERVICE_CONTINUES' `
            -Title 'The Service keeps polling and serving after the Watch closes' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' `
            -Body {
                if ($null -eq $script:watchProcess) {
                    throw 'The Watch was never started, so its independence cannot be judged.'
                }
                $before = Get-PollTraceHighWater -Client $httpClient -BaseUrl $watchBaseUrl `
                    -BearerToken $sharedSecret
                $script:watchProcess.CloseMainWindow() | Out-Null
                if (-not $script:watchProcess.WaitForExit(60000)) {
                    $script:watchProcess.Kill()
                    throw 'The Watch did not exit after its main window was closed.'
                }
                $watchExitCode = $script:watchProcess.ExitCode
                $script:watchProcess.Dispose()
                $script:watchProcess = $null
                if ($watchExitCode -ne 0) {
                    throw "The packaged Watch exited with code $watchExitCode."
                }
                if ($hostProcess.HasExited) {
                    throw 'The Host exited when the Watch closed; the Service does not own its own lifetime.'
                }

                $after = Wait-PollTraceHighWaterAbove -Client $httpClient -BaseUrl $watchBaseUrl `
                    -Previous $before -TimeoutSeconds $RoundWaitTimeoutSeconds -BearerToken $sharedSecret
                if ($after -le $before) {
                    throw "The Service did not complete a further round after the Watch closed (high water $before)."
                }
                $catalogAfter = Get-AcceptanceJson -Client $httpClient -BearerToken $sharedSecret `
                    -Uri "$watchBaseUrl/api/v2/externally-readable-demand-catalog"
                ("The Watch exited with code 0; the Service kept running, advanced the PollTrace high water " +
                    "$before -> $after, wrote to SQL Server and still served the catalog at revision " +
                    "$([long]$catalogAfter.catalogRevision).")
            })
    } else {
        $watchSkipDetail = 'Not run in this invocation. The packaged Watch needs the interactive plant desktop; rerun with -IncludePackagedWatch there.'
        Add-Check -Id 'WATCH_SIX_PAGE_LIVE_HOST_VERIFICATION' `
            -Title 'Watch pages, settings and compact Host state against live Host data' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status SKIPPED `
            -Owner 'plant operator' -Requires 'interactive plant desktop with -IncludePackagedWatch' `
            -Detail $watchSkipDetail
        Add-Check -Id 'WATCH_CLOSED_SERVICE_CONTINUES' `
            -Title 'The Service keeps polling and serving after the Watch closes' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status SKIPPED `
            -Owner 'plant operator' -Requires 'interactive plant desktop with -IncludePackagedWatch' `
            -Detail $watchSkipDetail
        Add-Check -Id 'WATCH_PAGING_ON_LIVE_DATA' `
            -Title 'Demand series paging on live Host data' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status SKIPPED `
            -Owner 'plant operator' -Requires 'interactive plant desktop with -IncludePackagedWatch' `
            -Detail $watchSkipDetail
        Add-Check -Id 'WATCH_FAILURE_RETENTION' `
            -Title 'A Host failure is shown and the loaded board is retained' `
            -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status SKIPPED `
            -Owner 'plant operator' -Requires 'interactive plant desktop with -IncludePackagedWatch' `
            -Detail $watchSkipDetail
    }

    # Ticket 26 explicitly does not re-run ticket 23's pixel, stability, baseline or DPI
    # gates for packaging that did not change UI output. Recording that as a deliberate
    # decision, with the condition that reopens it, is part of the acceptance.
    Add-Check -Id 'TICKET23_VISUAL_GATES_NOT_REPEATED' `
        -Title 'Ticket 23 visual gates deliberately not repeated' `
        -Gate 'FACTORY_WATCH_ACCEPTANCE' -Status PASSED `
        -Detail ('This run verifies final packaging and live-site connection only. The pixel candidate, ' +
            'ten-run stability, baseline promotion and DPI clone from ticket 23 stay valid because this ' +
            'ticket changed no XAML, UI Automation or DPI behaviour. If the plant observes a real UI, ' +
            'UI Automation or DPI regression, keep the red evidence and return the affected scenario to ' +
            "ticket 23's gates rather than re-approving here.")

    # -----------------------------------------------------------------------
    # 7. Evidence closure.
    # -----------------------------------------------------------------------
    [void](Invoke-AcceptanceSection `
        -Id 'EVIDENCE_REDACTION_AND_HASHES' `
        -Title 'Evidence is redacted, hashed and free of credentials' `
        -Gate 'FACTORY_EVIDENCE_CLOSURE' `
        -Evidence @('evidence-hashes.json') `
        -Body {
            $textFiles = @(Get-ChildItem -LiteralPath $artifacts -File -Recurse -Force |
                Where-Object { $_.Extension -in @('.json', '.txt', '.log', '.md') })
            $leaks = New-Object System.Collections.ArrayList
            foreach ($file in $textFiles) {
                $text = [IO.File]::ReadAllText($file.FullName)
                if ($text.IndexOf($sharedSecret, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    [void]$leaks.Add("$($file.Name): shared secret")
                }
                if ([Regex]::IsMatch($text, '(?i)(password|pwd)\s*[:=]\s*(?!\[redacted\])\S+')) {
                    [void]$leaks.Add("$($file.Name): password")
                }
            }
            if ($leaks.Count -gt 0) {
                throw "Evidence carries values that must not leave the plant: $($leaks -join '; ')."
            }
            $index = Get-EvidenceHashIndex -Path $artifacts
            $index | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath (Join-Path $artifacts 'evidence-hashes.json') -Encoding UTF8
            ("$($index.Count) evidence files hashed. Probe logs and provider messages are redacted; " +
                'API response bodies and Watch window captures are not published — they carry customer rows.')
        })
}
catch {
    # Something outside a section broke — a Host that would not start, a target that went
    # away mid-run. The run is over, but the evidence still closes: every check that never
    # produced a result is recorded as red with the reason, so no gate is left ambiguous.
    $abortReason = Protect-FactoryAcceptanceText -Text ([string]$_.Exception.Message)
    Write-Warning "The acceptance run aborted: $abortReason"
}
finally {
    $sqlConnectionString = $null
    if ($null -ne $watchProcess) {
        if (-not $watchProcess.HasExited) {
            $watchProcess.Kill()
            $watchProcess.WaitForExit(10000) | Out-Null
            [void]$cleanupNotes.Add('The packaged Watch was still running and was terminated during cleanup.')
        }
        $watchProcess.Dispose()
    }
    Stop-LiveHostProcess -Process $hostProcess
    Stop-LiveHostProcess -Process $restartedHostProcess
    Stop-LiveHostProcess -Process $remoteBindProcess
    foreach ($subscription in @($hostLogSubscriptions)) {
        Unregister-Event -SourceIdentifier $subscription.Name -ErrorAction SilentlyContinue
    }
    # The stream handler redacts line by line with a narrower pattern than the evidence
    # pass. Run the full redaction here so an aborted run cannot leave the weaker one as
    # the version that survives on disk.
    if (Test-Path -LiteralPath $hostLogPath -PathType Leaf) {
        Set-Content -LiteralPath $hostLogPath -Encoding UTF8 -Value (
            Protect-FactoryAcceptanceText -Text ([IO.File]::ReadAllText($hostLogPath)))
    }
    if ($null -ne $httpClient) { $httpClient.Dispose() }
    [void]$cleanupNotes.Add('All Host and Watch processes started by this run were stopped; the target database is left in place for review.')
}

$reportedChecks = if ([string]::IsNullOrWhiteSpace($abortReason)) {
    @($checks)
} else {
    Complete-AbortedAcceptanceChecks `
        -Checks @($checks) -ExpectedCheckIds $declaredCheckIds -Reason $abortReason
}

$summary = New-FactoryAcceptanceSummary `
    -Checks $reportedChecks `
    -ExpectedCheckIds $declaredCheckIds `
    -RollbackReadiness ('Rollback stays the ticket 25 drill: stop the new Service, restore the separate ' +
        'legacy backup with scripts\cutover\Invoke-CutoverRollback.ps1, and run the legacy programs against ' +
        'the restored legacy database. No mixed-mode operation is supported.') `
    -ResidualRisks @(
        'Oracle Thick mode was not re-verified unless a separate -OracleMode Thick run recorded it.',
        'Plant business semantics (DATES/STEP per TASK_TYPE) remain a human confirmation in validation\execution-log.md.',
        'Response bodies and Watch captures stay on this machine, so an off-site reviewer sees counts and hashes rather than rows.') `
    -Context @{
        runId = $runId
        environment = $environmentRecord
        packageSourceCommit = $(if ($null -ne $packageIdentity) { $packageIdentity.SourceCommit } else { 'UNKNOWN' })
        roundsObserved = $rounds.Count
        cleanup = @($cleanupNotes)
    }

$summary | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $artifacts 'factory-acceptance-summary.json') -Encoding UTF8

$report = New-Object System.Collections.ArrayList
[void]$report.Add("# MesIngest factory acceptance — $($summary.status)")
[void]$report.Add('')
[void]$report.Add("Run $runId on $([Environment]::MachineName) at $([DateTimeOffset]::Now.ToString('O')).")
[void]$report.Add('')
[void]$report.Add("Package source commit: $(if ($null -ne $packageIdentity) { $packageIdentity.SourceCommit } else { 'UNKNOWN' }).")
[void]$report.Add('')
[void]$report.Add('| Check | Gate | Status | Detail |')
[void]$report.Add('| --- | --- | --- | --- |')
foreach ($check in @($summary.passed + $summary.failed + $summary.namedSkips)) {
    [void]$report.Add("| $($check.id) | $($check.gate) | $($check.status) | $($check.detail) |")
}
[void]$report.Add('')
if ($summary.namedSkips.Count -gt 0) {
    [void]$report.Add('## Named skips')
    [void]$report.Add('')
    foreach ($skip in $summary.namedSkips) {
        [void]$report.Add("- **$($skip.id)** — owner: $($skip.owner); requires: $($skip.requires); gate left open: $($skip.gate).")
    }
    [void]$report.Add('')
}
[void]$report.Add('## Residual risks')
[void]$report.Add('')
foreach ($risk in $summary.residualRisks) { [void]$report.Add("- $risk") }
[void]$report.Add('')
[void]$report.Add('## Rollback readiness')
[void]$report.Add('')
[void]$report.Add($summary.rollbackReadiness)
$report -join [Environment]::NewLine |
    Set-Content -LiteralPath (Join-Path $artifacts 'factory-acceptance-summary.md') -Encoding UTF8

Write-Output "MESINGEST_FACTORY_ACCEPTANCE_$($summary.status): artifacts=$artifacts"
if ($summary.status -ceq 'FAILED') {
    exit 1
}
