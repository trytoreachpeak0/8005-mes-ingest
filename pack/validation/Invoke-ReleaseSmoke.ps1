#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ArtifactsDirectory,

    [ValidateRange(5, 30)]
    [int] $StartupTimeoutSeconds = 10
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

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serviceExecutable = Join-Path $packageRoot 'service\MesIngest.Host.exe'
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

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$hostProcess = $null
$httpClient = $null
$startedAt = [DateTimeOffset]::UtcNow
try {
    $hostInfo = [Diagnostics.ProcessStartInfo]::new()
    $hostInfo.FileName = $serviceExecutable
    $hostInfo.WorkingDirectory = Split-Path -Parent $serviceExecutable
    $hostInfo.UseShellExecute = $false
    $hostInfo.RedirectStandardOutput = $true
    $hostInfo.RedirectStandardError = $true
    $hostInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    $hostInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $hostInfo.EnvironmentVariables.Remove('MES_INGEST_RELEASE_SMOKE_SQLSERVER')
    $hostInfo.EnvironmentVariables.Remove('MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED')
    $hostInfo.EnvironmentVariables.Remove('MES_INGEST_SQLSERVER')
    $hostInfo.EnvironmentVariables['MesIngest__NewSqlServerConnectionString'] = $sqlConnectionString
    $hostInfo.EnvironmentVariables['MesIngest__SnapshotSource'] = 'Oracle'
    $hostInfo.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] = 'false'
    $hostInfo.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] = 'false'
    # Request the development-only switch deliberately. Production must suppress
    # it, otherwise this build cannot claim the ADR-mes-0017 production cutover.
    $hostInfo.EnvironmentVariables['MesIngest__EnableLegacyDevelopmentEndpoints'] = 'true'
    $hostInfo.EnvironmentVariables['MesIngest__Urls'] = $baseUrl
    $hostInfo.EnvironmentVariables['MesIngest__SharedSecret'] = ''
    $hostProcess = [Diagnostics.Process]::Start($hostInfo)
    if ($null -eq $hostProcess) { throw 'Packaged Production V2 Host did not start.' }
    # Drain both streams to prevent a full pipe from blocking the Host, but never persist
    # provider output: SQL/Oracle failures can include datasource or credential details.
    $hostProcess.BeginOutputReadLine()
    $hostProcess.BeginErrorReadLine()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $contract = $null
    do {
        if ($hostProcess.HasExited) {
            throw "Packaged Production V2 Host exited during startup with code $($hostProcess.ExitCode)."
        }
        try {
            $contract = Invoke-RestMethod -Uri "$baseUrl/api/v2/contract" -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 200
        }
    } while ($null -eq $contract -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $contract) {
        throw "Packaged Production V2 Host did not expose /api/v2/contract within $StartupTimeoutSeconds seconds."
    }
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

    Add-Type -AssemblyName System.Net.Http
    $httpClient = [Net.Http.HttpClient]::new()
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
        sqlServerConfigured = $true
        dedicatedEmptyDatabaseConfirmed = $true
        preflightUserTableCount = $preflightUserTableCount
        oracleConnectionAttempted = $false
        watchValidated = $false
        watchValidation = 'DEFERRED_TO_PACKAGED_WATCH_ACCEPTANCE'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifacts 'release-smoke-result.json') -Encoding UTF8
    Write-Output "MESINGEST_PRODUCTION_V2_RELEASE_SMOKE_PASSED: artifacts=$artifacts"
}
finally {
    $sqlConnectionString = $null
    if ($null -ne $httpClient) {
        $httpClient.Dispose()
    }
    if ($null -ne $hostProcess) {
        if (-not $hostProcess.HasExited) {
            $hostProcess.Kill()
            $hostProcess.WaitForExit(5000) | Out-Null
        }
        $hostProcess.Dispose()
    }
}
