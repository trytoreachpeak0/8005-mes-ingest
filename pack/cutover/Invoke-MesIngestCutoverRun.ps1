#Requires -Version 5.1
<#
.SYNOPSIS
  Runs the one-time MesIngest cutover and deletes only the exact proven old database.

.DESCRIPTION
  Reads only ArchivedDemandKeyTombstone identity facts from the explicit old database,
  seeds them transactionally into the explicit new database, proves the stable count
  and hash, and then requires the stopped old Host, no old business connections, exact
  old/new database identities, three consecutive projections, the V2 Watch APIs, the
  external catalog, and the production reference consumer to agree on one HistoryEpoch.

  Immediately before deletion it re-resolves every destructive gate for the same
  CutoverRunId, writes immutable pre-delete evidence and a Windows event, then creates
  a run-scoped SQL principal with CONTROL on only the proven old database. The principal,
  its mapped user, and all temporary permissions are removed in every exit path.

  The privileged broker is accepted only by this attended one-time entry point. It is
  never passed to Host, Watch, or the reference consumer. This run creates no backup and
  has no retry or rollback path after deletion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$')]
    [string] $CutoverRunId,

    [Parameter(Mandatory = $true)] [string] $OldConnectionString,
    [Parameter(Mandatory = $true)] [string] $NewConnectionString,
    [Parameter(Mandatory = $true)] [string] $PrivilegeConnectionString,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$')]
    [string] $OldDatabaseName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$')]
    [string] $NewDatabaseName,

    [Parameter(Mandatory = $true)] [int] $ExpectedOldSchemaVersion,
    [Parameter(Mandatory = $true)] [string] $ExpectedOldContractVersion,
    [Parameter(Mandatory = $true)] [int] $ExpectedNewSchemaVersion,
    [Parameter(Mandatory = $true)] [string] $ExpectedNewContractVersion,
    [Parameter(Mandatory = $true)] [string] $ExpectedHistoryEpoch,
    [Parameter(Mandatory = $true)] [string] $ExpectedSqlDataDirectory,
    [Parameter(Mandatory = $true)] [string] $OldHostServiceName,
    [Parameter(Mandatory = $true)] [uri] $NewHostBaseUrl,
    [Parameter(Mandatory = $true)] [string] $ReferenceConsumerExecutable,
    [Parameter(Mandatory = $true)] [string] $EvidenceDirectory,
    [string] $SharedSecretEnvironmentVariable,
    [ValidateRange(1, 3600)] [int] $ProjectionWaitTimeoutSeconds = 600,
    [ValidateRange(1, 60)] [int] $ProjectionPollIntervalSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CutoverSqlTools.ps1')

function Open-ExplicitCutoverDatabaseConnection {
    param(
        [Parameter(Mandatory = $true)] [string] $ConnectionString,
        [Parameter(Mandatory = $true)] [string] $ExpectedDatabaseName
    )

    Add-Type -AssemblyName System.Data | Out-Null
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    if (-not [string]::Equals(
            [string]$builder['Initial Catalog'],
            $ExpectedDatabaseName,
            [StringComparison]::Ordinal)) {
        throw 'CUTOVER_CONNECTION_DATABASE_MISMATCH: each connection must name its explicit database.'
    }
    $builder['Pooling'] = $false
    $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.Open()
    return $connection
}

function New-CutoverPassedGate {
    param([string] $Name, [string] $Detail)
    return [pscustomobject]@{
        Name = $Name
        CutoverRunId = $CutoverRunId
        Passed = $true
        Detail = $Detail
    }
}

function Assert-OldCutoverHostStopped {
    $service = Get-Service -Name $OldHostServiceName -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "CUTOVER_OLD_HOST_RUNNING: service $OldHostServiceName is $($service.Status)."
    }
}

function Invoke-CutoverHttpGet {
    param([Net.Http.HttpClient] $Client, [string] $Path)
    $response = $Client.GetAsync($Path).GetAwaiter().GetResult()
    try {
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "CUTOVER_HTTP_GATE_FAILED: GET $Path returned HTTP $([int]$response.StatusCode)."
        }
        return $body
    } finally {
        $response.Dispose()
    }
}

function Invoke-CutoverReferenceConsumer {
    param([string] $ForbiddenTokenFile)

    $arguments = @(
        'verify-cutover',
        '--base-url', $NewHostBaseUrl.AbsoluteUri,
        '--expected-history-epoch', $ExpectedHistoryEpoch,
        '--forbidden-key-token-file', $ForbiddenTokenFile
    )
    if (-not [string]::IsNullOrWhiteSpace($SharedSecretEnvironmentVariable)) {
        $arguments += @('--shared-secret-env', $SharedSecretEnvironmentVariable)
    }

    if (-not (Test-Path -LiteralPath $ReferenceConsumerExecutable -PathType Leaf)) {
        throw "CUTOVER_REFERENCE_CONSUMER_NOT_FOUND: $ReferenceConsumerExecutable"
    }
    if ([IO.Path]::GetExtension($ReferenceConsumerExecutable) -ieq '.dll') {
        $output = @(& dotnet $ReferenceConsumerExecutable @arguments 2>&1)
    } else {
        $output = @(& $ReferenceConsumerExecutable @arguments 2>&1)
    }
    if ($LASTEXITCODE -ne 0) {
        throw "CUTOVER_REFERENCE_CONSUMER_FAILED: exitCode=$LASTEXITCODE."
    }
    try {
        return (($output -join "`n") | ConvertFrom-Json)
    } catch {
        throw 'CUTOVER_REFERENCE_CONSUMER_INVALID_RESULT: the probe did not return JSON.'
    }
}

$startedAt = [DateTimeOffset]::UtcNow
$completedGates = [Collections.Generic.List[object]]::new()
$oldConnection = $null
$newConnection = $null
$http = $null
$forbiddenTokenFile = $null
$tombstoneProof = $null
$oldIdentity = $null
$newIdentity = $null
$provenOldIdentity = $null
$provenNewIdentity = $null
$projectionGate = $null
$postSeedPollTraceHighWater = $null
$evidenceWritten = $false
$preDeleteEvidenceWritten = $false
$permissionLifecycle = [pscustomobject][ordered]@{
    CutoverRunId = $CutoverRunId
    PrincipalName = 'MesIngestCutover_' + $CutoverRunId.Replace('-', '').ToLowerInvariant()
    FinalStatus = 'NOT_GRANTED_BY_THIS_RUN'
    VerifiedAbsent = $false
    Events = @()
}

try {
    $jsonEvidencePath = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) "$CutoverRunId.json"
    $markdownEvidencePath = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) "$CutoverRunId.md"
    $preDeleteJsonEvidencePath = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) "$CutoverRunId.pre-delete.json"
    $preDeleteMarkdownEvidencePath = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) "$CutoverRunId.pre-delete.md"
    if (Test-Path -LiteralPath $jsonEvidencePath -PathType Leaf -ErrorAction SilentlyContinue) {
        throw "CUTOVER_EVIDENCE_EXISTS: evidence for CutoverRunId $CutoverRunId is immutable."
    }
    if (Test-Path -LiteralPath $markdownEvidencePath -PathType Leaf -ErrorAction SilentlyContinue) {
        throw "CUTOVER_EVIDENCE_EXISTS: evidence for CutoverRunId $CutoverRunId is immutable."
    }
    if ((Test-Path -LiteralPath $preDeleteJsonEvidencePath -PathType Leaf -ErrorAction SilentlyContinue) -or
        (Test-Path -LiteralPath $preDeleteMarkdownEvidencePath -PathType Leaf -ErrorAction SilentlyContinue)) {
        throw "CUTOVER_EVIDENCE_EXISTS: pre-delete evidence for CutoverRunId $CutoverRunId is immutable."
    }

    Assert-OldCutoverHostStopped
    $completedGates.Add((New-CutoverPassedGate -Name 'OLD_HOST_STOPPED' -Detail "service=$OldHostServiceName;status=Stopped"))

    $oldConnection = Open-ExplicitCutoverDatabaseConnection `
        -ConnectionString $OldConnectionString `
        -ExpectedDatabaseName $OldDatabaseName
    $newConnection = Open-ExplicitCutoverDatabaseConnection `
        -ConnectionString $NewConnectionString `
        -ExpectedDatabaseName $NewDatabaseName
    $oldIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $oldConnection
    $newIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $newConnection
    $provenOldIdentity = $oldIdentity
    $provenNewIdentity = $newIdentity
    $identityGates = @(Assert-CutoverDatabaseIdentityPolicy `
        -CutoverRunId $CutoverRunId `
        -OldIdentity $oldIdentity `
        -NewIdentity $newIdentity `
        -ExpectedOldDatabaseName $OldDatabaseName `
        -ExpectedNewDatabaseName $NewDatabaseName `
        -ExpectedOldSchemaVersion $ExpectedOldSchemaVersion `
        -ExpectedOldContractVersion $ExpectedOldContractVersion `
        -ExpectedNewSchemaVersion $ExpectedNewSchemaVersion `
        -ExpectedNewContractVersion $ExpectedNewContractVersion `
        -ExpectedHistoryEpoch $ExpectedHistoryEpoch `
        -ExpectedSqlDataDirectory $ExpectedSqlDataDirectory)
    foreach ($gate in $identityGates) { $completedGates.Add($gate) }

    $tombstones = @(Get-CutoverArchivedTombstones -Connection $oldConnection)
    $tombstoneProof = Get-CutoverTombstoneProof -Tombstones $tombstones
    $seed = Invoke-CutoverTombstoneSeed `
        -Connection $newConnection `
        -Tombstones $tombstones `
        -ExpectedProof $tombstoneProof
    if ($seed.Proof.AlgorithmVersion -cne $tombstoneProof.AlgorithmVersion -or
        $seed.Proof.KeyTokenAlgorithmVersion -cne $tombstoneProof.KeyTokenAlgorithmVersion -or
        $seed.Proof.Count -ne $tombstoneProof.Count -or
        $seed.Proof.Sha256 -cne $tombstoneProof.Sha256) {
        throw 'CUTOVER_TOMBSTONE_PROOF_MISMATCH: export, import, and verification differ.'
    }
    $completedGates.Add((New-CutoverPassedGate `
        -Name 'TOMBSTONE_PROOF_MATCH' `
        -Detail "algorithm=$($tombstoneProof.AlgorithmVersion);keyAlgorithm=$($tombstoneProof.KeyTokenAlgorithmVersion);count=$($tombstoneProof.Count);sha256=$($tombstoneProof.Sha256)"))

    # Only rounds committed after this post-seed high-water can belong to this run's
    # causal proof. The foreground wait is bounded and never schedules a retry.
    $postSeedPollTraceHighWater = Get-CutoverPollTraceHighWater -Connection $newConnection
    $projectionDeadline = [DateTimeOffset]::UtcNow.AddSeconds($ProjectionWaitTimeoutSeconds)
    while ($null -eq $projectionGate) {
        try {
            $projectionGate = Get-CutoverThreeProjectionGate `
                -Connection $newConnection `
                -CutoverRunId $CutoverRunId `
                -ExpectedHistoryEpoch $ExpectedHistoryEpoch `
                -AfterPollTraceSequence $postSeedPollTraceHighWater
        } catch {
            if ($_.Exception.Message -notmatch '^CUTOVER_THREE_PROJECTIONS_') { throw }
            if ([DateTimeOffset]::UtcNow -ge $projectionDeadline) {
                throw "CUTOVER_THREE_PROJECTIONS_TIMEOUT: no three consecutive post-seed successes arrived within $ProjectionWaitTimeoutSeconds seconds."
            }
            Start-Sleep -Seconds $ProjectionPollIntervalSeconds
        }
    }
    $completedGates.Add($projectionGate.Gate)

    $http = [Net.Http.HttpClient]::new()
    $http.BaseAddress = $NewHostBaseUrl
    $http.Timeout = [TimeSpan]::FromSeconds(30)
    if (-not [string]::IsNullOrWhiteSpace($SharedSecretEnvironmentVariable)) {
        $secret = [Environment]::GetEnvironmentVariable($SharedSecretEnvironmentVariable)
        if ([string]::IsNullOrWhiteSpace($secret)) {
            throw 'CUTOVER_SHARED_SECRET_MISSING: the named environment variable is empty.'
        }
        $http.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $secret)
    }

    $contractBody = Invoke-CutoverHttpGet -Client $http -Path '/api/v2/contract'
    $contract = $contractBody | ConvertFrom-Json
    $expectedCapabilities = @(
        'CONTRACT_DISCOVERY/2.0', 'CURRENT_INGEST_ATTENTION/2.0', 'DEMAND_SERIES/2.0',
        'ERROR_SEARCH/2.1', 'EXTERNALLY_READABLE_DEMAND_CATALOG/2.0',
        'POLL_HEALTH_AND_EVIDENCE/2.0', 'READABILITY_AUDIT/2.0',
        'SERIES_ERROR_CATALOG/2.0', 'WATCH_OVERVIEW/2.0'
    ) | Sort-Object
    $actualCapabilities = @($contract.capabilities | ForEach-Object { "$($_.id)/$($_.version)" }) | Sort-Object
    if ([string]$contract.contractVersion -cne $ExpectedNewContractVersion -or
        [int]$contract.schemaVersion -ne $ExpectedNewSchemaVersion -or
        ($actualCapabilities -join "`n") -cne ($expectedCapabilities -join "`n")) {
        throw 'CUTOVER_HTTP_CONTRACT_MISMATCH: the Host does not expose the exact V2 identity.'
    }
    $openApiBody = Invoke-CutoverHttpGet -Client $http -Path '/openapi/v2.json'
    if ($openApiBody -notmatch [regex]::Escape($ExpectedNewContractVersion)) {
        throw 'CUTOVER_OPENAPI_CONTRACT_MISMATCH: the V2 OpenAPI identity is not exact.'
    }

    $watchPaths = @(
        '/api/v2/watch-overview',
        '/api/v2/current-ingest-attention?pageSize=1&pageNumber=1',
        '/api/v2/demand-series?pageSize=1&page=1',
        '/api/v2/readability-audit?pageSize=1&page=1',
        '/api/v2/error-search?pageSize=1&page=1'
    )
    $watchOverviewBody = $null
    foreach ($path in $watchPaths) {
        $body = Invoke-CutoverHttpGet -Client $http -Path $path
        if ($body -notmatch [regex]::Escape($ExpectedHistoryEpoch)) {
            throw "CUTOVER_WATCH_HISTORY_EPOCH_MISMATCH: GET $path belongs to another HistoryEpoch."
        }
        if ($path -eq '/api/v2/watch-overview') { $watchOverviewBody = $body }
    }
    if ($watchOverviewBody -notmatch [regex]::Escape([string]$projectionGate.LatestProjectionCommitId)) {
        throw 'CUTOVER_WATCH_PROJECTION_MISMATCH: Watch overview is not on the proven projection.'
    }
    $completedGates.Add((New-CutoverPassedGate `
        -Name 'WATCH_API' `
        -Detail "paths=$($watchPaths.Count);projectionCommitId=$($projectionGate.LatestProjectionCommitId)"))

    $forbiddenTokenFile = [IO.Path]::GetTempFileName()
    @($tombstones | ForEach-Object { $_.KeyToken }) |
        Set-Content -LiteralPath $forbiddenTokenFile -Encoding ascii
    $reference = Invoke-CutoverReferenceConsumer -ForbiddenTokenFile $forbiddenTokenFile
    if ([string]$reference.historyEpoch -cne $ExpectedHistoryEpoch -or
        [string]$reference.ProjectionCommitId -cne [string]$projectionGate.LatestProjectionCommitId -or
        -not [bool]$reference.TombstoneKeysExcluded) {
        throw 'CUTOVER_REFERENCE_CONSUMER_MISMATCH: the reference consumer did not read the proven projection.'
    }
    $completedGates.Add((New-CutoverPassedGate `
        -Name 'EXTERNAL_CATALOG' `
        -Detail "itemCount=$([int]$reference.ItemCount);projectionCommitId=$([string]$reference.ProjectionCommitId)"))
    $completedGates.Add((New-CutoverPassedGate `
        -Name 'REFERENCE_CONSUMER' `
        -Detail "historyEpoch=$ExpectedHistoryEpoch;projectionCommitId=$([string]$reference.ProjectionCommitId)"))
    $completedGates.Add((New-CutoverPassedGate `
        -Name 'TOMBSTONE_KEYS_EXCLUDED' `
        -Detail "tombstoneCount=$($tombstoneProof.Count);excluded=True"))

    # Re-resolve the destructive-safety facts immediately before the passed evidence.
    # A restarted old Host, a new old-database session, changed identity, or changed
    # tombstone set invalidates this run rather than leaving a stale early gate.
    Assert-OldCutoverHostStopped
    $finalOldIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $oldConnection
    $finalNewIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $newConnection
    Assert-CutoverDatabaseIdentityPolicy `
        -CutoverRunId $CutoverRunId `
        -OldIdentity $finalOldIdentity `
        -NewIdentity $finalNewIdentity `
        -ExpectedOldDatabaseName $OldDatabaseName `
        -ExpectedNewDatabaseName $NewDatabaseName `
        -ExpectedOldSchemaVersion $ExpectedOldSchemaVersion `
        -ExpectedOldContractVersion $ExpectedOldContractVersion `
        -ExpectedNewSchemaVersion $ExpectedNewSchemaVersion `
        -ExpectedNewContractVersion $ExpectedNewContractVersion `
        -ExpectedHistoryEpoch $ExpectedHistoryEpoch `
        -ExpectedSqlDataDirectory $ExpectedSqlDataDirectory | Out-Null
    $finalOldProof = Get-CutoverTombstoneProof -Tombstones @(
        Get-CutoverArchivedTombstones -Connection $oldConnection)
    $finalNewProof = Get-CutoverTombstoneProof -Tombstones @(
        Get-CutoverArchivedTombstones -Connection $newConnection)
    foreach ($finalProof in @($finalOldProof, $finalNewProof)) {
        if ($finalProof.AlgorithmVersion -cne $tombstoneProof.AlgorithmVersion -or
            $finalProof.KeyTokenAlgorithmVersion -cne $tombstoneProof.KeyTokenAlgorithmVersion -or
            $finalProof.Count -ne $tombstoneProof.Count -or
            $finalProof.Sha256 -cne $tombstoneProof.Sha256) {
            throw 'CUTOVER_FINAL_TOMBSTONE_PROOF_MISMATCH: a tombstone set changed before final evidence.'
        }
    }
    $oldIdentity = $finalOldIdentity
    $newIdentity = $finalNewIdentity

    $gateSet = Assert-CutoverGateSet -CutoverRunId $CutoverRunId -GateResults @($completedGates)
    $authorization = Assert-CutoverDeleteAuthorization `
        -CutoverRunId $CutoverRunId `
        -GateResults @($completedGates) `
        -ProvenOldIdentity $provenOldIdentity `
        -CurrentOldIdentity $finalOldIdentity `
        -ProvenNewIdentity $provenNewIdentity `
        -CurrentNewIdentity $finalNewIdentity `
        -ProvenTombstoneProof $tombstoneProof `
        -CurrentOldTombstoneProof $finalOldProof `
        -CurrentNewTombstoneProof $finalNewProof `
        -ExpectedOldDatabaseName $OldDatabaseName `
        -ExpectedNewDatabaseName $NewDatabaseName `
        -ExpectedSqlDataDirectory $ExpectedSqlDataDirectory

    $preDeleteEvidence = [ordered]@{
        SchemaVersion = 2
        CutoverRunId = $CutoverRunId
        Status = 'DELETE_AUTHORIZED_BEFORE_ELEVATION'
        StartedAtUtc = $startedAt.ToString('o')
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Operator = "$env:USERDOMAIN\$env:USERNAME"
        ExecutionLogin = $oldIdentity.ExecutionLogin
        HistoryEpoch = $ExpectedHistoryEpoch
        ContractVersion = $ExpectedNewContractVersion
        SchemaIdentity = $ExpectedNewSchemaVersion
        OldDatabase = [ordered]@{
            ServerIdentity = $oldIdentity.ServerIdentity
            DatabaseName = $oldIdentity.DatabaseName
            DatabaseId = $oldIdentity.DatabaseId
            CreateDateUtc = $oldIdentity.CreateDateUtc
            ContractVersion = $oldIdentity.ContractVersion
            SchemaIdentity = $oldIdentity.SchemaVersion
            DataDirectories = $oldIdentity.DataDirectories
            ForeignSessionCount = $oldIdentity.ForeignSessionCount
        }
        NewDatabase = [ordered]@{
            ServerIdentity = $newIdentity.ServerIdentity
            DatabaseName = $newIdentity.DatabaseName
            DatabaseId = $newIdentity.DatabaseId
            CreateDateUtc = $newIdentity.CreateDateUtc
            ContractVersion = $newIdentity.ContractVersion
            SchemaIdentity = $newIdentity.SchemaVersion
            DataDirectories = $newIdentity.DataDirectories
        }
        TombstoneProof = [ordered]@{
            AlgorithmVersion = $tombstoneProof.AlgorithmVersion
            KeyTokenAlgorithmVersion = $tombstoneProof.KeyTokenAlgorithmVersion
            Count = $tombstoneProof.Count
            Sha256 = $tombstoneProof.Sha256
            InsertedCount = $seed.InsertedCount
            ExistingCount = $seed.ExistingCount
        }
        ProjectionRounds = @($projectionGate.Rounds | ForEach-Object {
            [ordered]@{
                PollTraceSequence = $_.PollTraceSequence
                ProjectionCommitId = $_.ProjectionCommitId
                ProjectionSequence = $_.ProjectionSequence
                HistoryEpoch = $_.HistoryEpoch
            }
        })
        PostSeedPollTraceHighWater = $postSeedPollTraceHighWater
        Gates = @($completedGates)
        GateCount = $gateSet.Count
        DeleteAuthorization = $authorization
        PermissionLifecycle = $permissionLifecycle
        DatabaseDeletion = 'PENDING_EXACT_PROVEN_OLD_DATABASE'
        IsDatabaseBackup = $false
        HasRollbackPath = $false
    }
    Write-CutoverWindowsEvent `
        -CutoverRunId $CutoverRunId `
        -Status $preDeleteEvidence.Status `
        -HistoryEpoch $ExpectedHistoryEpoch `
        -TombstoneSha256 $tombstoneProof.Sha256 `
        -KeyTokenAlgorithmVersion $tombstoneProof.KeyTokenAlgorithmVersion `
        -DatabaseDeletion $preDeleteEvidence.DatabaseDeletion `
        -PermissionStatus $permissionLifecycle.FinalStatus `
        -ServerIdentity $oldIdentity.ServerIdentity `
        -OldDatabaseIdentity "$($oldIdentity.DatabaseName)/$($oldIdentity.DatabaseId)/$($oldIdentity.CreateDateUtc)" `
        -NewDatabaseIdentity "$($newIdentity.DatabaseName)/$($newIdentity.DatabaseId)/$($newIdentity.CreateDateUtc)" `
        -ContractSchemaIdentity "$ExpectedNewContractVersion/$ExpectedNewSchemaVersion" `
        -ProjectionIdentity "$($projectionGate.LatestProjectionCommitId)/$($projectionGate.LatestProjectionSequence)" `
        -InterfaceGateSummary "gateCount=$($gateSet.Count);watch=passed;catalog=passed;reference=passed" `
        -ExecutionIdentity "$env:USERDOMAIN\$env:USERNAME|$($oldIdentity.ExecutionLogin)" `
        -StartedAtUtc $preDeleteEvidence.StartedAtUtc `
        -CompletedAtUtc $preDeleteEvidence.CompletedAtUtc
    $preDeletePaths = Write-CutoverEvidence `
        -EvidenceDirectory $EvidenceDirectory `
        -Evidence $preDeleteEvidence `
        -Stage 'pre-delete'
    $preDeleteEvidenceWritten = $true

    $oldConnection.Dispose()
    $oldConnection = $null
    $newConnection.Dispose()
    $newConnection = $null
    [Data.SqlClient.SqlConnection]::ClearAllPools()
    $deletion = Invoke-CutoverProvenOldDatabaseDeletion `
        -CutoverRunId $CutoverRunId `
        -PrivilegeConnectionString $PrivilegeConnectionString `
        -EvidenceDirectory $EvidenceDirectory `
        -OldHostServiceName $OldHostServiceName `
        -GateResults @($completedGates) `
        -ProvenOldIdentity $provenOldIdentity `
        -CurrentOldIdentity $finalOldIdentity `
        -ProvenNewIdentity $provenNewIdentity `
        -CurrentNewIdentity $finalNewIdentity `
        -ProvenTombstoneProof $tombstoneProof `
        -CurrentOldTombstoneProof $finalOldProof `
        -CurrentNewTombstoneProof $finalNewProof `
        -ExpectedOldDatabaseName $OldDatabaseName `
        -ExpectedNewDatabaseName $NewDatabaseName `
        -ExpectedSqlDataDirectory $ExpectedSqlDataDirectory
    $permissionLifecycle = $deletion.PermissionLifecycle

    $evidence = [ordered]@{} + $preDeleteEvidence
    $evidence.Status = 'PASSED_OLD_DATABASE_DELETED'
    $evidence.CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $evidence.PermissionLifecycle = $permissionLifecycle
    $evidence.DatabaseDeletion = 'DELETED_EXACT_PROVEN_OLD_DATABASE'
    Write-CutoverWindowsEvent `
        -CutoverRunId $CutoverRunId `
        -Status $evidence.Status `
        -HistoryEpoch $ExpectedHistoryEpoch `
        -TombstoneSha256 $tombstoneProof.Sha256 `
        -KeyTokenAlgorithmVersion $tombstoneProof.KeyTokenAlgorithmVersion `
        -DatabaseDeletion $evidence.DatabaseDeletion `
        -PermissionStatus $permissionLifecycle.FinalStatus `
        -ServerIdentity $oldIdentity.ServerIdentity `
        -OldDatabaseIdentity "$($oldIdentity.DatabaseName)/$($oldIdentity.DatabaseId)/$($oldIdentity.CreateDateUtc)" `
        -NewDatabaseIdentity "$($newIdentity.DatabaseName)/$($newIdentity.DatabaseId)/$($newIdentity.CreateDateUtc)" `
        -ContractSchemaIdentity "$ExpectedNewContractVersion/$ExpectedNewSchemaVersion" `
        -ProjectionIdentity "$($projectionGate.LatestProjectionCommitId)/$($projectionGate.LatestProjectionSequence)" `
        -InterfaceGateSummary "gateCount=$($gateSet.Count);watch=passed;catalog=passed;reference=passed" `
        -ExecutionIdentity "$env:USERDOMAIN\$env:USERNAME|$($oldIdentity.ExecutionLogin)" `
        -StartedAtUtc $evidence.StartedAtUtc `
        -CompletedAtUtc $evidence.CompletedAtUtc
    $paths = Write-CutoverEvidence -EvidenceDirectory $EvidenceDirectory -Evidence $evidence
    $evidenceWritten = $true

    Write-Host "MESINGEST_CUTOVER_RUN_PASSED: CutoverRunId=$CutoverRunId"
    Write-Host "CUTOVER_PRE_DELETE_EVIDENCE_JSON: $($preDeletePaths.JsonPath)"
    Write-Host "CUTOVER_PRE_DELETE_EVIDENCE_MARKDOWN: $($preDeletePaths.MarkdownPath)"
    Write-Host "CUTOVER_EVIDENCE_JSON: $($paths.JsonPath)"
    Write-Host "CUTOVER_EVIDENCE_MARKDOWN: $($paths.MarkdownPath)"
    Write-Host "DATABASE_DELETION: $($evidence.DatabaseDeletion)"
}
catch {
    $primary = $_
    $errorCode = if ($primary.Exception.Message -match '^([A-Z0-9_]+):') {
        $Matches[1]
    } else {
        'CUTOVER_UNEXPECTED_FAILURE'
    }
    if (-not $evidenceWritten) {
        try {
            $errorPermissionLifecycle = $primary.Exception.Data['MesIngest.CutoverPermissionLifecycle']
            if ($null -ne $errorPermissionLifecycle) {
                $permissionLifecycle = $errorPermissionLifecycle
            }
            $deleteCompleted = @($permissionLifecycle.Events | Where-Object {
                [string]$_.Action -ceq 'EXACT_DATABASE_DELETED'
            }).Count -gt 0
            $databaseDeletion = if ($deleteCompleted) {
                'DELETED_EXACT_PROVEN_OLD_DATABASE'
            } elseif ($preDeleteEvidenceWritten) {
                'DELETE_FAILED_OLD_DATABASE_PRESERVED'
            } else {
                'NOT_ATTEMPTED_GATE_FAILURE'
            }
            $failureProof = if ($null -eq $tombstoneProof) {
                [pscustomobject]@{
                    AlgorithmVersion = 'NOT_COMPUTED'
                    KeyTokenAlgorithmVersion = 'NOT_COMPUTED'
                    Count = 0
                    Sha256 = ('0' * 64)
                }
            } else { $tombstoneProof }
            $failureEvidence = [ordered]@{
                SchemaVersion = 2
                CutoverRunId = $CutoverRunId
                Status = if ($preDeleteEvidenceWritten) { 'FAILED_AFTER_DELETE_AUTHORIZATION' } else { 'FAILED_BEFORE_DELETE_AUTHORIZATION' }
                StartedAtUtc = $startedAt.ToString('o')
                CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                Operator = "$env:USERDOMAIN\$env:USERNAME"
                HistoryEpoch = $ExpectedHistoryEpoch
                ErrorCode = $errorCode
                TombstoneProof = [ordered]@{
                    AlgorithmVersion = $failureProof.AlgorithmVersion
                    KeyTokenAlgorithmVersion = $failureProof.KeyTokenAlgorithmVersion
                    Count = $failureProof.Count
                    Sha256 = $failureProof.Sha256
                }
                GateCount = $completedGates.Count
                Gates = @($completedGates)
                PermissionLifecycle = $permissionLifecycle
                DatabaseDeletion = $databaseDeletion
                IsDatabaseBackup = $false
                HasRollbackPath = $false
            }
            try {
                Write-CutoverWindowsEvent `
                    -CutoverRunId $CutoverRunId `
                    -Status $failureEvidence.Status `
                    -HistoryEpoch $ExpectedHistoryEpoch `
                    -TombstoneSha256 $failureProof.Sha256 `
                    -KeyTokenAlgorithmVersion $failureProof.KeyTokenAlgorithmVersion `
                    -DatabaseDeletion $failureEvidence.DatabaseDeletion `
                    -PermissionStatus $permissionLifecycle.FinalStatus
            } catch {
                $primary.Exception.Data['MesIngest.CutoverEventLogFailure'] = $_.Exception.Message
            }
            Write-CutoverEvidence -EvidenceDirectory $EvidenceDirectory -Evidence $failureEvidence | Out-Null
        } catch {
            $primary.Exception.Data['MesIngest.CutoverEvidenceFailure'] = $_.Exception.Message
        }
    }
    throw $primary
}
finally {
    if ($null -ne $http) { $http.Dispose() }
    if ($null -ne $oldConnection) { $oldConnection.Dispose() }
    if ($null -ne $newConnection) { $newConnection.Dispose() }
    [Data.SqlClient.SqlConnection]::ClearAllPools()
    if ($null -ne $forbiddenTokenFile -and (Test-Path -LiteralPath $forbiddenTokenFile)) {
        Remove-Item -LiteralPath $forbiddenTokenFile -Force
    }
}
