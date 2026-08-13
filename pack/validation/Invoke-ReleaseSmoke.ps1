#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ArtifactsDirectory,

    [ValidateRange(5, 30)]
    [int] $StartupTimeoutSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serviceExecutable = Join-Path $packageRoot 'service\MesIngest.Host.exe'
$canonicalQueryRelativePath = 'service/queries/mes-task-union/query.sql'
$canonicalQueryManifestRelativePath = 'service/queries/mes-task-union/query.manifest.json'
$canonicalQuerySha256 = '54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae'
$canonicalQueryVersion = "MES_TASK_UNION/sha256:$canonicalQuerySha256"
$canonicalQueryPath = Join-Path $packageRoot $canonicalQueryRelativePath
$canonicalQueryManifestPath = Join-Path $packageRoot $canonicalQueryManifestRelativePath

foreach ($path in @($serviceExecutable, $canonicalQueryPath, $canonicalQueryManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Production V2 release smoke input missing: $path"
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
    $hostInfo.EnvironmentVariables['MesIngest__EnableLegacyDevelopmentEndpoints'] = 'false'
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
    if ([string]::IsNullOrWhiteSpace([string]$contract.contractVersion) `
        -or [int]$contract.schemaVersion -le 0 `
        -or [string]::IsNullOrWhiteSpace([string]$contract.transportDemandKeyComparison)) {
        throw 'Packaged Production V2 Host returned an incomplete contract identity.'
    }

    $legacyStatus = 0
    try {
        $legacyResponse = Invoke-WebRequest -Uri "$baseUrl/api/contract" -TimeoutSec 2 -UseBasicParsing
        $legacyStatus = [int]$legacyResponse.StatusCode
    } catch {
        if ($null -ne $_.Exception.Response) {
            $legacyStatus = [int]$_.Exception.Response.StatusCode
        } else {
            throw
        }
    }
    if ($legacyStatus -ne 404) {
        throw "Packaged Production V2 Host exposed the legacy contract endpoint (HTTP $legacyStatus)."
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
        transportDemandKeyComparison = [string]$contract.transportDemandKeyComparison
        legacyContractStatus = $legacyStatus
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
    if ($null -ne $hostProcess) {
        if (-not $hostProcess.HasExited) {
            $hostProcess.Kill()
            $hostProcess.WaitForExit(5000) | Out-Null
        }
        $hostProcess.Dispose()
    }
}
