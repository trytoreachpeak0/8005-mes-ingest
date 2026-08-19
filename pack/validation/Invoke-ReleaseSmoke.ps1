#Requires -Version 5.1
<#
.SYNOPSIS
  Production V2 release smoke for a built MesIngest install package.

.DESCRIPTION
  Starts the packaged Host against a dedicated, disposable, empty SQL Server
  database and proves the frozen /api/v2 contract from the published binaries.

  Rounds are driven from a recording written by this script, so the smoke is
  repeatable with no factory Oracle. The recording replaces only the Oracle
  statement result: the canonical query artifact, the production round source,
  and the projection commit boundary are the shipped production code. Recorded
  rounds are never factory acceptance evidence — the Host reports driver
  FILE_REPLAY and refuses --probe-oracle while a recording is configured.

.PARAMETER IncludePackagedWatch
  Also start and close the packaged WPF Watch to prove the Service keeps polling
  and serving after the Watch exits. Requires an interactive Windows desktop, so
  it belongs to the gpt_win11 interactive scheduled task; without it that check
  is recorded as the named skip PACKAGED_WATCH_PROCESS_INDEPENDENCE.
#>
[CmdletBinding()]
param(
    [string] $ArtifactsDirectory,

    [ValidateRange(5, 30)]
    [int] $StartupTimeoutSeconds = 10,

    [switch] $IncludePackagedWatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HttpStatus {
    param([Parameter(Mandatory = $true)][string] $Uri)

    try {
        $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 2 -UseBasicParsing
        return [int]$response.StatusCode
    } catch {
        if ($null -eq $_.Exception.Response) {
            throw
        }
        return [int]$_.Exception.Response.StatusCode
    }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
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

# One GET with full control over conditional and authorization headers. Invoke-WebRequest
# cannot express "read the 304 and its ETag", which the catalog contract requires.
function Invoke-SmokeRequest {
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
        $response = $Client.SendAsync($message).GetAwaiter().GetResult()
        try {
            [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                ETag = if ($null -ne $response.Headers.ETag) { $response.Headers.ETag.ToString() } else { $null }
                Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        } finally {
            $response.Dispose()
        }
    } finally {
        $message.Dispose()
    }
}

function Start-PackagedHostProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $SqlConnectionString,
        [Parameter(Mandatory = $true)][string] $SharedSecret,
        [string] $RecordingPath,
        [bool] $ContinuousPoll = $false,
        [bool] $OneShotOnStartup = $false,
        # A long-lived Host must have both streams drained or a full pipe blocks it.
        # A start that is expected to refuse immediately leaves stderr for the caller,
        # so the refusal reason can be asserted instead of only the exit code.
        [bool] $DrainStreams = $true
    )

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = Split-Path -Parent $Executable
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    $info.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $info.EnvironmentVariables.Remove('MES_INGEST_RELEASE_SMOKE_SQLSERVER')
    $info.EnvironmentVariables.Remove('MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED')
    $info.EnvironmentVariables.Remove('MES_INGEST_SQLSERVER')
    $info.EnvironmentVariables['MesIngest__NewSqlServerConnectionString'] = $SqlConnectionString
    $info.EnvironmentVariables['MesIngest__SnapshotSource'] = 'Oracle'
    $info.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] =
        $(if ($OneShotOnStartup) { 'true' } else { 'false' })
    $info.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] =
        $(if ($ContinuousPoll) { 'true' } else { 'false' })
    $info.EnvironmentVariables['MesIngest__PostPollDelaySeconds'] = '1'
    $info.EnvironmentVariables['MesIngest__GoLiveBaseline'] = '2026-01-01T00:00:00+08:00'
    # Request the development-only switch deliberately. Production must suppress
    # it, otherwise this build cannot claim the ADR-mes-0017 production cutover.
    $info.EnvironmentVariables['MesIngest__EnableLegacyDevelopmentEndpoints'] = 'true'
    $info.EnvironmentVariables['MesIngest__Urls'] = $BaseUrl
    $info.EnvironmentVariables['MesIngest__SharedSecret'] = $SharedSecret
    if (-not [string]::IsNullOrWhiteSpace($RecordingPath)) {
        $info.EnvironmentVariables['MesIngest__ReplayRoundsFromRecordingPath'] = $RecordingPath
        $info.EnvironmentVariables['MesIngest__ReplayRoundsAcknowledgement'] =
            'RELEASE_SMOKE_NOT_FACTORY_EVIDENCE'
    }

    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'Packaged Production V2 Host did not start.' }
    if ($DrainStreams) {
        # Drain both streams to prevent a full pipe from blocking the Host, but never persist
        # provider output: SQL/Oracle failures can include datasource or credential details.
        $process.BeginOutputReadLine()
        $process.BeginErrorReadLine()
    }
    return $process
}

function Wait-PackagedHostContract {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $contract = $null
    do {
        if ($Process.HasExited) {
            throw "Packaged Production V2 Host exited during startup with code $($Process.ExitCode)."
        }
        try {
            $contract = Invoke-RestMethod -Uri "$BaseUrl/api/v2/contract" -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 200
        }
    } while ($null -eq $contract -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $contract) {
        throw "Packaged Production V2 Host did not expose /api/v2/contract within $TimeoutSeconds seconds."
    }
    return $contract
}

function Get-PollTraceHighWater {
    param([Parameter(Mandatory = $true)][string] $BaseUrl)

    $attention = Invoke-RestMethod `
        -Uri "$BaseUrl/api/v2/current-ingest-attention?pageNumber=1&pageSize=1" `
        -TimeoutSec 5
    return [long]$attention.snapshot.pollTraceHighWater
}

function Wait-PollTraceHighWaterAbove {
    param(
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][long] $Previous,
        [int] $TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = Get-PollTraceHighWater -BaseUrl $BaseUrl
        if ($current -gt $Previous) { return $current }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "PollTrace high-water did not advance beyond $Previous within $TimeoutSeconds seconds."
}

# ConvertFrom-Json objects also carry PowerShell's intrinsic Count, so a contract
# field literally named "count" has to be read from the JSON properties.
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

function Stop-PackagedHostProcess {
    param([Diagnostics.Process] $Process)

    if ($null -eq $Process) { return }
    if (-not $Process.HasExited) {
        $Process.Kill()
        $Process.WaitForExit(5000) | Out-Null
    }
    $Process.Dispose()
}

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serviceExecutable = Join-Path $packageRoot 'service\MesIngest.Host.exe'
$watchExecutable = Join-Path $packageRoot 'watch\MesIngest.Watch.exe'
$releaseManifestPath = Join-Path $packageRoot 'RELEASE-MANIFEST.json'
$canonicalOpenApiRelativePath = 'openapi/v2.json'
$canonicalOpenApiPath = Join-Path $packageRoot $canonicalOpenApiRelativePath
$expectedContractVersion = '2026.08.new-mes-ingest.v2.0'
$expectedContractSchemaVersion = 17
$expectedCompatibilityPolicy = 'EXACT_VERSION_SCHEMA_AND_CAPABILITIES'
$expectedCapabilityVersion = '1.0'
$expectedCapabilityOperations = [ordered]@{
    CONTRACT_DISCOVERY = @('/api/v2/contract')
    CURRENT_INGEST_ATTENTION = @('/api/v2/current-ingest-attention')
    DEMAND_SERIES = @(
        '/api/v2/demand-series',
        '/api/v2/demand-series/by-key',
        '/api/v2/demand-series/{seriesId}'
    )
    ERROR_SEARCH = @(
        '/api/v2/error-search',
        '/api/v2/error-search/{seriesId}',
        '/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations'
    )
    EXTERNALLY_READABLE_DEMAND_CATALOG = @('/api/v2/externally-readable-demand-catalog')
    POLL_HEALTH_AND_EVIDENCE = @(
        '/api/v2/poll-traces/{pollTraceId}',
        '/api/v2/absence-authority',
        '/api/v2/absence-authority/{hostSessionId}',
        '/api/v2/task-type-protections',
        '/api/v2/task-type-protections/{workType}'
    )
    READABILITY_AUDIT = @(
        '/api/v2/readability-audit',
        '/api/v2/readability-audit/{demandId}'
    )
    SERIES_ERROR_CATALOG = @('/api/v2/contract')
    WATCH_OVERVIEW = @('/api/v2/watch-overview')
}
$expectedCapabilityIds = @($expectedCapabilityOperations.Keys)
$expectedOpenApiPaths = @(
    $expectedCapabilityOperations.Values |
        ForEach-Object { $_ } |
        Sort-Object -Unique
)
$canonicalQueryRelativePath = 'service/queries/mes-task-union/query.sql'
$canonicalQueryManifestRelativePath = 'service/queries/mes-task-union/query.manifest.json'
$canonicalQuerySha256 = '54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae'
$canonicalQueryVersion = "MES_TASK_UNION/sha256:$canonicalQuerySha256"
$canonicalQueryPath = Join-Path $packageRoot $canonicalQueryRelativePath
$canonicalQueryManifestPath = Join-Path $packageRoot $canonicalQueryManifestRelativePath

foreach ($path in @(
    $serviceExecutable,
    $releaseManifestPath,
    $canonicalOpenApiPath,
    $canonicalQueryPath,
    $canonicalQueryManifestPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Production V2 release smoke input missing: $path"
    }
}
if ($IncludePackagedWatch) {
    if (-not (Test-Path -LiteralPath $watchExecutable -PathType Leaf)) {
        throw "IncludePackagedWatch requires the packaged Watch: $watchExecutable"
    }
    if (-not [Environment]::UserInteractive) {
        throw 'IncludePackagedWatch needs the interactive golden desktop; run it through the gpt_win11 scheduled task.'
    }
}

try {
    $releaseManifest = Get-Content -Raw -LiteralPath $releaseManifestPath | ConvertFrom-Json
    $canonicalOpenApi = Get-Content -Raw -LiteralPath $canonicalOpenApiPath | ConvertFrom-Json
} catch {
    throw 'Production V2 release smoke requires valid RELEASE-MANIFEST.json and canonical openapi/v2.json.'
}
$canonicalOpenApiHash = (
    Get-FileHash -LiteralPath $canonicalOpenApiPath -Algorithm SHA256
).Hash.ToLowerInvariant()
if (
    [int]$releaseManifest.schemaVersion -ne 3 -or
    [string]$releaseManifest.validationStatus -cne 'PASSED' -or
    [string]$releaseManifest.openApiStatus -cne 'FROZEN' -or
    [string]$releaseManifest.openApi.path -cne $canonicalOpenApiRelativePath -or
    [string]$releaseManifest.openApi.contractVersion -cne $expectedContractVersion -or
    [int]$releaseManifest.openApi.schemaVersion -ne $expectedContractSchemaVersion -or
    ([string]$releaseManifest.openApi.sha256).ToLowerInvariant() -cne $canonicalOpenApiHash
) {
    throw 'Production V2 release manifest does not claim the exact frozen OpenAPI identity and SHA-256.'
}
if (
    [string]$canonicalOpenApi.info.version -cne $expectedContractVersion -or
    $null -eq $canonicalOpenApi.paths
) {
    throw 'Packaged canonical OpenAPI contract identity is not the frozen V2 identity.'
}

# Service and Watch must implement one versioned contract. NewMesIngestContract lives in
# MesIngest.Core, so both published copies have to be the byte-identical assembly the
# release manifest recorded.
$sharedContractAssembly = [string]$releaseManifest.sharedContract.assembly
$sharedContractSha256 = ([string]$releaseManifest.sharedContract.sha256).ToLowerInvariant()
if ($sharedContractAssembly -cne 'MesIngest.Core.dll') {
    throw 'Release manifest does not name the shared versioned contract assembly.'
}
if (-not $releaseManifest.sharedContract.serviceAndWatchIdentical) {
    throw 'A package published with -SkipWatch cannot be release-smoked: the smoke has to prove Service and Watch share one versioned contract.'
}
foreach ($contractHost in @('service', 'watch')) {
    $contractAssemblyPath = Join-Path $packageRoot (Join-Path $contractHost $sharedContractAssembly)
    if (-not (Test-Path -LiteralPath $contractAssemblyPath -PathType Leaf)) {
        throw "Packaged $contractHost is missing the shared contract assembly $sharedContractAssembly."
    }
    $actualContractSha256 = (
        Get-FileHash -LiteralPath $contractAssemblyPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualContractSha256 -cne $sharedContractSha256) {
        throw "Packaged $contractHost does not carry the shared versioned contract assembly from the release manifest."
    }
}
$packagedOpenApiPaths = @(
    $canonicalOpenApi.paths.PSObject.Properties |
        ForEach-Object { $_.Name } |
        Sort-Object
)
if (@(
    Compare-Object -ReferenceObject $expectedOpenApiPaths -DifferenceObject $packagedOpenApiPaths -CaseSensitive
).Count -gt 0) {
    throw 'Packaged canonical OpenAPI paths differ from the frozen V2 surface.'
}
foreach ($pathProperty in $canonicalOpenApi.paths.PSObject.Properties) {
    $members = @($pathProperty.Value.PSObject.Properties | ForEach-Object { $_.Name })
    if ($members.Count -ne 1 -or $members[0] -cne 'get') {
        throw "Packaged canonical OpenAPI is not read-only GET at $($pathProperty.Name)."
    }
}

$sqlConnectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_RELEASE_SMOKE_SQLSERVER')
if ([string]::IsNullOrWhiteSpace($sqlConnectionString)) {
    throw 'Production V2 release smoke requires a dedicated empty database via MES_INGEST_RELEASE_SMOKE_SQLSERVER.'
}
$emptyDatabaseConfirmation = [Environment]::GetEnvironmentVariable(
    'MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED')
if ($emptyDatabaseConfirmation -cne 'YES') {
    throw 'Set MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED=YES only after confirming the target database is dedicated, disposable, and empty.'
}

# Fail closed before Host bootstrap. The smoke owns no database lifecycle and never
# drops or clears a target supplied by an operator.
try {
    $sqlBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($sqlConnectionString)
} catch {
    throw 'MES_INGEST_RELEASE_SMOKE_SQLSERVER is not a valid SQL Server connection string.'
}
$targetDatabase = $sqlBuilder.InitialCatalog.Trim()
if ([string]::IsNullOrWhiteSpace($targetDatabase) `
    -or $targetDatabase -in @('master', 'model', 'msdb', 'tempdb')) {
    throw 'Production V2 release smoke requires an explicit non-system Initial Catalog.'
}
$preflightConnection = $null
$preflightCommand = $null
try {
    $preflightConnection = [System.Data.SqlClient.SqlConnection]::new($sqlBuilder.ConnectionString)
    $preflightConnection.Open()
    $preflightCommand = $preflightConnection.CreateCommand()
    $preflightCommand.CommandTimeout = 15
    $preflightCommand.CommandText = 'SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0;'
    $preflightUserTableCount = [long]$preflightCommand.ExecuteScalar()
} catch {
    throw 'Unable to verify that the dedicated release-smoke SQL Server database is reachable and empty.'
} finally {
    if ($null -ne $preflightCommand) { $preflightCommand.Dispose() }
    if ($null -ne $preflightConnection) { $preflightConnection.Dispose() }
}
if ($preflightUserTableCount -ne 0) {
    throw "Dedicated release-smoke database must have zero user tables before Host bootstrap; found $preflightUserTableCount."
}

$queryFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.sql' -File -Recurse -Force)
if ($queryFiles.Count -ne 1) {
    throw "Production V2 release smoke requires exactly one SQL artifact; found $($queryFiles.Count)."
}
$actualQueryPath = $queryFiles[0].FullName.Substring($packageRoot.Length).TrimStart('\', '/').Replace('\', '/')
if ($actualQueryPath -cne $canonicalQueryRelativePath) {
    throw "The only SQL artifact is not the canonical deployment path: $actualQueryPath"
}
$queryHash = (Get-FileHash -LiteralPath $canonicalQueryPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($queryHash -cne $canonicalQuerySha256) {
    throw "Canonical query hash mismatch: expected $canonicalQuerySha256; actual $queryHash"
}
$queryFile = Get-Item -LiteralPath $canonicalQueryPath
try {
    $queryManifest = Get-Content -Raw -LiteralPath $canonicalQueryManifestPath | ConvertFrom-Json
} catch {
    throw "Canonical query manifest is invalid: $canonicalQueryManifestRelativePath"
}
if ([int]$queryManifest.schemaVersion -ne 1 `
    -or [string]$queryManifest.id -cne 'MES_TASK_UNION' `
    -or [string]$queryManifest.version -cne $canonicalQueryVersion `
    -or [string]$queryManifest.path -cne $canonicalQueryRelativePath `
    -or [long]$queryManifest.length -ne $queryFile.Length `
    -or ([string]$queryManifest.sha256).ToLowerInvariant() -cne $canonicalQuerySha256) {
    throw 'Canonical query manifest does not match the approved deployment artifact.'
}

$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path ([IO.Path]::GetTempPath()) ("mes-ingest-release-smoke-" + [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

# Scripted rounds for the production entry. The values are synthetic: no plant row,
# credential, or datasource appears here, and the declared query version pins the
# recording to the same canonical artifact the Host loads and hashes.
$recordingPath = Join-Path $artifacts 'mes-task-union-rounds.json'
# @(@(...)) flattens in PowerShell, so each round's rows are placed by index to keep
# the recording an array of row arrays.
$firstRoundRows = [object[]]::new(1)
$firstRoundRows[0] = @(
    'SMOKE_TYPE_A', 'SMOKE-SUBLOT-1', 'SMOKE-AREA', 'SMOKE-EQP-1', 'SMOKE-STEP-1',
    '2026-01-02T09:00:00+08:00', 'SMOKE-PACKAGE-1'
)
$secondRoundRows = [object[]]::new(2)
$secondRoundRows[0] = @(
    'SMOKE_TYPE_A', 'SMOKE-SUBLOT-1', 'SMOKE-AREA', 'SMOKE-EQP-1', 'SMOKE-STEP-2',
    '2026-01-02T10:00:00+08:00', 'SMOKE-PACKAGE-1'
)
$secondRoundRows[1] = @(
    'SMOKE_TYPE_B', 'SMOKE-SUBLOT-2', 'SMOKE-AREA', 'SMOKE-EQP-2', 'SMOKE-STEP-1',
    '2026-01-02T10:05:00+08:00', 'SMOKE-PACKAGE-2'
)
$recordedRounds = [object[]]::new(2)
$recordedRounds[0] = [ordered]@{ rows = $firstRoundRows }
$recordedRounds[1] = [ordered]@{ rows = $secondRoundRows }
[ordered]@{
    schemaVersion = 1
    queryVersion = $canonicalQueryVersion
    columns = @(
        [ordered]@{ name = 'TASK_TYPE'; kind = 'Text' }
        [ordered]@{ name = 'SUBLOT'; kind = 'Text' }
        [ordered]@{ name = 'AREA'; kind = 'Text' }
        [ordered]@{ name = 'EQP'; kind = 'Text' }
        [ordered]@{ name = 'STEP'; kind = 'Text' }
        [ordered]@{ name = 'DATES'; kind = 'DateTimeOffset' }
        [ordered]@{ name = 'PACKAGE'; kind = 'Text' }
    )
    rounds = $recordedRounds
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recordingPath -Encoding UTF8

$sharedSecret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$port = Get-FreeLoopbackPort
$baseUrl = "http://127.0.0.1:$port"
$hostProcess = $null
$restartedHostProcess = $null
$remoteBindProcess = $null
$watchProcess = $null
$httpClient = $null
$startedAt = [DateTimeOffset]::UtcNow
try {
    Add-Type -AssemblyName System.Net.Http
    $httpClient = [Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(10)

    $hostProcess = Start-PackagedHostProcess `
        -Executable $serviceExecutable `
        -BaseUrl $baseUrl `
        -SqlConnectionString $sqlConnectionString `
        -SharedSecret $sharedSecret `
        -RecordingPath $recordingPath `
        -ContinuousPoll $true `
        -OneShotOnStartup $true
    $contract = Wait-PackagedHostContract `
        -Process $hostProcess `
        -BaseUrl $baseUrl `
        -TimeoutSeconds $StartupTimeoutSeconds

    if (
        [string]$contract.contractVersion -cne $expectedContractVersion -or
        [int]$contract.schemaVersion -ne $expectedContractSchemaVersion -or
        [string]$contract.compatibilityPolicy -cne $expectedCompatibilityPolicy -or
        [string]$contract.openApiDocument -cne '/openapi/v2.json' -or
        [string]$contract.businessSurface -cne 'READ_ONLY_GET' -or
        [string]$contract.legacySurfacePolicy -cne 'DEVELOPMENT_ONLY_EXCLUDED_FROM_V2' -or
        [string]::IsNullOrWhiteSpace([string]$contract.transportDemandKeyComparison)
    ) {
        throw 'Packaged Production V2 Host returned a non-frozen contract identity or production policy.'
    }

    $actualCapabilities = @($contract.capabilities)
    $actualCapabilityIds = @(
        $actualCapabilities |
            ForEach-Object { [string]$_.id } |
            Sort-Object
    )
    $capabilityDifference = @(
        Compare-Object -ReferenceObject $expectedCapabilityIds -DifferenceObject $actualCapabilityIds -CaseSensitive
    )
    if (
        $actualCapabilities.Count -ne $expectedCapabilityIds.Count -or
        $capabilityDifference.Count -gt 0
    ) {
        throw 'Packaged Production V2 Host capability set is not the exact frozen set.'
    }
    foreach ($expectedCapabilityId in $expectedCapabilityIds) {
        $actualCapability = @(
            $actualCapabilities |
                Where-Object { [string]$_.id -ceq $expectedCapabilityId }
        )
        if (
            $actualCapability.Count -ne 1 -or
            [string]$actualCapability[0].version -cne $expectedCapabilityVersion
        ) {
            throw "Packaged Production V2 Host capability $expectedCapabilityId has a non-frozen identity."
        }
        $actualCapabilityOperations = @(
            $actualCapability[0].operations |
                Where-Object { [string]$_.method -ceq 'GET' } |
                ForEach-Object { [string]$_.path } |
                Sort-Object
        )
        $expectedOperations = @($expectedCapabilityOperations[$expectedCapabilityId] | Sort-Object)
        $operationDifference = @(
            Compare-Object `
                -ReferenceObject $expectedOperations `
                -DifferenceObject $actualCapabilityOperations `
                -CaseSensitive
        )
        if (
            @($actualCapability[0].operations).Count -ne $expectedOperations.Count -or
            $operationDifference.Count -gt 0
        ) {
            throw "Packaged Production V2 Host capability $expectedCapabilityId has non-frozen operations."
        }
    }
    $contractOperations = @($actualCapabilities | ForEach-Object { $_.operations })
    $nonGetContractOperations = @(
        $contractOperations | Where-Object { [string]$_.method -cne 'GET' }
    )
    $contractOperationPaths = @(
        $contractOperations |
            ForEach-Object { [string]$_.path } |
            Sort-Object -Unique
    )
    $contractPathDifference = @(
        Compare-Object -ReferenceObject $expectedOpenApiPaths -DifferenceObject $contractOperationPaths -CaseSensitive
    )
    if ($nonGetContractOperations.Count -gt 0 -or $contractPathDifference.Count -gt 0) {
        throw 'Packaged Production V2 Host discovery operations differ from canonical OpenAPI.'
    }

    $liveOpenApiBytes = $httpClient.GetByteArrayAsync(
        "$baseUrl/openapi/v2.json"
    ).GetAwaiter().GetResult()
    try {
        $liveOpenApi = [Text.Encoding]::UTF8.GetString($liveOpenApiBytes) | ConvertFrom-Json
    } catch {
        throw 'Live /openapi/v2.json is not valid JSON.'
    }
    $packagedCanonicalJson = $canonicalOpenApi | ConvertTo-Json -Depth 100 -Compress
    $liveCanonicalJson = $liveOpenApi | ConvertTo-Json -Depth 100 -Compress
    if ($liveCanonicalJson -cne $packagedCanonicalJson) {
        throw 'Live /openapi/v2.json differs semantically from packaged canonical OpenAPI.'
    }
    $liveOpenApiCanonicalHash = Get-BytesSha256 -Bytes (
        [Text.Encoding]::UTF8.GetBytes($liveCanonicalJson)
    )

    $legacyRoutes = @(
        '/api/contract',
        '/api/demands',
        '/api/demands/legacy-probe',
        '/api/alerts',
        '/api/poll-health',
        '/api/demand-changes',
        '/openapi/v1.json'
    )
    $legacyStatuses = [ordered]@{}
    foreach ($legacyRoute in $legacyRoutes) {
        $legacyStatus = Get-HttpStatus -Uri "$baseUrl$legacyRoute"
        $legacyStatuses[$legacyRoute] = $legacyStatus
        if ($legacyStatus -ne 404) {
            throw "Packaged Production V2 Host exposed legacy route $legacyRoute (HTTP $legacyStatus)."
        }
    }

    # First catalog body, its CatalogRevision/ETag, and the conditional read.
    $catalogUri = "$baseUrl/api/v2/externally-readable-demand-catalog"
    $catalogFirst = Invoke-SmokeRequest -Client $httpClient -Uri $catalogUri
    if ($catalogFirst.StatusCode -ne 200) {
        throw "First externally readable Demand catalog read failed (HTTP $($catalogFirst.StatusCode))."
    }
    if ([string]::IsNullOrWhiteSpace($catalogFirst.ETag)) {
        throw 'The externally readable Demand catalog did not return a CatalogRevision ETag.'
    }
    $catalogBody = $catalogFirst.Body | ConvertFrom-Json
    $catalogDeclaredCount = [int](Get-JsonProperty -Object $catalogBody -Name 'count')
    if (
        [string]$catalogBody.contractVersion -cne $expectedContractVersion -or
        [long]$catalogBody.catalogRevision -le 0 -or
        $catalogDeclaredCount -le 0 -or
        @($catalogBody.items).Count -ne $catalogDeclaredCount -or
        [string]::IsNullOrWhiteSpace([string]$catalogBody.projectionCommitId)
    ) {
        throw 'The first catalog body did not carry a committed, non-empty V2 catalog revision.'
    }
    if ($catalogFirst.ETag -cne "W/`"catalog-r$([long]$catalogBody.catalogRevision)`"") {
        throw 'The catalog ETag does not identify the CatalogRevision in the body.'
    }
    $catalogConditional = Invoke-SmokeRequest `
        -Client $httpClient `
        -Uri $catalogUri `
        -IfNoneMatch $catalogFirst.ETag
    if ($catalogConditional.StatusCode -ne 304) {
        throw "A same-revision conditional catalog read must be 304; got $($catalogConditional.StatusCode)."
    }
    if (-not [string]::IsNullOrWhiteSpace($catalogConditional.Body)) {
        throw 'A 304 catalog read must not carry a body.'
    }

    # Restricted raw evidence authorizes before it looks anything up, on localhost too.
    $rawEvidenceUri =
        "$baseUrl/api/v2/error-search/release-smoke/evidence/release-smoke/raw-observations"
    $rawWithoutSecret = Invoke-SmokeRequest -Client $httpClient -Uri $rawEvidenceUri
    if ($rawWithoutSecret.StatusCode -ne 403) {
        throw "Restricted raw evidence must be denied without a Bearer secret; got $($rawWithoutSecret.StatusCode)."
    }
    $rawWithSecret = Invoke-SmokeRequest `
        -Client $httpClient `
        -Uri $rawEvidenceUri `
        -BearerToken $sharedSecret
    if ($rawWithSecret.StatusCode -eq 403) {
        throw 'Restricted raw evidence rejected the configured shared secret.'
    }
    $rawWithWrongSecret = Invoke-SmokeRequest `
        -Client $httpClient `
        -Uri $rawEvidenceUri `
        -BearerToken 'not-the-configured-secret'
    if ($rawWithWrongSecret.StatusCode -ne 403) {
        throw "Restricted raw evidence accepted a wrong Bearer secret (HTTP $($rawWithWrongSecret.StatusCode))."
    }

    # The Service owns the poll loop. Prove it advances before any Watch exists.
    $highWaterBeforeWatch = Get-PollTraceHighWater -BaseUrl $baseUrl
    $highWaterWhilePolling = Wait-PollTraceHighWaterAbove `
        -BaseUrl $baseUrl `
        -Previous $highWaterBeforeWatch

    if ($IncludePackagedWatch) {
        # This check is about process lifetime, not about the operator's desktop. Point
        # the Watch at a smoke-owned profile so it leaves no window, refresh, connection,
        # or area-filter preference behind on the golden machine.
        $watchProfileRoot = Join-Path $artifacts 'packaged-watch-profile'
        New-Item -ItemType Directory -Path $watchProfileRoot -Force | Out-Null
        $watchInfo = [Diagnostics.ProcessStartInfo]::new()
        $watchInfo.FileName = $watchExecutable
        $watchInfo.WorkingDirectory = Split-Path -Parent $watchExecutable
        $watchInfo.UseShellExecute = $false
        $watchInfo.EnvironmentVariables['MesIngestWatch__BaseUrl'] = $baseUrl
        $watchInfo.EnvironmentVariables['MesIngestWatch__SharedSecret'] = $sharedSecret
        $watchInfo.EnvironmentVariables['MesIngestWatch__RenderingMode'] = 'SoftwareOnly'
        $watchInfo.EnvironmentVariables['LOCALAPPDATA'] = $watchProfileRoot
        $watchInfo.EnvironmentVariables['MESINGEST_WATCH_UI_TEST_MODE'] = '1'
        $watchInfo.EnvironmentVariables['MesIngestWatch__LogDirectory'] =
            Join-Path $watchProfileRoot 'logs'
        $watchProcess = [Diagnostics.Process]::Start($watchInfo)
        if ($null -eq $watchProcess) { throw 'Packaged Watch did not start.' }
        try {
            $watchReachedIdle = $watchProcess.WaitForInputIdle(30000)
        } catch [InvalidOperationException] {
            # WaitForInputIdle throws instead of returning when the process is already
            # gone, which would hide the real startup failure behind a Win32 message.
            $watchProcess.WaitForExit(5000) | Out-Null
            throw "Packaged Watch exited during startup with code $($watchProcess.ExitCode)."
        }
        if (-not $watchReachedIdle) {
            throw 'Packaged Watch did not reach an idle input state within 30 seconds.'
        }
        $watchProcess.Refresh()
        if ($watchProcess.MainWindowHandle -eq [IntPtr]::Zero) {
            throw 'Packaged Watch reached idle input without showing a main window.'
        }
        $watchProcess.CloseMainWindow() | Out-Null
        if (-not $watchProcess.WaitForExit(30000)) {
            $watchProcess.Kill()
            $watchProcess.WaitForExit(5000) | Out-Null
            throw 'Packaged Watch did not exit after its main window was closed.'
        }
        $watchExitCode = $watchProcess.ExitCode
        if ($watchExitCode -ne 0) {
            throw "Packaged Watch exited with code $watchExitCode."
        }
        if ($hostProcess.HasExited) {
            throw 'Packaged Host exited when the Watch closed; the Service does not own its own lifetime.'
        }
        $highWaterAfterWatch = Wait-PollTraceHighWaterAbove `
            -BaseUrl $baseUrl `
            -Previous $highWaterWhilePolling
        $watchIndependence = [ordered]@{
            checked = $true
            hostStillServing = $true
            pollTraceHighWaterAfterWatchExit = $highWaterAfterWatch
        }
    } else {
        $watchIndependence = [ordered]@{
            checked = $false
            namedSkip = 'PACKAGED_WATCH_PROCESS_INDEPENDENCE'
            reason = 'Starting the packaged WPF Watch needs the gpt_win11 interactive scheduled task; rerun with -IncludePackagedWatch there.'
        }
    }

    $catalogBeforeRestart = Invoke-SmokeRequest -Client $httpClient -Uri $catalogUri
    if ($catalogBeforeRestart.StatusCode -ne 200) {
        throw "Pre-restart catalog read failed (HTTP $($catalogBeforeRestart.StatusCode))."
    }
    $beforeRestart = $catalogBeforeRestart.Body | ConvertFrom-Json
    $beforeRestartDemandIds = @($beforeRestart.items | ForEach-Object { [string]$_.demandId } | Sort-Object)

    # An abrupt stop is the honest restart: a committed projection must survive it.
    Stop-PackagedHostProcess -Process $hostProcess
    $hostProcess = $null

    $restartPort = Get-FreeLoopbackPort
    $restartBaseUrl = "http://127.0.0.1:$restartPort"
    $restartedHostProcess = Start-PackagedHostProcess `
        -Executable $serviceExecutable `
        -BaseUrl $restartBaseUrl `
        -SqlConnectionString $sqlConnectionString `
        -SharedSecret $sharedSecret `
        -ContinuousPoll $false `
        -OneShotOnStartup $false
    Wait-PackagedHostContract `
        -Process $restartedHostProcess `
        -BaseUrl $restartBaseUrl `
        -TimeoutSeconds $StartupTimeoutSeconds | Out-Null
    $catalogAfterRestart = Invoke-SmokeRequest `
        -Client $httpClient `
        -Uri "$restartBaseUrl/api/v2/externally-readable-demand-catalog"
    if ($catalogAfterRestart.StatusCode -ne 200) {
        throw "Post-restart catalog read failed (HTTP $($catalogAfterRestart.StatusCode))."
    }
    $afterRestart = $catalogAfterRestart.Body | ConvertFrom-Json
    $afterRestartDemandIds = @($afterRestart.items | ForEach-Object { [string]$_.demandId } | Sort-Object)
    $afterRestartCount = [int](Get-JsonProperty -Object $afterRestart -Name 'count')
    $beforeRestartCount = [int](Get-JsonProperty -Object $beforeRestart -Name 'count')
    # The pre-restart Host was still polling, so its commit identity can legitimately
    # advance between the read and the kill. What must not change is the catalog the
    # recording settled on, and the revision must never go backwards.
    if (
        $afterRestartCount -ne $beforeRestartCount -or
        @(Compare-Object `
            -ReferenceObject $beforeRestartDemandIds `
            -DifferenceObject $afterRestartDemandIds `
            -CaseSensitive).Count -gt 0 -or
        [long]$afterRestart.catalogRevision -lt [long]$beforeRestart.catalogRevision -or
        [string]::IsNullOrWhiteSpace([string]$afterRestart.projectionCommitId)
    ) {
        throw 'The SQL Server projection did not survive a Host restart unchanged.'
    }
    # The restarted Host does not poll, so two reads must be byte-for-byte the same
    # revision. That is what makes the comparison above a persistence result rather
    # than a snapshot of a moving projection.
    $catalogAfterRestartRepeat = Invoke-SmokeRequest `
        -Client $httpClient `
        -Uri "$restartBaseUrl/api/v2/externally-readable-demand-catalog"
    if ($catalogAfterRestartRepeat.StatusCode -ne 200 `
        -or $catalogAfterRestartRepeat.ETag -cne $catalogAfterRestart.ETag) {
        throw 'The restarted Host did not serve a quiesced projection.'
    }
    Stop-PackagedHostProcess -Process $restartedHostProcess
    $restartedHostProcess = $null

    # A remote binding without a shared secret must refuse to start at all. Startup
    # validation runs before Kestrel binds, so nothing is ever exposed off-loopback.
    $remoteBindPort = Get-FreeLoopbackPort
    $remoteBindProcess = Start-PackagedHostProcess `
        -Executable $serviceExecutable `
        -BaseUrl "http://0.0.0.0:$remoteBindPort" `
        -SqlConnectionString $sqlConnectionString `
        -SharedSecret '' `
        -ContinuousPoll $false `
        -OneShotOnStartup $false `
        -DrainStreams $false
    # The process is expected to die immediately, so reading stderr to end cannot block.
    $remoteBindStandardError = $remoteBindProcess.StandardError.ReadToEnd()
    if (-not $remoteBindProcess.WaitForExit(20000)) {
        $remoteBindProcess.Kill()
        $remoteBindProcess.WaitForExit(5000) | Out-Null
        throw 'A remote binding with no shared secret started instead of refusing.'
    }
    $remoteBindExitCode = $remoteBindProcess.ExitCode
    if ($remoteBindExitCode -eq 0) {
        throw 'A remote binding with no shared secret exited successfully instead of refusing.'
    }
    # A non-zero exit alone would also be produced by, say, a port conflict, which would
    # let this record a pass the shared-secret guard never earned. The refusal message is
    # a fixed startup string and carries no credential or datasource value.
    if ($remoteBindStandardError.IndexOf(
            'MesIngest:SharedSecret',
            [StringComparison]::Ordinal) -lt 0) {
        throw 'A remote binding with no shared secret failed for some other reason than the missing shared secret.'
    }
    $remoteBindStandardError = $null
    $remoteBindProcess.Dispose()
    $remoteBindProcess = $null

    [ordered]@{
        status = 'PASSED'
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        durationMs = [Math]::Round(([DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds, 1)
        baseUrl = 'http://127.0.0.1:<ephemeral>'
        environment = 'Production'
        contractEndpoint = '/api/v2/contract'
        contractVersion = [string]$contract.contractVersion
        schemaVersion = [int]$contract.schemaVersion
        compatibilityPolicy = [string]$contract.compatibilityPolicy
        capabilityIds = $actualCapabilityIds
        transportDemandKeyComparison = [string]$contract.transportDemandKeyComparison
        openApi = [ordered]@{
            path = $canonicalOpenApiRelativePath
            sha256 = $canonicalOpenApiHash
            liveCanonicalSha256 = $liveOpenApiCanonicalHash
        }
        legacyDevelopmentFlagRequested = $true
        legacyRouteStatuses = $legacyStatuses
        canonicalQuery = [ordered]@{
            path = $canonicalQueryRelativePath
            length = $queryFile.Length
            sha256 = $queryHash
            version = $canonicalQueryVersion
        }
        sharedContract = [ordered]@{
            assembly = $sharedContractAssembly
            sha256 = $sharedContractSha256
            serviceAndWatchIdentical = $true
        }
        roundSource = [ordered]@{
            mode = 'RECORDED_ROUNDS'
            driver = 'FILE_REPLAY'
            recording = 'mes-task-union-rounds.json'
            queryVersion = $canonicalQueryVersion
            liveOracleAttested = $false
            note = 'Recorded rounds drive the production entry; they are never factory acceptance evidence.'
        }
        externallyReadableDemandCatalog = [ordered]@{
            firstBodyCount = $catalogDeclaredCount
            catalogRevision = [long]$catalogBody.catalogRevision
            etag = [string]$catalogFirst.ETag
            sameRevisionStatus = 304
        }
        readOnlyApiAuthorization = [ordered]@{
            restrictedRawEvidenceWithoutSecret = $rawWithoutSecret.StatusCode
            restrictedRawEvidenceWithWrongSecret = $rawWithWrongSecret.StatusCode
            restrictedRawEvidenceWithSecret = $rawWithSecret.StatusCode
            remoteBindWithoutSharedSecretExitCode = $remoteBindExitCode
        }
        servicePollOwnership = [ordered]@{
            pollTraceHighWaterBeforeWatch = $highWaterBeforeWatch
            pollTraceHighWaterWhilePolling = $highWaterWhilePolling
            watchIndependence = $watchIndependence
        }
        sqlServerRestartPersistence = [ordered]@{
            checked = $true
            catalogCount = $afterRestartCount
            projectionCommitId = [string]$afterRestart.projectionCommitId
            catalogRevision = [long]$afterRestart.catalogRevision
        }
        sqlServerConfigured = $true
        dedicatedEmptyDatabaseConfirmed = $true
        preflightUserTableCount = $preflightUserTableCount
        oracleConnectionAttempted = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'release-smoke-result.json') -Encoding UTF8
    Write-Output "MESINGEST_PRODUCTION_V2_RELEASE_SMOKE_PASSED: artifacts=$artifacts"
}
finally {
    $sqlConnectionString = $null
    $sharedSecret = $null
    if ($null -ne $httpClient) {
        $httpClient.Dispose()
    }
    if ($null -ne $watchProcess) {
        if (-not $watchProcess.HasExited) {
            $watchProcess.Kill()
            $watchProcess.WaitForExit(5000) | Out-Null
        }
        $watchProcess.Dispose()
    }
    Stop-PackagedHostProcess -Process $hostProcess
    Stop-PackagedHostProcess -Process $restartedHostProcess
    Stop-PackagedHostProcess -Process $remoteBindProcess
}
