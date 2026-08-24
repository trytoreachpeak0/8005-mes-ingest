#Requires -Version 5.1
<#
.SYNOPSIS
  Shared SQL helpers for the attended cutover and rollback drills.

.DESCRIPTION
  Every function here is dot-sourced by the two drill entry points. None of them is
  reachable from the Host, the Watch client, or install/uninstall, and none of them
  drops or overwrites a database without the caller having passed
  Assert-CutoverOperatorConfirmation for that exact resolved identity first.
#>

Set-StrictMode -Version Latest

function Get-CutoverRequiredGateNames {
    [CmdletBinding()]
    param()

    return @(
        'OLD_HOST_STOPPED',
        'OLD_DATABASE_NO_BUSINESS_CONNECTIONS',
        'TOMBSTONE_PROOF_MATCH',
        'CONTRACT_SCHEMA_EXACT',
        'THREE_CONSECUTIVE_PROJECTIONS',
        'WATCH_API',
        'EXTERNAL_CATALOG',
        'REFERENCE_CONSUMER',
        'TOMBSTONE_KEYS_EXCLUDED',
        'OLD_DATABASE_IDENTITY',
        'RUN_IDENTITY_NO_DELETE_PERMISSION'
    )
}

function Assert-CutoverGateSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$')]
        [string] $CutoverRunId,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $GateResults
    )

    $required = @(Get-CutoverRequiredGateNames)
    $byName = @{}
    foreach ($gate in $GateResults) {
        if ($null -eq $gate -or [string]::IsNullOrWhiteSpace([string]$gate.Name)) {
            throw 'CUTOVER_GATE_INVALID: every gate result must have a name.'
        }
        $name = [string]$gate.Name
        if ($name -cnotin $required) {
            throw "CUTOVER_GATE_UNEXPECTED: $name is not a Ticket 23 gate."
        }
        if ($byName.ContainsKey($name)) {
            throw "CUTOVER_GATE_DUPLICATE: $name appears more than once."
        }
        if ([string]$gate.CutoverRunId -cne $CutoverRunId) {
            throw "CUTOVER_GATE_RUN_ID_MISMATCH: $name belongs to another CutoverRunId."
        }
        if (-not [bool]$gate.Passed) {
            throw "CUTOVER_GATE_FAILED: $name did not pass."
        }
        $byName.Add($name, $gate)
    }

    $missing = @($required | Where-Object { -not $byName.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        throw "CUTOVER_GATE_SET_INCOMPLETE: missing $($missing -join ', ')."
    }

    return [pscustomobject][ordered]@{
        CutoverRunId = $CutoverRunId
        Count = $required.Count
        Names = $required
    }
}

function Assert-CutoverDatabaseIdentityPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $CutoverRunId,
        [Parameter(Mandatory = $true)] $OldIdentity,
        [Parameter(Mandatory = $true)] $NewIdentity,
        [Parameter(Mandatory = $true)] [string] $ExpectedOldDatabaseName,
        [Parameter(Mandatory = $true)] [string] $ExpectedNewDatabaseName,
        [Parameter(Mandatory = $true)] [int] $ExpectedOldSchemaVersion,
        [Parameter(Mandatory = $true)] [string] $ExpectedOldContractVersion,
        [Parameter(Mandatory = $true)] [int] $ExpectedNewSchemaVersion,
        [Parameter(Mandatory = $true)] [string] $ExpectedNewContractVersion,
        [Parameter(Mandatory = $true)] [string] $ExpectedHistoryEpoch,
        [Parameter(Mandatory = $true)] [string] $ExpectedSqlDataDirectory
    )

    $expectedDirectory = [IO.Path]::GetFullPath($ExpectedSqlDataDirectory).TrimEnd('\', '/')
    $oldDirectories = @($OldIdentity.DataDirectories | ForEach-Object {
        [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
    } | Sort-Object -Unique)
    $newDirectories = @($NewIdentity.DataDirectories | ForEach-Object {
        [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
    } | Sort-Object -Unique)

    if (-not [string]::Equals(
            [string]$OldIdentity.DatabaseName,
            $ExpectedOldDatabaseName,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$NewIdentity.DatabaseName,
            $ExpectedNewDatabaseName,
            [StringComparison]::Ordinal)) {
        throw 'CUTOVER_DATABASE_NAME_MISMATCH: the resolved names are not the explicit targets.'
    }
    if ([string]::Equals(
            [string]$OldIdentity.DatabaseName,
            [string]$NewIdentity.DatabaseName,
            [StringComparison]::OrdinalIgnoreCase) -or
        [int]$OldIdentity.DatabaseId -eq [int]$NewIdentity.DatabaseId) {
        throw 'CUTOVER_DATABASES_NOT_DISTINCT: the old and new database resolve to one identity.'
    }
    if ([bool]$OldIdentity.IsSystemDatabase -or [int]$OldIdentity.DatabaseId -le 4) {
        throw 'CUTOVER_OLD_DATABASE_IS_SYSTEM: a system database can never be a cutover target.'
    }
    if ([bool]$NewIdentity.IsSystemDatabase -or [int]$NewIdentity.DatabaseId -le 4) {
        throw 'CUTOVER_NEW_DATABASE_IS_SYSTEM: a system database can never be the new target.'
    }
    if ([string]$OldIdentity.ServerIdentity -cne [string]$NewIdentity.ServerIdentity) {
        throw 'CUTOVER_SERVER_IDENTITY_MISMATCH: old and new databases must be proven on one instance.'
    }
    if ([int]$OldIdentity.SchemaVersion -ne $ExpectedOldSchemaVersion -or
        [string]$OldIdentity.ContractVersion -cne $ExpectedOldContractVersion -or
        [int]$NewIdentity.SchemaVersion -ne $ExpectedNewSchemaVersion -or
        [string]$NewIdentity.ContractVersion -cne $ExpectedNewContractVersion) {
        throw 'CUTOVER_CONTRACT_SCHEMA_MISMATCH: an exact old or new contract identity did not match.'
    }
    if ([string]$NewIdentity.HistoryEpoch -cne $ExpectedHistoryEpoch) {
        throw 'CUTOVER_HISTORY_EPOCH_MISMATCH: the new database belongs to another HistoryEpoch.'
    }
    if ($oldDirectories.Count -ne 1 -or $newDirectories.Count -ne 1 -or
        -not [string]::Equals($oldDirectories[0], $expectedDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($newDirectories[0], $expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CUTOVER_SQL_DATA_DIRECTORY_MISMATCH: database files are outside the resolved expected data directory.'
    }
    if ([int]$OldIdentity.ForeignSessionCount -ne 0) {
        throw 'CUTOVER_OLD_DATABASE_STILL_IN_USE: the old database has a foreign business connection.'
    }
    if (-not [bool]$OldIdentity.HasGlobalSessionVisibility) {
        throw 'CUTOVER_SESSION_VISIBILITY_REQUIRED: the run identity cannot prove all SQL Server sessions.'
    }
    if ([bool]$OldIdentity.HasDeletePermission -or [bool]$NewIdentity.HasDeletePermission) {
        throw 'CUTOVER_RUN_IDENTITY_CAN_DELETE_DATABASE: Ticket 23 must run without database deletion permission.'
    }

    return @(
        [pscustomobject]@{
            Name = 'OLD_DATABASE_NO_BUSINESS_CONNECTIONS'; CutoverRunId = $CutoverRunId
            Passed = $true; Detail = 'foreignSessionCount=0'
        },
        [pscustomobject]@{
            Name = 'CONTRACT_SCHEMA_EXACT'; CutoverRunId = $CutoverRunId
            Passed = $true; Detail = "old=$ExpectedOldContractVersion/$ExpectedOldSchemaVersion;new=$ExpectedNewContractVersion/$ExpectedNewSchemaVersion"
        },
        [pscustomobject]@{
            Name = 'OLD_DATABASE_IDENTITY'; CutoverRunId = $CutoverRunId
            Passed = $true; Detail = "$($OldIdentity.ServerIdentity)/$ExpectedOldDatabaseName@$expectedDirectory"
        },
        [pscustomobject]@{
            Name = 'RUN_IDENTITY_NO_DELETE_PERMISSION'; CutoverRunId = $CutoverRunId
            Passed = $true; Detail = 'no CONTROL database or ALTER ANY DATABASE permission'
        }
    )
}

function Write-CutoverEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $EvidenceDirectory,
        [Parameter(Mandatory = $true)] $Evidence
    )

    $runId = [string]$Evidence.CutoverRunId
    if ($runId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'CUTOVER_EVIDENCE_INVALID: CutoverRunId is missing or invalid.'
    }
    $directory = [IO.Path]::GetFullPath($EvidenceDirectory)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $jsonPath = Join-Path $directory "$runId.json"
    $markdownPath = Join-Path $directory "$runId.md"
    if ([IO.File]::Exists($jsonPath) -or [IO.File]::Exists($markdownPath)) {
        throw "CUTOVER_EVIDENCE_EXISTS: evidence for CutoverRunId $runId is immutable."
    }

    $json = $Evidence | ConvertTo-Json -Depth 12
    if ($json -match '(?i)"(WorkType|Sublot|BusinessPayload|RawObservation|DemandRawObservation)"\s*:') {
        throw 'CUTOVER_EVIDENCE_CONTAINS_BUSINESS_DATA: evidence contains a forbidden business field.'
    }
    $markdown = @(
        '# MesIngest CutoverRun evidence'
        ''
        "- CutoverRunId: $runId"
        "- Status: $([string]$Evidence.Status)"
        "- HistoryEpoch: $([string]$Evidence.HistoryEpoch)"
        "- Tombstone algorithm: $([string]$Evidence.TombstoneProof.AlgorithmVersion)"
        "- TransportDemandKey algorithm: $([string]$Evidence.TombstoneProof.KeyTokenAlgorithmVersion)"
        "- Tombstone count: $([string]$Evidence.TombstoneProof.Count)"
        "- Tombstone SHA-256: $([string]$Evidence.TombstoneProof.Sha256)"
        "- Gate count: $([string]$Evidence.GateCount)"
        "- Database deletion: $([string]$Evidence.DatabaseDeletion)"
        ''
        'This evidence contains identities and gate summaries only. It is not a database backup.'
    ) -join "`n"

    $encoding = [Text.UTF8Encoding]::new($false)
    $jsonStream = $null
    $markdownStream = $null
    try {
        $jsonStream = [IO.FileStream]::new(
            $jsonPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        try {
            $writer = [IO.StreamWriter]::new($jsonStream, $encoding)
            try { $writer.Write($json) } finally { $writer.Dispose() }
        } finally {
            if ($null -ne $jsonStream) { $jsonStream.Dispose() }
        }

        $markdownStream = [IO.FileStream]::new(
            $markdownPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        try {
            $writer = [IO.StreamWriter]::new($markdownStream, $encoding)
            try { $writer.Write($markdown) } finally { $writer.Dispose() }
        } finally {
            if ($null -ne $markdownStream) { $markdownStream.Dispose() }
        }
    } catch [IO.IOException] {
        throw "CUTOVER_EVIDENCE_EXISTS: evidence for CutoverRunId $runId is immutable."
    }

    return [pscustomobject]@{
        JsonPath = $jsonPath
        MarkdownPath = $markdownPath
    }
}

function Get-CutoverTransportDemandKeyToken {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $WorkType,
        [Parameter(Mandatory = $true)] [string] $Sublot
    )

    if ([string]::IsNullOrWhiteSpace($WorkType) -or [string]::IsNullOrWhiteSpace($Sublot)) {
        throw 'CUTOVER_TOMBSTONE_INVALID: WorkType and Sublot must be non-blank.'
    }
    # Frozen offline codec for TransportDemandKeyIdentity.CreateToken. The version is
    # carried in every proof, and the C# test suite has a cross-language fixed vector;
    # changing the authoritative codec therefore fails cutover tests until this version
    # and its evidence contract are deliberately advanced together.
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($value in @($Sublot, $WorkType)) {
            $bytes = [Text.Encoding]::UTF8.GetBytes($value)
            $length = [byte[]]::new(4)
            $length[0] = [byte](($bytes.Length -shr 24) -band 0xff)
            $length[1] = [byte](($bytes.Length -shr 16) -band 0xff)
            $length[2] = [byte](($bytes.Length -shr 8) -band 0xff)
            $length[3] = [byte]($bytes.Length -band 0xff)
            $hash.AppendData($length)
            $hash.AppendData($bytes)
        }
        return ([BitConverter]::ToString($hash.GetHashAndReset())).Replace('-', '').ToLowerInvariant()
    } finally {
        $hash.Dispose()
    }
}

function Get-CutoverTransportDemandKeyAlgorithmVersion {
    [CmdletBinding()]
    param()

    return 'TRANSPORT_DEMAND_KEY_SHA256_LENGTH_PREFIXED_V1'
}

function Get-CutoverDatabaseIdentityEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = @'
SELECT
    CONVERT(NVARCHAR(256), SERVERPROPERTY('MachineName')),
    COALESCE(CONVERT(NVARCHAR(256), SERVERPROPERTY('InstanceName')), N'MSSQLSERVER'),
    DB_NAME(), DB_ID(),
    CASE WHEN DB_ID() <= 4 THEN CONVERT(BIT, 1) ELSE CONVERT(BIT, 0) END,
    schemaInfo.SchemaVersion,
    schemaInfo.ContractVersion,
    schemaInfo.HistoryEpoch,
    CONVERT(BIT, CASE WHEN
        IS_SRVROLEMEMBER(N'sysadmin') = 1
        OR HAS_PERMS_BY_NAME(NULL, N'SERVER', N'ALTER ANY DATABASE') = 1
        OR HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL') = 1
        THEN 1 ELSE 0 END),
    CONVERT(BIT, CASE WHEN
        IS_SRVROLEMEMBER(N'sysadmin') = 1
        OR HAS_PERMS_BY_NAME(NULL, N'SERVER', N'VIEW SERVER STATE') = 1
        OR HAS_PERMS_BY_NAME(NULL, N'SERVER', N'VIEW SERVER PERFORMANCE STATE') = 1
        THEN 1 ELSE 0 END),
    ORIGINAL_LOGIN()
FROM mesingest.SchemaInfo AS schemaInfo
WHERE schemaInfo.Id = 1;

SELECT physical_name
FROM sys.database_files
WHERE type = 0
ORDER BY file_id;

SELECT COUNT(*)
FROM sys.dm_exec_sessions AS sessionRow
WHERE sessionRow.database_id = DB_ID()
  AND sessionRow.session_id <> @@SPID
  AND sessionRow.is_user_process = 1;
'@
        $reader = $command.ExecuteReader()
        try {
            if (-not $reader.Read()) {
                throw 'CUTOVER_DATABASE_IDENTITY_MISSING: mesingest.SchemaInfo is absent.'
            }
            $serverIdentity = "$($reader.GetString(0))\$($reader.GetString(1))"
            $databaseName = $reader.GetString(2)
            $databaseId = [Convert]::ToInt32($reader.GetValue(3), [Globalization.CultureInfo]::InvariantCulture)
            $isSystemDatabase = $reader.GetBoolean(4)
            $schemaVersion = [Convert]::ToInt32($reader.GetValue(5), [Globalization.CultureInfo]::InvariantCulture)
            $contractVersion = $reader.GetString(6)
            $historyEpoch = $reader.GetGuid(7).ToString('D')
            $hasDeletePermission = $reader.GetBoolean(8)
            $hasGlobalSessionVisibility = $reader.GetBoolean(9)
            $executionLogin = $reader.GetString(10)

            if (-not $reader.NextResult()) {
                throw 'CUTOVER_DATABASE_FILES_MISSING: the database file result is absent.'
            }
            $directories = [Collections.Generic.List[string]]::new()
            while ($reader.Read()) {
                $directory = [IO.Path]::GetDirectoryName($reader.GetString(0))
                if (-not [string]::IsNullOrWhiteSpace($directory)) {
                    $directories.Add($directory)
                }
            }

            if (-not $reader.NextResult() -or -not $reader.Read()) {
                throw 'CUTOVER_DATABASE_SESSIONS_MISSING: the connection result is absent.'
            }
            $foreignSessionCount = [Convert]::ToInt32($reader.GetValue(0), [Globalization.CultureInfo]::InvariantCulture)
        } finally {
            $reader.Dispose()
        }
    } finally {
        $command.Dispose()
    }

    return [pscustomobject]@{
        ServerIdentity = $serverIdentity
        DatabaseName = $databaseName
        DatabaseId = $databaseId
        IsSystemDatabase = $isSystemDatabase
        SchemaVersion = $schemaVersion
        ContractVersion = $contractVersion
        HistoryEpoch = $historyEpoch
        DataDirectories = @($directories | Sort-Object -Unique)
        ForeignSessionCount = $foreignSessionCount
        HasDeletePermission = $hasDeletePermission
        HasGlobalSessionVisibility = $hasGlobalSessionVisibility
        ExecutionLogin = $executionLogin
    }
}

function Get-CutoverThreeProjectionGate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $CutoverRunId,
        [Parameter(Mandatory = $true)] [string] $ExpectedHistoryEpoch,
        [Parameter(Mandatory = $true)] [long] $AfterPollTraceSequence
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = @'
SELECT TOP (3)
    pollTrace.PollTraceSequence,
    pollTrace.Outcome,
    projection.ProjectionCommitId,
    projection.ProjectionSequence,
    projection.HistoryEpoch
FROM mesingest.PollTraces AS pollTrace
LEFT JOIN mesingest.ProjectionCommits AS projection
    ON projection.PollTraceId = pollTrace.PollTraceId
WHERE pollTrace.PollTraceSequence > @afterPollTraceSequence
ORDER BY pollTrace.PollTraceSequence DESC;
'@
        $afterParameter = $command.Parameters.Add(
            '@afterPollTraceSequence',
            [Data.SqlDbType]::BigInt)
        $afterParameter.Value = $AfterPollTraceSequence
        $reader = $command.ExecuteReader()
        try {
            $rounds = [Collections.Generic.List[object]]::new()
            while ($reader.Read()) {
                $rounds.Add([pscustomobject]@{
                    PollTraceSequence = $reader.GetInt64(0)
                    Outcome = $reader.GetString(1)
                    ProjectionCommitId = if ($reader.IsDBNull(2)) { $null } else { $reader.GetString(2) }
                    ProjectionSequence = if ($reader.IsDBNull(3)) { $null } else { $reader.GetInt64(3) }
                    HistoryEpoch = if ($reader.IsDBNull(4)) { $null } else { $reader.GetGuid(4).ToString('D') }
                })
            }
        } finally {
            $reader.Dispose()
        }
    } finally {
        $command.Dispose()
    }

    if ($rounds.Count -ne 3 -or @($rounds | Where-Object {
            $_.Outcome -cne 'SUCCESS' -or
            [string]::IsNullOrWhiteSpace([string]$_.ProjectionCommitId) -or
            $_.HistoryEpoch -cne $ExpectedHistoryEpoch
        }).Count -ne 0) {
        throw 'CUTOVER_THREE_PROJECTIONS_NOT_PROVEN: the latest three rounds are not successful commits in this HistoryEpoch.'
    }
    if ($rounds[0].PollTraceSequence -ne $rounds[1].PollTraceSequence + 1 -or
        $rounds[1].PollTraceSequence -ne $rounds[2].PollTraceSequence + 1) {
        throw 'CUTOVER_THREE_PROJECTIONS_NOT_CONSECUTIVE: the latest successful rounds are not consecutive.'
    }

    return [pscustomobject]@{
        Gate = [pscustomobject]@{
            Name = 'THREE_CONSECUTIVE_PROJECTIONS'
            CutoverRunId = $CutoverRunId
            Passed = $true
            Detail = "projectionSequences=$($rounds.ProjectionSequence -join ',')"
        }
        LatestProjectionCommitId = $rounds[0].ProjectionCommitId
        LatestProjectionSequence = $rounds[0].ProjectionSequence
        Rounds = @($rounds)
    }
}

function Get-CutoverPollTraceHighWater {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Connection)

    return [long](Invoke-CutoverScalar `
        -Connection $Connection `
        -Sql 'SELECT COALESCE(MAX(PollTraceSequence), 0) FROM mesingest.PollTraces;')
}

function Write-CutoverWindowsEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $CutoverRunId,
        [Parameter(Mandatory = $true)] [string] $Status,
        [Parameter(Mandatory = $true)] [string] $HistoryEpoch,
        [Parameter(Mandatory = $true)] [string] $TombstoneSha256,
        [Parameter(Mandatory = $true)] [string] $KeyTokenAlgorithmVersion
    )

    Add-Type -AssemblyName System.Diagnostics.EventLog | Out-Null
    $message = "MesIngest CutoverRunId=$CutoverRunId Status=$Status " +
        "HistoryEpoch=$HistoryEpoch TombstoneSha256=$TombstoneSha256 " +
        "KeyTokenAlgorithmVersion=$KeyTokenAlgorithmVersion " +
        'DatabaseDeletion=NOT_AUTHORIZED_TICKET_23'
    [Diagnostics.EventLog]::WriteEntry(
        'Application',
        $message,
        [Diagnostics.EventLogEntryType]::Information,
        2300)
}

function Get-CutoverTombstoneProof {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Tombstones,

        [switch] $IncludeNormalizedSet
    )

    $algorithmVersion = 'ARCHIVED_DEMAND_KEY_TOMBSTONE_SHA256_V1'
    $keyTokenAlgorithmVersion = Get-CutoverTransportDemandKeyAlgorithmVersion
    $byKey = [Collections.Generic.SortedDictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $keyBySeries = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)

    foreach ($row in $Tombstones) {
        if ($null -eq $row) {
            throw 'CUTOVER_TOMBSTONE_INVALID: a tombstone row is null.'
        }

        $keyToken = [string]$row.KeyToken
        $workType = [string]$row.WorkType
        $sublot = [string]$row.Sublot
        $originalSeriesId = [string]$row.OriginalSeriesId
        $archiveConclusion = [string]$row.ArchiveConclusion
        $tombstoneVersion = [int]$row.TombstoneVersion
        try {
            $archivedAt = ([DateTimeOffset]$row.ArchivedAt).ToUniversalTime()
        } catch {
            throw 'CUTOVER_TOMBSTONE_INVALID: ArchivedAt is not a DateTimeOffset.'
        }

        if ($keyToken -cnotmatch '^[0-9a-f]{64}$') {
            throw 'CUTOVER_TOMBSTONE_INVALID: KeyToken must be 64 lowercase hexadecimal characters.'
        }
        if ([string]::IsNullOrWhiteSpace($workType) -or $workType.Length -gt 128) {
            throw 'CUTOVER_TOMBSTONE_INVALID: WorkType must be non-blank and at most 128 characters.'
        }
        if ([string]::IsNullOrWhiteSpace($sublot) -or $sublot.Length -gt 256) {
            throw 'CUTOVER_TOMBSTONE_INVALID: Sublot must be non-blank and at most 256 characters.'
        }
        if ([string]::IsNullOrWhiteSpace($originalSeriesId) -or $originalSeriesId.Length -gt 64) {
            throw 'CUTOVER_TOMBSTONE_INVALID: OriginalSeriesId must be non-blank and at most 64 characters.'
        }
        if ($archiveConclusion -cne 'ARCHIVED' -or $tombstoneVersion -ne 1) {
            throw 'CUTOVER_TOMBSTONE_INVALID: only ARCHIVED tombstone version 1 is accepted.'
        }
        $expectedKeyToken = Get-CutoverTransportDemandKeyToken -WorkType $workType -Sublot $sublot
        if ($keyToken -cne $expectedKeyToken) {
            throw 'CUTOVER_TOMBSTONE_KEY_TOKEN_MISMATCH: KeyToken does not match WorkType and Sublot.'
        }

        $normalized = [pscustomobject]@{
            KeyToken = $keyToken
            WorkType = $workType
            Sublot = $sublot
            OriginalSeriesId = $originalSeriesId
            ArchivedAt = $archivedAt.ToString(
                'yyyy-MM-ddTHH:mm:ss.fffffffZ',
                [Globalization.CultureInfo]::InvariantCulture)
            ArchiveConclusion = $archiveConclusion
            TombstoneVersion = $tombstoneVersion
        }

        if ($byKey.ContainsKey($keyToken)) {
            $existing = $byKey[$keyToken]
            if (($existing | ConvertTo-Json -Compress) -cne ($normalized | ConvertTo-Json -Compress)) {
                throw "CUTOVER_TOMBSTONE_CONFLICT: KeyToken $keyToken has more than one identity."
            }
            continue
        }
        if ($keyBySeries.ContainsKey($originalSeriesId) -and
            $keyBySeries[$originalSeriesId] -cne $keyToken) {
            throw ("CUTOVER_TOMBSTONE_CONFLICT: OriginalSeriesId $originalSeriesId belongs " +
                'to more than one key.')
        }

        $byKey.Add($keyToken, $normalized)
        $keyBySeries[$originalSeriesId] = $keyToken
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.StreamWriter]::new($stream, $utf8, 4096, $true)
    try {
        $writer.NewLine = "`n"
        $writer.WriteLine($algorithmVersion)
        foreach ($normalized in $byKey.Values) {
            foreach ($value in @(
                $normalized.KeyToken,
                $normalized.WorkType,
                $normalized.Sublot,
                $normalized.OriginalSeriesId,
                $normalized.ArchivedAt,
                $normalized.ArchiveConclusion,
                [string]$normalized.TombstoneVersion)) {
                $bytes = $utf8.GetBytes([string]$value)
                $writer.Write($bytes.Length.ToString(
                    'x8',
                    [Globalization.CultureInfo]::InvariantCulture))
                $writer.Write(':')
                $writer.Write([string]$value)
            }
            $writer.WriteLine()
        }
        $writer.Flush()
        $stream.Position = 0
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $digest = $sha.ComputeHash($stream)
        } finally {
            $sha.Dispose()
        }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    $result = [ordered]@{
        AlgorithmVersion = $algorithmVersion
        KeyTokenAlgorithmVersion = $keyTokenAlgorithmVersion
        Count = $byKey.Count
        Sha256 = ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
    }
    if ($IncludeNormalizedSet) {
        $result.Tombstones = @($byKey.Values)
    }
    return [pscustomobject]$result
}

function Get-CutoverArchivedTombstones {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        $Transaction
    )

    $command = $Connection.CreateCommand()
    try {
        if ($null -ne $Transaction) {
            $command.Transaction = $Transaction
        }
        # This SELECT is intentionally the complete old-database export surface. Do not
        # add projection, raw observation, event, error, or business payload columns.
        $command.CommandText = @'
SELECT
    KeyToken,
    WorkType,
    Sublot,
    OriginalSeriesId,
    ArchivedAt,
    ArchiveConclusion,
    TombstoneVersion
FROM mesingest.ArchivedDemandKeyTombstones
ORDER BY KeyToken COLLATE Latin1_General_100_BIN2;
'@
        $reader = $command.ExecuteReader()
        try {
            $rows = [Collections.Generic.List[object]]::new()
            while ($reader.Read()) {
                $rows.Add([pscustomobject]@{
                    KeyToken = $reader.GetString(0)
                    WorkType = $reader.GetString(1)
                    Sublot = $reader.GetString(2)
                    OriginalSeriesId = $reader.GetString(3)
                    ArchivedAt = $reader.GetFieldValue[DateTimeOffset](4)
                    ArchiveConclusion = $reader.GetString(5)
                    TombstoneVersion = $reader.GetInt32(6)
                })
            }
            return $rows
        } finally {
            $reader.Dispose()
        }
    } finally {
        $command.Dispose()
    }
}

function Invoke-CutoverTombstoneSeed {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Tombstones,

        [Parameter(Mandatory = $true)] $ExpectedProof
    )

    $inputProof = Get-CutoverTombstoneProof `
        -Tombstones $Tombstones `
        -IncludeNormalizedSet
    if ([string]$ExpectedProof.AlgorithmVersion -cne $inputProof.AlgorithmVersion -or
        [string]$ExpectedProof.KeyTokenAlgorithmVersion -cne $inputProof.KeyTokenAlgorithmVersion -or
        [int]$ExpectedProof.Count -ne $inputProof.Count -or
        [string]$ExpectedProof.Sha256 -cne $inputProof.Sha256) {
        throw 'CUTOVER_TOMBSTONE_PROOF_MISMATCH: the seed input does not match its export proof.'
    }

    $json = if ($inputProof.Count -eq 0) {
        '[]'
    } else {
        @($inputProof.Tombstones) | ConvertTo-Json -Depth 4 -Compress
    }
    $transaction = $Connection.BeginTransaction([Data.IsolationLevel]::Serializable)
    try {
        $command = $Connection.CreateCommand()
        try {
            $command.Transaction = $transaction
            $command.CommandTimeout = 120
            $command.CommandText = @'
SET XACT_ABORT ON;

DECLARE @lockResult INT;
EXEC @lockResult = sys.sp_getapplock
    @Resource = N'mesingest.cutover.tombstone-seed.v1',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
IF @lockResult < 0
    THROW 51100, 'CUTOVER_TOMBSTONE_LOCK_FAILED', 1;

DECLARE @seed TABLE
(
    KeyToken CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OriginalSeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
    ArchivedAt DATETIMEOFFSET(7) NOT NULL,
    ArchiveConclusion NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TombstoneVersion INT NOT NULL
);

INSERT INTO @seed
    (KeyToken, WorkType, Sublot, OriginalSeriesId, ArchivedAt,
     ArchiveConclusion, TombstoneVersion)
SELECT
    KeyToken, WorkType, Sublot, OriginalSeriesId, ArchivedAt,
    ArchiveConclusion, TombstoneVersion
FROM OPENJSON(@tombstones)
WITH
(
    KeyToken CHAR(64) '$.KeyToken',
    WorkType NVARCHAR(128) '$.WorkType',
    Sublot NVARCHAR(256) '$.Sublot',
    OriginalSeriesId NVARCHAR(64) '$.OriginalSeriesId',
    ArchivedAt DATETIMEOFFSET(7) '$.ArchivedAt',
    ArchiveConclusion NVARCHAR(32) '$.ArchiveConclusion',
    TombstoneVersion INT '$.TombstoneVersion'
);

IF @@ROWCOUNT <> @expectedCount
    THROW 51101, 'CUTOVER_TOMBSTONE_INPUT_COUNT_MISMATCH', 1;

IF EXISTS
(
    SELECT 1
    FROM @seed AS source
    INNER JOIN mesingest.ArchivedDemandKeyTombstones AS target WITH (UPDLOCK, HOLDLOCK)
        ON target.KeyToken = source.KeyToken
        OR target.OriginalSeriesId = source.OriginalSeriesId
    WHERE target.KeyToken <> source.KeyToken
       OR target.WorkType <> source.WorkType
       OR target.Sublot <> source.Sublot
       OR target.OriginalSeriesId <> source.OriginalSeriesId
       OR target.ArchivedAt <> source.ArchivedAt
       OR target.ArchiveConclusion <> source.ArchiveConclusion
       OR target.TombstoneVersion <> source.TombstoneVersion
)
    THROW 51102, 'CUTOVER_TOMBSTONE_CONFLICT', 1;

DECLARE @existingCount INT =
(
    SELECT COUNT(*)
    FROM @seed AS source
    INNER JOIN mesingest.ArchivedDemandKeyTombstones AS target WITH (UPDLOCK, HOLDLOCK)
        ON target.KeyToken = source.KeyToken
       AND target.WorkType = source.WorkType
       AND target.Sublot = source.Sublot
       AND target.OriginalSeriesId = source.OriginalSeriesId
       AND target.ArchivedAt = source.ArchivedAt
       AND target.ArchiveConclusion = source.ArchiveConclusion
       AND target.TombstoneVersion = source.TombstoneVersion
);

INSERT INTO mesingest.ArchivedDemandKeyTombstones
    (KeyToken, WorkType, Sublot, OriginalSeriesId, ArchivedAt,
     ArchiveConclusion, TombstoneVersion)
SELECT
    source.KeyToken, source.WorkType, source.Sublot, source.OriginalSeriesId,
    source.ArchivedAt, source.ArchiveConclusion, source.TombstoneVersion
FROM @seed AS source
WHERE NOT EXISTS
(
    SELECT 1
    FROM mesingest.ArchivedDemandKeyTombstones AS target WITH (UPDLOCK, HOLDLOCK)
    WHERE target.KeyToken = source.KeyToken
);

SELECT @@ROWCOUNT, @existingCount;
'@
            $jsonParameter = $command.Parameters.Add('@tombstones', [Data.SqlDbType]::NVarChar, -1)
            $jsonParameter.Value = $json
            $countParameter = $command.Parameters.Add('@expectedCount', [Data.SqlDbType]::Int)
            $countParameter.Value = $inputProof.Count
            $reader = $command.ExecuteReader()
            try {
                if (-not $reader.Read()) {
                    throw 'CUTOVER_TOMBSTONE_SEED_NO_RESULT: the seed did not return counts.'
                }
                $insertedCount = $reader.GetInt32(0)
                $existingCount = $reader.GetInt32(1)
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        $targetRows = @(Get-CutoverArchivedTombstones `
            -Connection $Connection `
            -Transaction $transaction)
        $targetProof = Get-CutoverTombstoneProof -Tombstones $targetRows
        if ($targetProof.AlgorithmVersion -cne $inputProof.AlgorithmVersion -or
            $targetProof.KeyTokenAlgorithmVersion -cne $inputProof.KeyTokenAlgorithmVersion -or
            $targetProof.Count -ne $inputProof.Count -or
            $targetProof.Sha256 -cne $inputProof.Sha256) {
            throw 'CUTOVER_TOMBSTONE_PROOF_MISMATCH: the target set is not exactly the exported set.'
        }

        $transaction.Commit()
        return [pscustomobject]@{
            InsertedCount = $insertedCount
            ExistingCount = $existingCount
            Proof = $targetProof
        }
    } catch {
        try {
            $transaction.Rollback()
        } catch {
            # Preserve the primary exception; the caller is already in a failed cutover run.
        }
        throw
    } finally {
        $transaction.Dispose()
        $json = $null
    }
}

function Open-CutoverConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $ConnectionString,
        [Parameter(Mandatory = $true)] [string] $ForbiddenDatabase
    )

    Add-Type -AssemblyName System.Data | Out-Null
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    # SqlConnectionStringBuilder is an IDictionary, so PowerShell binds member access to the
    # keyword indexer rather than the CLR property. The keyword is 'Initial Catalog' with a
    # space; 'InitialCatalog' is only a property name and is rejected as an unknown keyword.
    $initialCatalog = [string]$builder['Initial Catalog']

    # Dropping or restoring a database from a connection that is inside it fails or, worse,
    # succeeds against a database the operator did not mean to name. Force master.
    if ($initialCatalog -and
        $initialCatalog -ne 'master' -and
        [string]::Equals($initialCatalog, $ForbiddenDatabase, [StringComparison]::OrdinalIgnoreCase)) {
        throw ("CUTOVER_CONNECTION_TARGETS_THE_DATABASE_ITSELF: connect through master, " +
            "not Initial Catalog=$initialCatalog.")
    }

    $builder['Initial Catalog'] = 'master'
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    $connection.Open()
    return $connection
}

function Invoke-CutoverNonQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $Sql,
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 600
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        $command.CommandTimeout = $TimeoutSeconds
        foreach ($key in $Parameters.Keys) {
            [void]$command.Parameters.AddWithValue($key, $Parameters[$key])
        }
        [void]$command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}

function Invoke-CutoverScalar {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $Sql,
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 120
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        $command.CommandTimeout = $TimeoutSeconds
        foreach ($key in $Parameters.Keys) {
            [void]$command.Parameters.AddWithValue($key, $Parameters[$key])
        }
        return $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Get-CutoverTargetIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName
    )

    $machine = [string](Invoke-CutoverScalar -Connection $Connection `
        -Sql "SELECT CONVERT(NVARCHAR(256), SERVERPROPERTY('MachineName'));")
    $instance = Invoke-CutoverScalar -Connection $Connection `
        -Sql "SELECT CONVERT(NVARCHAR(256), SERVERPROPERTY('InstanceName'));"
    $instanceName = if ($null -eq $instance -or $instance -is [DBNull]) { 'MSSQLSERVER' } else { [string]$instance }

    $createDate = Invoke-CutoverScalar -Connection $Connection `
        -Sql "SELECT create_date FROM sys.databases WHERE name = @name;" `
        -Parameters @{ '@name' = $DatabaseName }
    $exists = -not ($null -eq $createDate -or $createDate -is [DBNull])

    $userTables = 0
    if ($exists) {
        $userTables = [int](Invoke-CutoverScalar -Connection $Connection `
            -Sql "SELECT COUNT(*) FROM [$DatabaseName].sys.tables WHERE is_ms_shipped = 0;")
    }

    return [pscustomobject]@{
        ServerIdentity = "$machine\$instanceName"
        DatabaseName   = $DatabaseName
        Exists         = $exists
        CreateDate     = if ($exists) { ([DateTime]$createDate).ToString('o') } else { $null }
        UserTableCount = $userTables
    }
}

function Assert-CutoverOperatorConfirmation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)] [string] $Action
    )

    $expected = "$($Identity.ServerIdentity)/$($Identity.DatabaseName)"
    Write-Host ""
    Write-Host "This drill will $Action the database named above."
    Write-Host "Type the exact target identity to continue: $expected"
    $typed = Read-Host -Prompt 'Target identity'
    if (-not [string]::Equals(($typed | Out-String).Trim(), $expected, [StringComparison]::Ordinal)) {
        throw ("CUTOVER_TARGET_NOT_CONFIRMED: the typed identity does not match the resolved " +
            "target. Nothing was changed. This confirmation cannot be bypassed, so a " +
            "non-interactive run always stops here.")
    }
}

function Get-CutoverForeignSessionCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName
    )

    $sql = "SELECT COUNT(*) FROM sys.dm_exec_sessions AS s " +
        "WHERE s.database_id = DB_ID(@name) AND s.session_id <> @@SPID AND s.is_user_process = 1;"
    return [int](Invoke-CutoverScalar -Connection $Connection -Sql $sql -Parameters @{ '@name' = $DatabaseName })
}

function Invoke-CutoverFullBackup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName,
        [Parameter(Mandatory = $true)] [string] $BackupPath
    )

    $sql = "BACKUP DATABASE [$DatabaseName] TO DISK = @path WITH INIT, FORMAT, CHECKSUM;"
    Invoke-CutoverNonQuery -Connection $Connection -Sql $sql -Parameters @{ '@path' = $BackupPath }
}

function Invoke-CutoverBackupVerify {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $BackupPath
    )

    Invoke-CutoverNonQuery -Connection $Connection `
        -Sql "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;" `
        -Parameters @{ '@path' = $BackupPath }
}

function Get-CutoverBackupSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $BackupPath
    )

    if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
        # The backup lives on the SQL Server host and may not be reachable from here.
        return 'NOT_REACHABLE_FROM_THIS_HOST'
    }

    return (Get-FileHash -LiteralPath $BackupPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-CutoverDropDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName
    )

    $sql = "ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
        "DROP DATABASE [$DatabaseName];"
    Invoke-CutoverNonQuery -Connection $Connection -Sql $sql
}

function Invoke-CutoverCreateEmptyDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName
    )

    Invoke-CutoverNonQuery -Connection $Connection -Sql "CREATE DATABASE [$DatabaseName];"
}

function Invoke-CutoverRestoreDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName,
        [Parameter(Mandatory = $true)] [string] $BackupPath
    )

    $sql = "IF DB_ID(@nameCheck) IS NOT NULL ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
        "RESTORE DATABASE [$DatabaseName] FROM DISK = @path WITH REPLACE, CHECKSUM; " +
        "ALTER DATABASE [$DatabaseName] SET MULTI_USER;"
    Invoke-CutoverNonQuery -Connection $Connection -Sql $sql `
        -Parameters @{ '@path' = $BackupPath; '@nameCheck' = $DatabaseName }
}
