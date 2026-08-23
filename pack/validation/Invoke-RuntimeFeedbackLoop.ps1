#Requires -Version 5.1
<#
.SYNOPSIS
  Capture a read-only MesIngest runtime feedback snapshot.

.DESCRIPTION
  Reads Windows service/process/listener state, deployed assembly and contract identities,
  representative V2 GET responses, SQL Server resource state, SQL/Windows error signals, an
  optional VSTest TRX summary, and an optional git worktree inventory. It never changes a service,
  database, server configuration, or production configuration file. Connection strings and
  credentials are held in memory only and are never written to the evidence bundle.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $ServiceName = 'MesIngest',
    [string] $SqlServiceName = 'MSSQLSERVER',
    [string] $BaseUrl = '',
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime-feedback-runs'),
    [ValidateRange(1, 120)] [int] $RequestTimeoutSeconds = 5,
    [ValidateRange(1, 120)] [int] $SqlConnectionTimeoutSeconds = 5,
    [ValidateRange(1, 300)] [int] $SqlCommandTimeoutSeconds = 10,
    [ValidateRange(1, 8760)] [int] $EventLookbackHours = 168,
    [string] $SharedSecretEnvironmentVariable = 'MES_INGEST_SHARED_SECRET',
    [string] $SqlConnectionStringEnvironmentVariable = '',
    [string] $SqlTestTrxPath = '',
    [string] $SqlTestAttestationPath = '',
    [string] $RepositoryRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$startedAt = [DateTimeOffset]::UtcNow
$runId = 'run-{0}-{1}' -f $startedAt.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $runId
if (Test-Path -LiteralPath $runDirectory) {
    throw "Refusing to overwrite runtime feedback run: $runDirectory"
}
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null

function Get-SafeExecutablePath {
    param([AllowNull()][AllowEmptyString()][string] $CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return '' }
    if ($CommandLine -match '^\s*"([^"]+\.exe)"') { return $Matches[1] }
    if ($CommandLine -match '^\s*([^\s]+\.exe)(?:\s|$)') { return $Matches[1] }
    return 'UNRESOLVED_EXECUTABLE_PATH'
}

function Get-SafeBaseUrl {
    param([Parameter(Mandatory = $true)][string] $Value)
    try {
        $uri = [Uri]$Value
        $port = if ($uri.IsDefaultPort) { -1 } else { $uri.Port }
        return ([UriBuilder]::new($uri.Scheme, $uri.Host, $port)).Uri.GetLeftPart([UriPartial]::Authority)
    }
    catch { return 'INVALID_BASE_URL' }
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream] $Stream)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-Sha256String {
    param([Parameter(Mandatory = $true)][string] $Value)
    $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($Value), $false)
    try { return Get-StreamSha256 $stream } finally { $stream.Dispose() }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try { return Get-StreamSha256 $stream } finally { $stream.Dispose() }
}

function Get-JsonErrorCode {
    param([AllowNull()][AllowEmptyString()][string] $Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return '' }
    try {
        $json = $Body | ConvertFrom-Json
        foreach ($name in @('code', 'errorCode', 'diagnosticCode')) {
            $property = $json.PSObject.Properties[$name]
            if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                return [string]$property.Value
            }
        }
    } catch { }
    return ''
}

function Invoke-EndpointProbe {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [switch] $CaptureBody
    )

    $uri = $BaseUrl.TrimEnd('/') + $RelativePath
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $client = [Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
        if (-not [string]::IsNullOrWhiteSpace($sharedSecret)) {
            $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $sharedSecret)
        }
        $response = $client.GetAsync($uri).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $timer.Stop()
        $status = [int]$response.StatusCode
        $code = Get-JsonErrorCode $body
        $classification = if ($status -ge 200 -and $status -lt 400) {
            'SUCCESS'
        } elseif ($code -match 'CONTRACT.*MISMATCH|MISMATCH.*CONTRACT') {
            'CONTRACT_MISMATCH'
        } elseif ($status -eq 401 -or $status -eq 403) {
            'AUTHORIZATION_REQUIRED'
        } elseif ($code -match 'SQL.*UNAVAILABLE|DATABASE.*UNAVAILABLE') {
            'SQL_UNAVAILABLE'
        } else {
            'HTTP_ERROR'
        }
        $correlationId = ''
        if ($response.Headers.Contains('X-Correlation-Id')) {
            $correlationId = [string](@($response.Headers.GetValues('X-Correlation-Id')) | Select-Object -First 1)
        }
        $result = [ordered]@{
            name = $Name
            path = $RelativePath
            statusCode = $status
            durationMs = $timer.ElapsedMilliseconds
            responseBytes = [Text.Encoding]::UTF8.GetByteCount($body)
            correlationId = $correlationId
            diagnosticCode = $code
            classification = $classification
        }
        if ($CaptureBody) { $result.capturedBody = $body }
        return [pscustomobject]$result
    } catch [System.Threading.Tasks.TaskCanceledException] {
        $timer.Stop()
        return [pscustomobject][ordered]@{
            name = $Name; path = $RelativePath; statusCode = 0; durationMs = $timer.ElapsedMilliseconds
            responseBytes = 0; correlationId = ''; diagnosticCode = 'REQUEST_TIMEOUT'; classification = 'QUERY_TIMEOUT'
        }
    } catch {
        $timer.Stop()
        return [pscustomobject][ordered]@{
            name = $Name; path = $RelativePath; statusCode = 0; durationMs = $timer.ElapsedMilliseconds
            responseBytes = 0; correlationId = ''; diagnosticCode = 'HTTP_CONNECTION_FAILED'; classification = 'HOST_UNREACHABLE'
            errorType = $_.Exception.GetType().FullName
        }
    } finally {
        $client.Dispose()
    }
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

function Invoke-ReadOnlySqlQuery {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $CommandText
    )
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $command = $Connection.CreateCommand()
        try {
            $command.CommandTimeout = $SqlCommandTimeoutSeconds
            $command.CommandText = $CommandText
            $table = [Data.DataTable]::new()
            $reader = $command.ExecuteReader()
            try { $table.Load($reader) } finally { $reader.Dispose() }
            $timer.Stop()
            return [pscustomobject][ordered]@{
                name = $Name; available = $true; durationMs = $timer.ElapsedMilliseconds
                rows = @(Convert-DataTableRows $table); errorType = ''; diagnosticCode = ''
            }
        } finally { $command.Dispose() }
    } catch {
        $timer.Stop()
        $number = if ($_.Exception.InnerException -is [System.Data.SqlClient.SqlException]) {
            $_.Exception.InnerException.Number
        } elseif ($_.Exception -is [System.Data.SqlClient.SqlException]) { $_.Exception.Number } else { 0 }
        return [pscustomobject][ordered]@{
            name = $Name; available = $false; durationMs = $timer.ElapsedMilliseconds; rows = @()
            errorType = $_.Exception.GetType().FullName; diagnosticCode = "SQL_QUERY_FAILED_$number"
        }
    }
}

function Get-ErrorSignalSummary {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Events,
        [Parameter(Mandatory = $true)][string] $Signal
    )
    $matched = @($Events | Where-Object {
        $_.Id.ToString([Globalization.CultureInfo]::InvariantCulture) -eq $Signal -or $_.Message -match [regex]::Escape($Signal)
    })
    return [pscustomobject][ordered]@{
        signal = $Signal
        count = $matched.Count
        firstObservedAt = if ($matched.Count -gt 0) { ($matched | Sort-Object TimeCreated | Select-Object -First 1).TimeCreated.ToUniversalTime().ToString('o') } else { $null }
        lastObservedAt = if ($matched.Count -gt 0) { ($matched | Sort-Object TimeCreated -Descending | Select-Object -First 1).TimeCreated.ToUniversalTime().ToString('o') } else { $null }
    }
}

function Get-WorktreeInventory {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot) -or -not (Test-Path -LiteralPath $RepositoryRoot)) {
        return [pscustomobject][ordered]@{ available = $false; entries = @(); diagnosticCode = 'REPOSITORY_ROOT_NOT_PROVIDED' }
    }
    try {
        $lines = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=normal 2>$null)
        $entries = foreach ($line in $lines) {
            if ($line.Length -lt 4) { continue }
            $path = $line.Substring(3).Trim('"') -replace '\\', '/'
            $scope = if ($path -eq 'mes/ingest/csharp/pack/validation/Invoke-RuntimeFeedbackLoop.ps1' -or
                $path -eq 'mes/ingest/csharp/MesIngest.Tests/RuntimeFeedbackLoopTests.cs' -or
                $path -eq 'mes/ingest/csharp/pack/Publish-MesIngest.ps1' -or
                $path -eq 'mes/ingest/csharp/pack/INSTALL.md') {
                'ticket-path-overlap-unattributed'
            } elseif ($path -like '.scratch/mes-ingest-bounded-storage-low-memory*' -or
                $path -like 'mes/ingest/csharp/MesIngest.Infrastructure/SqlServer/*' -or
                $path -like 'docs/adr/mes/00*' -or $path -eq 'CONTEXT.md') {
                'optimization-path-overlap-unattributed'
            } else {
                'other-path-unattributed'
            }
            [pscustomobject][ordered]@{ status = $line.Substring(0, 2); path = $path; scope = $scope; ownership = 'preserve-unattributed' }
        }
        return [pscustomobject][ordered]@{ available = $true; entries = @($entries); diagnosticCode = '' }
    } catch {
        return [pscustomobject][ordered]@{ available = $false; entries = @(); diagnosticCode = 'GIT_STATUS_FAILED' }
    }
}

function Test-SqlQueryAvailable {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Queries,
        [Parameter(Mandatory = $true)][string] $Name
    )
    return @($Queries | Where-Object { $_.name -eq $Name -and $_.available }).Count -eq 1
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$serviceRoot = Join-Path $resolvedInstallRoot 'service'
$localConfigPath = Join-Path $serviceRoot 'appsettings.Local.json'
$manifestPath = Join-Path $resolvedInstallRoot 'RELEASE-MANIFEST.json'
$openApiPath = Join-Path $resolvedInstallRoot 'openapi\v2.json'

$mesConfig = $null
$connectionString = ''
if (Test-Path -LiteralPath $localConfigPath -PathType Leaf) {
    $config = Get-Content -Raw -LiteralPath $localConfigPath | ConvertFrom-Json
    $mesConfig = $config.MesIngest
    if ($null -ne $mesConfig) { $connectionString = [string]$mesConfig.NewSqlServerConnectionString }
}
if (-not [string]::IsNullOrWhiteSpace($SqlConnectionStringEnvironmentVariable)) {
    $environmentConnectionString = [Environment]::GetEnvironmentVariable($SqlConnectionStringEnvironmentVariable)
    if (-not [string]::IsNullOrWhiteSpace($environmentConnectionString)) { $connectionString = $environmentConnectionString }
}
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = if ($null -ne $mesConfig -and -not [string]::IsNullOrWhiteSpace([string]$mesConfig.Urls)) {
        ([string]$mesConfig.Urls -split ';')[0]
    } else { 'http://127.0.0.1:5088' }
}
$sharedSecret = [Environment]::GetEnvironmentVariable($SharedSecretEnvironmentVariable)

$service = Get-CimInstance Win32_Service -Filter "Name='$($ServiceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue
$process = $null
if ($null -ne $service -and [int]$service.ProcessId -gt 0) {
    $process = Get-Process -Id ([int]$service.ProcessId) -ErrorAction SilentlyContinue
}
$listeners = @()
if ($null -ne $service -and [int]$service.ProcessId -gt 0) {
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess ([int]$service.ProcessId) -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, OwningProcess, State)
}
$sqlService = Get-CimInstance Win32_Service -Filter "Name='$($SqlServiceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue

$assemblies = foreach ($relativePath in @('MesIngest.Host.exe', 'MesIngest.Host.dll', 'MesIngest.Core.dll', 'MesIngest.Infrastructure.dll')) {
    $path = Join-Path $serviceRoot $relativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $item = Get-Item -LiteralPath $path
        [pscustomobject][ordered]@{
            file = $relativePath
            length = $item.Length
            fileVersion = $item.VersionInfo.FileVersion
            productVersion = $item.VersionInfo.ProductVersion
            sha256 = Get-FileSha256 $path
            lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
        }
    }
}

$manifest = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
} else { $null }
$packageOpenApi = if (Test-Path -LiteralPath $openApiPath -PathType Leaf) {
    try { Get-Content -Raw -LiteralPath $openApiPath | ConvertFrom-Json } catch { $null }
} else { $null }
$expectedContractVersion = if ($null -ne $manifest -and $null -ne $manifest.openApi) { [string]$manifest.openApi.contractVersion } else { '' }
$expectedSchemaVersion = if ($null -ne $manifest -and $null -ne $manifest.openApi) { [int]$manifest.openApi.schemaVersion } else { 0 }
$expectedOpenApiSha256 = if ($null -ne $manifest -and $null -ne $manifest.openApi) { ([string]$manifest.openApi.sha256).ToLowerInvariant() } else { '' }
$expectedCapabilityIdentity = @()
if ($null -ne $packageOpenApi) {
    try {
        $capabilitySchema = $packageOpenApi.components.schemas.NewMesIngestCapabilityDto
        $capabilityVersions = @($capabilitySchema.properties.version.enum)
        if ($capabilityVersions.Count -eq 1) {
            $expectedCapabilityIdentity = @($capabilitySchema.properties.id.enum | ForEach-Object {
                '{0}:{1}' -f $_, $capabilityVersions[0]
            } | Sort-Object)
        }
    } catch { $expectedCapabilityIdentity = @() }
}
$expectedCapabilityIdentityText = $expectedCapabilityIdentity -join "`n"
$expectedCapabilityIdentitySha256 = if ($expectedCapabilityIdentity.Count -gt 0) { Get-Sha256String $expectedCapabilityIdentityText } else { '' }

$contractProbe = Invoke-EndpointProbe -Name 'contract' -RelativePath '/api/v2/contract' -CaptureBody
$contractBody = if ($contractProbe.PSObject.Properties['capturedBody']) { [string]$contractProbe.capturedBody } else { '' }
if ($contractProbe.PSObject.Properties['capturedBody']) { $contractProbe.PSObject.Properties.Remove('capturedBody') }
$contractIdentity = [ordered]@{
    available = $false; contractVersion = ''; schemaVersion = 0; compatibilityPolicy = ''
    capabilityCount = 0; capabilityIdentitySha256 = ''; expectedCapabilityCount = $expectedCapabilityIdentity.Count
    expectedCapabilityIdentitySha256 = $expectedCapabilityIdentitySha256; expectedContractVersion = $expectedContractVersion
    expectedSchemaVersion = $expectedSchemaVersion; exactPackageMatch = $false; classification = $contractProbe.classification
}
if (-not [string]::IsNullOrWhiteSpace($contractBody)) {
    try {
        $contract = $contractBody | ConvertFrom-Json
        $capabilityIdentity = @($contract.capabilities | ForEach-Object { '{0}:{1}' -f $_.id, $_.version } | Sort-Object) -join "`n"
        $contractIdentity.available = $true
        $contractIdentity.contractVersion = [string]$contract.contractVersion
        $contractIdentity.schemaVersion = [int]$contract.schemaVersion
        $contractIdentity.compatibilityPolicy = [string]$contract.compatibilityPolicy
        $contractIdentity.capabilityCount = @($contract.capabilities).Count
        $contractIdentity.capabilityIdentitySha256 = Get-Sha256String $capabilityIdentity
        $contractIdentity.exactPackageMatch = -not [string]::IsNullOrWhiteSpace($expectedContractVersion) -and
            $expectedCapabilityIdentity.Count -gt 0 -and
            [string]::Equals($contractIdentity.contractVersion, $expectedContractVersion, [StringComparison]::Ordinal) -and
            $contractIdentity.schemaVersion -eq $expectedSchemaVersion -and
            [string]::Equals($contractIdentity.capabilityIdentitySha256, $expectedCapabilityIdentitySha256, [StringComparison]::Ordinal) -and
            [string]::Equals($contractIdentity.compatibilityPolicy, 'EXACT_VERSION_SCHEMA_AND_CAPABILITIES', [StringComparison]::Ordinal)
        if (-not $contractIdentity.exactPackageMatch) {
            $contractIdentity.classification = 'CONTRACT_MISMATCH'
            $contractProbe.classification = 'CONTRACT_MISMATCH'
        }
    } catch {
        $contractIdentity.classification = 'CONTRACT_MISMATCH'
        $contractProbe.classification = 'CONTRACT_MISMATCH'
    }
}

$openApiProbe = Invoke-EndpointProbe -Name 'openapi' -RelativePath '/openapi/v2.json' -CaptureBody
$openApiBody = if ($openApiProbe.PSObject.Properties['capturedBody']) { [string]$openApiProbe.capturedBody } else { '' }
if ($openApiProbe.PSObject.Properties['capturedBody']) { $openApiProbe.PSObject.Properties.Remove('capturedBody') }
$runtimeOpenApi = $null
if (-not [string]::IsNullOrWhiteSpace($openApiBody)) {
    try { $runtimeOpenApi = $openApiBody | ConvertFrom-Json } catch { }
}
$openApiIdentity = [ordered]@{
    packageFilePresent = (Test-Path -LiteralPath $openApiPath -PathType Leaf)
    packageFileSha256 = if (Test-Path -LiteralPath $openApiPath -PathType Leaf) { Get-FileSha256 $openApiPath } else { '' }
    manifestSha256 = $expectedOpenApiSha256
    runtimeDocumentSha256 = if ([string]::IsNullOrEmpty($openApiBody)) { '' } else { Get-Sha256String $openApiBody }
    runtimeContractVersion = if ($null -ne $runtimeOpenApi) { [string]$runtimeOpenApi.info.version } else { '' }
    runtimePathCount = if ($null -ne $runtimeOpenApi) { @($runtimeOpenApi.paths.PSObject.Properties).Count } else { 0 }
}
$openApiIdentity.packageMatchesManifest = -not [string]::IsNullOrWhiteSpace($expectedOpenApiSha256) -and $openApiIdentity.packageFileSha256 -eq $expectedOpenApiSha256
$openApiIdentity.runtimeVersionMatchesManifest = -not [string]::IsNullOrWhiteSpace($expectedContractVersion) -and
    [string]::Equals($openApiIdentity.runtimeContractVersion, $expectedContractVersion, [StringComparison]::Ordinal)

$endpointProbes = @(
    $contractProbe,
    $openApiProbe,
    (Invoke-EndpointProbe -Name 'watch-overview' -RelativePath '/api/v2/watch-overview'),
    (Invoke-EndpointProbe -Name 'current-ingest-attention' -RelativePath '/api/v2/current-ingest-attention?pageNumber=1&pageSize=1'),
    (Invoke-EndpointProbe -Name 'externally-readable-demand-catalog' -RelativePath '/api/v2/externally-readable-demand-catalog'),
    (Invoke-EndpointProbe -Name 'demand-series-current-page' -RelativePath '/api/v2/demand-series?presence=VISIBLE&page=1&pageSize=1')
)

$sqlTarget = [ordered]@{ configured = -not [string]::IsNullOrWhiteSpace($connectionString); dataSource = ''; database = ''; integratedSecurity = $false; credentialMode = 'not-configured' }
$sqlConnection = $null
$sqlQueries = New-Object System.Collections.ArrayList
$sqlState = [ordered]@{ connected = $false; classification = 'SQL_UNAVAILABLE'; connectDurationMs = 0; errorType = ''; diagnosticCode = 'SQL_CONNECTION_NOT_CONFIGURED' }
if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
        $sqlTarget.dataSource = $builder.DataSource
        $sqlTarget.database = $builder.InitialCatalog
        $sqlTarget.integratedSecurity = $builder.IntegratedSecurity
        $sqlTarget.credentialMode = if ($builder.IntegratedSecurity) { 'integrated' } elseif (-not [string]::IsNullOrWhiteSpace($builder.UserID)) { 'sql-auth-redacted' } else { 'unspecified' }
        $builder['Connect Timeout'] = $SqlConnectionTimeoutSeconds
        $builder['Application Name'] = 'MesIngest.RuntimeFeedback.ReadOnly'
        $sqlConnection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            $sqlConnection.Open()
            $timer.Stop()
            $sqlState.connected = $true
            $sqlState.classification = 'SUCCESS'
            $sqlState.connectDurationMs = $timer.ElapsedMilliseconds
            $sqlState.diagnosticCode = ''
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'server-and-database-identity' @"
SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) AS machine_name,
       ISNULL(CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), N'MSSQLSERVER') AS instance_name,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS product_version,
       CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
       d.name AS database_name, d.state_desc, d.recovery_model_desc, d.compatibility_level
FROM sys.databases AS d WHERE d.name = DB_NAME();
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'max-server-memory' @"
SELECT name, CAST(value AS bigint) AS configured_mb, CAST(value_in_use AS bigint) AS value_in_use_mb
FROM sys.configurations WHERE name = N'max server memory (MB)';
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'database-files' @"
SELECT name, type_desc, physical_name, CAST(size * 8.0 / 1024.0 AS decimal(19,2)) AS size_mb,
       max_size, growth, is_percent_growth, state_desc
FROM sys.database_files ORDER BY file_id;
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'process-memory' @"
SELECT physical_memory_in_use_kb, locked_page_allocations_kb, large_page_allocations_kb,
       memory_utilization_percentage, process_physical_memory_low, process_virtual_memory_low
FROM sys.dm_os_process_memory;
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'query-memory-grants' @"
SELECT COUNT_BIG(*) AS grant_count,
       SUM(CASE WHEN grant_time IS NULL THEN 1 ELSE 0 END) AS waiting_count,
       SUM(CONVERT(bigint, requested_memory_kb)) AS requested_memory_kb,
       SUM(CONVERT(bigint, granted_memory_kb)) AS granted_memory_kb,
       MAX(wait_time_ms) AS max_wait_time_ms
FROM sys.dm_exec_query_memory_grants;
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'resource-semaphores' @"
SELECT resource_semaphore_id, target_memory_kb, max_target_memory_kb, total_memory_kb,
       available_memory_kb, granted_memory_kb, used_memory_kb, grantee_count, waiter_count,
       timeout_error_count, forced_grant_count
FROM sys.dm_exec_query_resource_semaphores;
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'resource-semaphore-waits' @"
SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type IN (N'RESOURCE_SEMAPHORE', N'RESOURCE_SEMAPHORE_QUERY_COMPILE');
"@))
            [void]$sqlQueries.Add((Invoke-ReadOnlySqlQuery $sqlConnection 'cached-query-spills' @"
SELECT COUNT_BIG(*) AS cached_query_count,
       SUM(CONVERT(bigint, total_spills)) AS total_spills,
       MAX(CONVERT(bigint, last_spills)) AS max_last_spills
FROM sys.dm_exec_query_stats;
"@))
            foreach ($signal in @('Error: 701', 'Error: 17300', 'Error: 17312', 'RESOURCE_SEMAPHORE', 'spill')) {
                $safeName = 'sql-errorlog-' + ($signal.ToLowerInvariant() -replace '[^a-z0-9]+', '-')
                $literal = $signal.Replace("'", "''")
                $errorLogQuery = Invoke-ReadOnlySqlQuery $sqlConnection $safeName "EXEC master.dbo.xp_readerrorlog 0, 1, N'$literal';"
                if ($errorLogQuery.available) {
                    $logDates = @($errorLogQuery.rows | ForEach-Object { $_.LogDate } | Where-Object { $null -ne $_ } | Sort-Object)
                    # Never persist SQL error-log text: it can contain statements or operational
                    # values. A count and time range are sufficient for this signal inventory.
                    $errorLogQuery.rows = @([pscustomobject][ordered]@{
                        signal = $signal
                        count = @($errorLogQuery.rows).Count
                        firstObservedAt = if ($logDates.Count -gt 0) { ([DateTime]$logDates[0]).ToUniversalTime().ToString('o') } else { $null }
                        lastObservedAt = if ($logDates.Count -gt 0) { ([DateTime]$logDates[-1]).ToUniversalTime().ToString('o') } else { $null }
                    })
                }
                [void]$sqlQueries.Add($errorLogQuery)
            }
        } catch {
            $timer.Stop()
            $sqlState.connectDurationMs = $timer.ElapsedMilliseconds
            $sqlState.errorType = $_.Exception.GetType().FullName
            $number = if ($_.Exception.InnerException -is [System.Data.SqlClient.SqlException]) { $_.Exception.InnerException.Number } elseif ($_.Exception -is [System.Data.SqlClient.SqlException]) { $_.Exception.Number } else { 0 }
            $sqlState.diagnosticCode = "SQL_CONNECTION_FAILED_$number"
        }
    } catch {
        $sqlState.errorType = $_.Exception.GetType().FullName
        $sqlState.diagnosticCode = 'SQL_CONNECTION_CONFIGURATION_INVALID'
    } finally {
        if ($null -ne $sqlConnection) { $sqlConnection.Dispose() }
    }
}

$eventStart = (Get-Date).AddHours(-$EventLookbackHours)
$sqlEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $eventStart } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match '^MSSQL' -or $_.Message -match 'SQL Server|sqlservr|RESOURCE_SEMAPHORE' })
$eventSignals = foreach ($signal in @('701', '17300', '17312', 'RESOURCE_SEMAPHORE', 'spill')) {
    Get-ErrorSignalSummary -Events $sqlEvents -Signal $signal
}
$sqlEvidenceAvailability = [ordered]@{
    maxServerMemory = Test-SqlQueryAvailable $sqlQueries 'max-server-memory'
    recoveryModel = Test-SqlQueryAvailable $sqlQueries 'server-and-database-identity'
    databaseFiles = Test-SqlQueryAvailable $sqlQueries 'database-files'
    processMemory = Test-SqlQueryAvailable $sqlQueries 'process-memory'
    memoryGrants = Test-SqlQueryAvailable $sqlQueries 'query-memory-grants'
    resourceSemaphore = Test-SqlQueryAvailable $sqlQueries 'resource-semaphores'
    spillDmv = Test-SqlQueryAvailable $sqlQueries 'cached-query-spills'
}
$lastMemoryChange = $null
$memoryChangeEvent = $sqlEvents | Where-Object {
    $_.Id -eq 15457 -and $_.Message -match "max server memory \(MB\).*changed from ([0-9]+) to ([0-9]+)"
} | Sort-Object TimeCreated -Descending | Select-Object -First 1
if ($null -ne $memoryChangeEvent -and
    $memoryChangeEvent.Message -match "max server memory \(MB\).*changed from ([0-9]+) to ([0-9]+)") {
    $lastMemoryChange = [ordered]@{
        observedAt = $memoryChangeEvent.TimeCreated.ToUniversalTime().ToString('o')
        fromMb = [int]$Matches[1]
        toMb = [int]$Matches[2]
        authority = 'WINDOWS_APPLICATION_EVENT_LAST_OBSERVED_NOT_CURRENT_CONFIGURATION'
    }
}

$tier1SqlConnectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_SQLSERVER')
$tier1SqlTarget = [ordered]@{ dataSource = ''; database = ''; realInstance = $false }
if (-not [string]::IsNullOrWhiteSpace($tier1SqlConnectionString)) {
    try {
        $tier1Builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($tier1SqlConnectionString)
        $tier1SqlTarget.dataSource = $tier1Builder.DataSource
        $tier1SqlTarget.database = $tier1Builder.InitialCatalog
        $tier1SqlTarget.realInstance = $tier1Builder.DataSource.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -lt 0
    } catch { }
}
$testEvidence = [ordered]@{
    provided = -not [string]::IsNullOrWhiteSpace($SqlTestTrxPath)
    trxPath = if ([string]::IsNullOrWhiteSpace($SqlTestTrxPath)) { '' } else { [IO.Path]::GetFullPath($SqlTestTrxPath) }
    total = 0; executed = 0; passed = 0; failed = 0; notExecuted = 0; skipped = 0
    sqlEnvironmentConfigured = -not [string]::IsNullOrWhiteSpace($tier1SqlConnectionString)
    sqlTarget = $tier1SqlTarget
    expectedProductMajor = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR')
    expectedCompatibilityLevel = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL')
    minimumTier1Total = 700
    attestationProvided = -not [string]::IsNullOrWhiteSpace($SqlTestAttestationPath)
    attestationValid = $false
    realSqlTier1Satisfied = $false
    diagnosticCode = if ([string]::IsNullOrWhiteSpace($SqlTestTrxPath)) { 'SQL_TIER1_TRX_NOT_PROVIDED' } else { '' }
    exactCommand = 'dotnet test MesIngest.Tests --configuration Release --results-directory <path> --logger "trx;LogFileName=runtime-feedback-tier1.trx"'
}
if (-not [string]::IsNullOrWhiteSpace($SqlTestTrxPath)) {
    if (Test-Path -LiteralPath $SqlTestTrxPath -PathType Leaf) {
        try {
            [xml]$trx = Get-Content -Raw -LiteralPath $SqlTestTrxPath
            $counters = $trx.TestRun.ResultSummary.Counters
            $testEvidence.total = [int]$counters.total
            $testEvidence.executed = [int]$counters.executed
            $testEvidence.passed = [int]$counters.passed
            $testEvidence.failed = [int]$counters.failed
            $testEvidence.notExecuted = [int]$counters.notExecuted
            # VSTest/xUnit v2 records skipped UnitTestResult rows as NotExecuted but can leave the
            # TRX notExecuted counter at zero. Total - Executed is the authoritative skip count.
            $testEvidence.skipped = $testEvidence.total - $testEvidence.executed
            $storageNames = @($trx.TestRun.TestDefinitions.UnitTest | ForEach-Object { [IO.Path]::GetFileName([string]$_.storage) } | Sort-Object -Unique)
            $trxCandidateValid = $testEvidence.total -ge 700 -and $testEvidence.failed -eq 0 -and
                $testEvidence.skipped -eq 0 -and $storageNames.Count -eq 1 -and
                [string]::Equals($storageNames[0], 'MesIngest.Tests.dll', [StringComparison]::OrdinalIgnoreCase)
            if (-not $trxCandidateValid) { $testEvidence.diagnosticCode = 'REAL_SQL_TIER1_TRX_NOT_FULL_SUITE' }

            if ($trxCandidateValid -and -not [string]::IsNullOrWhiteSpace($SqlTestAttestationPath) -and
                (Test-Path -LiteralPath $SqlTestAttestationPath -PathType Leaf)) {
                try {
                    $attestation = Get-Content -Raw -LiteralPath $SqlTestAttestationPath | ConvertFrom-Json
                    $attestedCompletedAt = [DateTimeOffset]::Parse(
                        [string]$attestation.completedAt,
                        [Globalization.CultureInfo]::InvariantCulture)
                    $attestationAge = [DateTimeOffset]::UtcNow - $attestedCompletedAt.ToUniversalTime()
                    $testEvidence.attestationValid = $attestation.schemaVersion -eq 1 -and
                        [string]::Equals([string]$attestation.commandPattern, $testEvidence.exactCommand, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$attestation.testAssembly, 'MesIngest.Tests.dll', [StringComparison]::OrdinalIgnoreCase) -and
                        [string]::Equals([string]$attestation.platform, 'VSTest', [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$attestation.framework, 'xUnit v2', [StringComparison]::Ordinal) -and
                        $attestation.exitCode -eq 0 -and
                        [string]::Equals([string]$attestation.trxSha256, (Get-FileSha256 $SqlTestTrxPath), [StringComparison]::OrdinalIgnoreCase) -and
                        $attestation.counts.total -eq $testEvidence.total -and
                        $attestation.counts.executed -eq $testEvidence.executed -and
                        $attestation.counts.passed -eq $testEvidence.passed -and
                        $attestation.counts.failed -eq $testEvidence.failed -and
                        $attestation.counts.skipped -eq $testEvidence.skipped -and
                        $attestationAge.TotalHours -ge 0 -and $attestationAge.TotalHours -le 24 -and
                        $testEvidence.sqlEnvironmentConfigured -and $testEvidence.sqlTarget.realInstance -and
                        [string]::Equals([string]$attestation.sqlTarget.dataSource, $testEvidence.sqlTarget.dataSource, [StringComparison]::OrdinalIgnoreCase) -and
                        [string]::Equals([string]$attestation.sqlTarget.database, $testEvidence.sqlTarget.database, [StringComparison]::OrdinalIgnoreCase) -and
                        [string]::Equals([string]$attestation.expectedProductMajor, $testEvidence.expectedProductMajor, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$attestation.expectedCompatibilityLevel, $testEvidence.expectedCompatibilityLevel, [StringComparison]::Ordinal) -and
                        $attestation.actualProductMajor -eq [int]$testEvidence.expectedProductMajor -and
                        $attestation.actualCompatibilityLevel -eq [int]$testEvidence.expectedCompatibilityLevel
                    if (-not $testEvidence.attestationValid) { $testEvidence.diagnosticCode = 'SQL_TIER1_ATTESTATION_MISMATCH' }
                } catch { $testEvidence.diagnosticCode = 'SQL_TIER1_ATTESTATION_INVALID' }
            } elseif ($trxCandidateValid) {
                $testEvidence.diagnosticCode = 'SQL_TIER1_ATTESTATION_NOT_PROVIDED'
            }
            $testEvidence.realSqlTier1Satisfied = $trxCandidateValid -and $testEvidence.attestationValid
        } catch { $testEvidence.diagnosticCode = 'SQL_TIER1_TRX_INVALID' }
    } else { $testEvidence.diagnosticCode = 'SQL_TIER1_TRX_NOT_FOUND' }
}

$worktree = Get-WorktreeInventory
$observations = New-Object System.Collections.ArrayList
if ($null -ne $service -and $service.State -eq 'Running' -and $listeners.Count -gt 0) { [void]$observations.Add('HOST_LISTENING') }
if (-not $contractIdentity.available) { [void]$observations.Add('HOST_UNREACHABLE') }
elseif (-not $contractIdentity.exactPackageMatch) { [void]$observations.Add('CONTRACT_MISMATCH') }
else { [void]$observations.Add('CONTRACT_OK') }
if (-not $sqlState.connected) { [void]$observations.Add('SQL_UNAVAILABLE') } else { [void]$observations.Add('SQL_CONNECTED') }
if (@($endpointProbes | Where-Object classification -eq 'QUERY_TIMEOUT').Count -gt 0) { [void]$observations.Add('QUERY_TIMEOUT') }
if (@($endpointProbes | Where-Object classification -eq 'CONTRACT_MISMATCH').Count -gt 0 -and -not $observations.Contains('CONTRACT_MISMATCH')) { [void]$observations.Add('CONTRACT_MISMATCH') }
$primaryState = if ($observations.Contains('HOST_UNREACHABLE')) { 'HOST_UNREACHABLE' }
elseif ($observations.Contains('CONTRACT_MISMATCH')) { 'CONTRACT_MISMATCH' }
elseif ($observations.Contains('SQL_UNAVAILABLE')) { 'SQL_UNAVAILABLE' }
elseif ($observations.Contains('QUERY_TIMEOUT')) { 'QUERY_TIMEOUT' }
elseif (@($endpointProbes | Where-Object classification -ne 'SUCCESS').Count -gt 0) { 'CURRENT_READ_FAILURE' }
else { 'HEALTHY' }

$report = [ordered]@{
    schemaVersion = 1
    runId = $runId
    startedAt = $startedAt.ToString('o')
    completedAt = [DateTimeOffset]::UtcNow.ToString('o')
    machine = [ordered]@{ name = $env:COMPUTERNAME; os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString() }
    diagnosis = [ordered]@{ primaryState = $primaryState; observations = @($observations) }
    host = [ordered]@{
        service = if ($null -eq $service) { $null } else { [ordered]@{ name = $service.Name; displayName = $service.DisplayName; state = $service.State; startMode = $service.StartMode; startName = $service.StartName; processId = [int]$service.ProcessId; executablePath = Get-SafeExecutablePath ([string]$service.PathName) } }
        process = if ($null -eq $process) { $null } else { [ordered]@{ id = $process.Id; name = $process.ProcessName; workingSetBytes = $process.WorkingSet64; privateMemoryBytes = $process.PrivateMemorySize64; startTime = try { $process.StartTime.ToUniversalTime().ToString('o') } catch { $null } } }
        listeners = @($listeners)
        baseUrl = Get-SafeBaseUrl $BaseUrl
        configuration = [ordered]@{ localFilePresent = Test-Path -LiteralPath $localConfigPath; snapshotSource = if ($null -ne $mesConfig) { [string]$mesConfig.SnapshotSource } else { '' }; hasSqlConnectionString = -not [string]::IsNullOrWhiteSpace($connectionString); hasSharedSecret = -not [string]::IsNullOrWhiteSpace($sharedSecret) }
    }
    deployment = [ordered]@{
        installRoot = $resolvedInstallRoot
        manifestPresent = $null -ne $manifest
        sourceCommit = if ($null -ne $manifest) { [string]$manifest.sourceCommit } else { '' }
        sourceDirty = if ($null -ne $manifest) { [bool]$manifest.sourceDirty } else { $null }
        assemblies = @($assemblies)
        contract = $contractIdentity
        openApi = $openApiIdentity
    }
    endpoints = @($endpointProbes)
    sqlServer = [ordered]@{
        service = if ($null -eq $sqlService) { $null } else { [ordered]@{ name = $sqlService.Name; state = $sqlService.State; startMode = $sqlService.StartMode; startName = $sqlService.StartName; executablePath = Get-SafeExecutablePath ([string]$sqlService.PathName) } }
        target = $sqlTarget
        connection = $sqlState
        evidenceAvailability = $sqlEvidenceAvailability
        queries = @($sqlQueries)
        windowsEventLookbackHours = $EventLookbackHours
        windowsEventSignals = @($eventSignals)
        lastObservedMaxServerMemoryChange = $lastMemoryChange
    }
    tests = $testEvidence
    worktree = $worktree
    safety = [ordered]@{ readOnly = $true; credentialsWritten = $false; connectionStringWritten = $false; serviceStateChanged = $false; databaseChanged = $false; productionConfigurationChanged = $false }
}

$jsonPath = Join-Path $runDirectory 'runtime-feedback.json'
$markdownPath = Join-Path $runDirectory 'runtime-feedback.md'
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add('# MesIngest runtime feedback')
$markdown.Add('')
$markdown.Add("- Run: ``$runId``")
$markdown.Add("- Captured: ``$($report.completedAt)``")
$markdown.Add("- Primary state: **$primaryState**")
$markdown.Add("- Observations: $(@($observations) -join ', ')")
$markdown.Add("- Read only: ``true``; credentials/connection strings written: ``false``")
$markdown.Add('')
$markdown.Add('## Host and contract')
$markdown.Add('')
$markdown.Add('| Check | Result |')
$markdown.Add('| --- | --- |')
$markdown.Add("| Service | $(if ($null -eq $service) { 'not installed' } else { "$($service.State), PID $($service.ProcessId)" }) |")
$markdown.Add("| Listener | $(if ($listeners.Count -eq 0) { 'none for service PID' } else { (@($listeners | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ') }) |")
$markdown.Add("| Contract | $($contractIdentity.classification); ``$($contractIdentity.contractVersion)`` / schema ``$($contractIdentity.schemaVersion)`` |")
$markdown.Add("| Deployment source | ``$(if ($null -ne $manifest) { $manifest.sourceCommit } else { 'not available' })``; dirty at package build: ``$(if ($null -ne $manifest) { $manifest.sourceDirty } else { 'unknown' })`` |")
$markdown.Add('')
$markdown.Add('## Current read probes')
$markdown.Add('')
$markdown.Add('| Endpoint | HTTP | ms | Classification | Code |')
$markdown.Add('| --- | ---: | ---: | --- | --- |')
foreach ($probe in $endpointProbes) { $markdown.Add("| ``$($probe.path)`` | $($probe.statusCode) | $($probe.durationMs) | $($probe.classification) | $($probe.diagnosticCode) |") }
$markdown.Add('')
$markdown.Add('## SQL Server')
$markdown.Add('')
$markdown.Add("- Service: $(if ($null -eq $sqlService) { 'not installed' } else { "$($sqlService.Name) / $($sqlService.State)" })")
$markdown.Add("- Target identity (no credential): ``$($sqlTarget.dataSource)`` / ``$($sqlTarget.database)`` / $($sqlTarget.credentialMode)")
$markdown.Add("- Connection: **$($sqlState.classification)** ($($sqlState.diagnosticCode), $($sqlState.connectDurationMs) ms)")
$markdown.Add("- Current configuration/recovery/files available: $($sqlEvidenceAvailability.maxServerMemory) / $($sqlEvidenceAvailability.recoveryModel) / $($sqlEvidenceAvailability.databaseFiles)")
$markdown.Add("- SQL query evidence objects: $($sqlQueries.Count); Windows SQL signal lookback: $EventLookbackHours hours")
foreach ($signal in $eventSignals) { $markdown.Add("  - $($signal.signal): count=$($signal.count), first=$($signal.firstObservedAt), last=$($signal.lastObservedAt)") }
$markdown.Add('')
$markdown.Add('## Tier 1 SQL test evidence')
$markdown.Add('')
$markdown.Add("- Command: ``$($testEvidence.exactCommand)``")
$markdown.Add("- Failed: **$($testEvidence.failed)**; Passed: **$($testEvidence.passed)**; Skipped: **$($testEvidence.skipped)**; Total: **$($testEvidence.total)**")
$markdown.Add("- Real SQL Tier 1 proven: **$($testEvidence.realSqlTier1Satisfied)** ($($testEvidence.diagnosticCode))")
$markdown.Add('')
$markdown.Add('## Dirty worktree')
$markdown.Add('')
$markdown.Add("- Inventory available: $($worktree.available); entries: $(@($worktree.entries).Count). All entries remain unattributed and preserved.")
$markdown | Set-Content -LiteralPath $markdownPath -Encoding UTF8

$inventory = foreach ($path in @($jsonPath, $markdownPath)) {
    $item = Get-Item -LiteralPath $path
    [pscustomobject][ordered]@{ file = $item.Name; length = $item.Length; sha256 = Get-FileSha256 $path }
}
$inventoryPath = Join-Path $runDirectory 'sha256-inventory.json'
$inventory | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $inventoryPath -Encoding UTF8

Write-Output "MESINGEST_RUNTIME_FEEDBACK_CAPTURED: state=$primaryState run=$runDirectory"
Write-Output "JSON=$jsonPath"
Write-Output "MARKDOWN=$markdownPath"
