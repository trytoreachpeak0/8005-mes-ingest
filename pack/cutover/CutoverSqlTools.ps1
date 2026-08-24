#Requires -Version 5.1
<#
.SYNOPSIS
  Shared SQL helpers for the attended cutover and rollback drills.

.DESCRIPTION
  Every function here is dot-sourced by the two drill entry points. None of them is
  reachable from the Host, the Watch client, or install/uninstall. Legacy cutover and
  rollback drills require Assert-CutoverOperatorConfirmation. The Ticket 25 deletion
  helper instead requires the full same-run gate, identity, tombstone, stopped-service,
  and immutable-evidence workflow owned by Invoke-MesIngestCutoverRun.ps1.
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

function Assert-CutoverDeleteAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$')]
        [string] $CutoverRunId,
        [Parameter(Mandatory = $true)] [object[]] $GateResults,
        [Parameter(Mandatory = $true)] $ProvenOldIdentity,
        [Parameter(Mandatory = $true)] $CurrentOldIdentity,
        [Parameter(Mandatory = $true)] $ProvenNewIdentity,
        [Parameter(Mandatory = $true)] $CurrentNewIdentity,
        [Parameter(Mandatory = $true)] $ProvenTombstoneProof,
        [Parameter(Mandatory = $true)] $CurrentOldTombstoneProof,
        [Parameter(Mandatory = $true)] $CurrentNewTombstoneProof,
        [Parameter(Mandatory = $true)] [string] $ExpectedOldDatabaseName,
        [Parameter(Mandatory = $true)] [string] $ExpectedNewDatabaseName,
        [Parameter(Mandatory = $true)] [string] $ExpectedSqlDataDirectory
    )

    try {
        $gateSet = Assert-CutoverGateSet -CutoverRunId $CutoverRunId -GateResults $GateResults
    } catch {
        throw "CUTOVER_DELETE_GATE_SET_INVALID: $($_.Exception.Message)"
    }

    foreach ($name in @($ExpectedOldDatabaseName, $ExpectedNewDatabaseName)) {
        if ($name -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$' -or
            $name.IndexOfAny([char[]]'*?[]%') -ge 0) {
            throw 'CUTOVER_DELETE_DATABASE_NAME_NOT_EXACT: database names cannot contain patterns or wildcards.'
        }
    }
    if ([string]::Equals($ExpectedOldDatabaseName, $ExpectedNewDatabaseName, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CUTOVER_DELETE_TARGET_IS_NEW_DATABASE: the old and new database names must be distinct.'
    }

    $expectedDirectory = [IO.Path]::GetFullPath($ExpectedSqlDataDirectory).TrimEnd('\', '/')
    foreach ($pair in @(
            @($ProvenOldIdentity, $CurrentOldIdentity, $ExpectedOldDatabaseName, 'OLD'),
            @($ProvenNewIdentity, $CurrentNewIdentity, $ExpectedNewDatabaseName, 'NEW'))) {
        $proven = $pair[0]
        $current = $pair[1]
        $expectedName = [string]$pair[2]
        $label = [string]$pair[3]
        $currentDirectories = @($current.DataDirectories | ForEach-Object {
            [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
        } | Sort-Object -Unique)
        if ([string]$current.DatabaseName -cne $expectedName -or
            [string]$proven.DatabaseName -cne $expectedName) {
            throw "CUTOVER_DELETE_${label}_DATABASE_NAME_CHANGED: the resolved database is not the explicit target."
        }
        if ([bool]$current.IsSystemDatabase -or [int]$current.DatabaseId -le 4) {
            throw "CUTOVER_DELETE_${label}_DATABASE_IS_SYSTEM: a system database can never be a cutover target."
        }
        if ([string]$current.ServerIdentity -cne [string]$proven.ServerIdentity -or
            [int]$current.DatabaseId -ne [int]$proven.DatabaseId -or
            [string]$current.CreateDateUtc -cne [string]$proven.CreateDateUtc) {
            throw "CUTOVER_DELETE_${label}_DATABASE_WAS_REPLACED: the database identity changed after the gates passed."
        }
        if ([int]$current.SchemaVersion -ne [int]$proven.SchemaVersion -or
            [string]$current.ContractVersion -cne [string]$proven.ContractVersion -or
            [string]$current.HistoryEpoch -cne [string]$proven.HistoryEpoch) {
            throw "CUTOVER_DELETE_${label}_CONTRACT_SCHEMA_CHANGED: the database contract identity changed."
        }
        if ($currentDirectories.Count -ne 1 -or
            -not [string]::Equals($currentDirectories[0], $expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "CUTOVER_DELETE_${label}_DATA_DIRECTORY_CHANGED: the database files are outside the exact SQL data directory."
        }
        if ([bool]$current.HasDeletePermission) {
            throw "CUTOVER_DELETE_BASE_IDENTITY_ALREADY_PRIVILEGED: deletion permission must not exist before one-time elevation."
        }
    }
    if ([string]$CurrentOldIdentity.ServerIdentity -cne [string]$CurrentNewIdentity.ServerIdentity -or
        [int]$CurrentOldIdentity.DatabaseId -eq [int]$CurrentNewIdentity.DatabaseId -or
        [string]$CurrentOldIdentity.ExecutionLogin -cne [string]$CurrentNewIdentity.ExecutionLogin) {
        throw 'CUTOVER_DELETE_DATABASE_IDENTITIES_NOT_DISTINCT: old and new must be distinct databases reached by one base identity.'
    }
    if (-not [bool]$CurrentOldIdentity.HasGlobalSessionVisibility) {
        throw 'CUTOVER_DELETE_SESSION_VISIBILITY_REQUIRED: the base identity cannot prove every SQL Server session.'
    }
    if ([int]$CurrentOldIdentity.ForeignSessionCount -ne 0) {
        throw 'CUTOVER_DELETE_OLD_DATABASE_STILL_IN_USE: a foreign business connection exists.'
    }

    foreach ($proof in @($CurrentOldTombstoneProof, $CurrentNewTombstoneProof)) {
        if ([string]$proof.AlgorithmVersion -cne [string]$ProvenTombstoneProof.AlgorithmVersion -or
            [string]$proof.KeyTokenAlgorithmVersion -cne [string]$ProvenTombstoneProof.KeyTokenAlgorithmVersion -or
            [int]$proof.Count -ne [int]$ProvenTombstoneProof.Count -or
            [string]$proof.Sha256 -cne [string]$ProvenTombstoneProof.Sha256) {
            throw 'CUTOVER_DELETE_TOMBSTONE_PROOF_CHANGED: old, new, and proven tombstone sets differ.'
        }
    }

    return [pscustomobject][ordered]@{
        AuthorizationType = 'CUTOVER_DELETE_AUTHORIZATION_V1'
        CutoverRunId = $CutoverRunId
        GateCount = $gateSet.Count
        ServerIdentity = [string]$CurrentOldIdentity.ServerIdentity
        OldDatabaseName = $ExpectedOldDatabaseName
        OldDatabaseId = [int]$CurrentOldIdentity.DatabaseId
        OldDatabaseCreateDateUtc = [string]$CurrentOldIdentity.CreateDateUtc
        OldSchemaVersion = [int]$CurrentOldIdentity.SchemaVersion
        OldContractVersion = [string]$CurrentOldIdentity.ContractVersion
        OldHistoryEpoch = [string]$CurrentOldIdentity.HistoryEpoch
        NewDatabaseName = $ExpectedNewDatabaseName
        NewDatabaseId = [int]$CurrentNewIdentity.DatabaseId
        NewDatabaseCreateDateUtc = [string]$CurrentNewIdentity.CreateDateUtc
        NewSchemaVersion = [int]$CurrentNewIdentity.SchemaVersion
        NewContractVersion = [string]$CurrentNewIdentity.ContractVersion
        NewHistoryEpoch = [string]$CurrentNewIdentity.HistoryEpoch
        SqlDataDirectory = $expectedDirectory
        BaseExecutionLogin = [string]$CurrentOldIdentity.ExecutionLogin
        TombstoneAlgorithmVersion = [string]$ProvenTombstoneProof.AlgorithmVersion
        KeyTokenAlgorithmVersion = [string]$ProvenTombstoneProof.KeyTokenAlgorithmVersion
        TombstoneCount = [int]$ProvenTombstoneProof.Count
        TombstoneSha256 = [string]$ProvenTombstoneProof.Sha256
    }
}

function Invoke-CutoverProvenOldDatabaseDeletion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$')]
        [string] $CutoverRunId,
        [Parameter(Mandatory = $true)] [string] $PrivilegeConnectionString,
        [Parameter(Mandatory = $true)] [string] $EvidenceDirectory,
        [Parameter(Mandatory = $true)] [string] $OldHostServiceName,
        [Parameter(Mandatory = $true)] [object[]] $GateResults,
        [Parameter(Mandatory = $true)] $ProvenOldIdentity,
        [Parameter(Mandatory = $true)] $CurrentOldIdentity,
        [Parameter(Mandatory = $true)] $ProvenNewIdentity,
        [Parameter(Mandatory = $true)] $CurrentNewIdentity,
        [Parameter(Mandatory = $true)] $ProvenTombstoneProof,
        [Parameter(Mandatory = $true)] $CurrentOldTombstoneProof,
        [Parameter(Mandatory = $true)] $CurrentNewTombstoneProof,
        [Parameter(Mandatory = $true)] [string] $ExpectedOldDatabaseName,
        [Parameter(Mandatory = $true)] [string] $ExpectedNewDatabaseName,
        [Parameter(Mandatory = $true)] [string] $ExpectedSqlDataDirectory
    )

    $authorization = Assert-CutoverDeleteAuthorization `
        -CutoverRunId $CutoverRunId `
        -GateResults $GateResults `
        -ProvenOldIdentity $ProvenOldIdentity `
        -CurrentOldIdentity $CurrentOldIdentity `
        -ProvenNewIdentity $ProvenNewIdentity `
        -CurrentNewIdentity $CurrentNewIdentity `
        -ProvenTombstoneProof $ProvenTombstoneProof `
        -CurrentOldTombstoneProof $CurrentOldTombstoneProof `
        -CurrentNewTombstoneProof $CurrentNewTombstoneProof `
        -ExpectedOldDatabaseName $ExpectedOldDatabaseName `
        -ExpectedNewDatabaseName $ExpectedNewDatabaseName `
        -ExpectedSqlDataDirectory $ExpectedSqlDataDirectory
    $oldHost = Get-Service -Name $OldHostServiceName -ErrorAction Stop
    if ($oldHost.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "CUTOVER_DELETE_OLD_HOST_RUNNING: service $OldHostServiceName is $($oldHost.Status)."
    }
    $resolvedEvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
    $preDeleteJsonPath = Join-Path $resolvedEvidenceDirectory "$CutoverRunId.pre-delete.json"
    $preDeleteMarkdownPath = Join-Path $resolvedEvidenceDirectory "$CutoverRunId.pre-delete.md"
    if (-not (Test-Path -LiteralPath $preDeleteJsonPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $preDeleteMarkdownPath -PathType Leaf)) {
        throw 'CUTOVER_DELETE_PRE_DELETE_EVIDENCE_MISSING: immutable JSON and Markdown evidence must exist before elevation.'
    }
    $preDeleteEvidence = Get-Content -LiteralPath $preDeleteJsonPath -Raw | ConvertFrom-Json
    $preDeleteMarkdown = Get-Content -LiteralPath $preDeleteMarkdownPath -Raw
    if ([string]$preDeleteEvidence.CutoverRunId -cne $CutoverRunId -or
        [string]$preDeleteEvidence.Status -cne 'DELETE_AUTHORIZED_BEFORE_ELEVATION' -or
        [string]$preDeleteEvidence.DatabaseDeletion -cne 'PENDING_EXACT_PROVEN_OLD_DATABASE' -or
        [string]$preDeleteEvidence.OldDatabase.DatabaseName -cne [string]$authorization.OldDatabaseName -or
        [int]$preDeleteEvidence.OldDatabase.DatabaseId -ne [int]$authorization.OldDatabaseId -or
        $preDeleteMarkdown -notmatch [regex]::Escape($CutoverRunId)) {
        throw 'CUTOVER_DELETE_PRE_DELETE_EVIDENCE_MISMATCH: external evidence does not authorize this exact run and database.'
    }

    Add-Type -AssemblyName System.Data | Out-Null
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($PrivilegeConnectionString)
    if (-not [string]::Equals([string]$builder['Initial Catalog'], 'master', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CUTOVER_DELETE_PRIVILEGE_CONNECTION_NOT_MASTER: the one-time broker must explicitly connect to master.'
    }
    $builder['Pooling'] = $false

    $principalName = 'MesIngestCutover_' + $CutoverRunId.Replace('-', '').ToLowerInvariant()
    $auditEvents = [Collections.Generic.List[object]]::new()
    $audit = [ordered]@{
        CutoverRunId = $CutoverRunId
        PrincipalName = $principalName
        DeletePermission = "CONTROL DATABASE::$([string]$Authorization.OldDatabaseName)"
        SessionVisibilityPermission = 'VIEW SERVER STATE; VIEW SERVER PERFORMANCE STATE'
        GrantedAtUtc = $null
        RevokedAtUtc = $null
        FinalStatus = 'NOT_GRANTED'
        VerifiedAbsent = $false
        Events = $auditEvents
    }
    $connection = $null
    $principalCreated = $false
    $principalCreationAttempted = $false
    $impersonated = $false
    $databaseDeleted = $false
    $primaryError = $null

    try {
        $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        $connection.Open()
        $isBroker = [int](Invoke-CutoverScalar -Connection $connection -Sql @'
SELECT CASE WHEN IS_SRVROLEMEMBER(N'sysadmin') = 1 THEN 1 ELSE 0 END;
'@)
        if ($isBroker -ne 1) {
            throw 'CUTOVER_DELETE_PRIVILEGE_BROKER_REQUIRED: the supplied one-time broker is not sysadmin.'
        }
        $machine = [string](Invoke-CutoverScalar -Connection $connection `
            -Sql "SELECT CONVERT(NVARCHAR(256), SERVERPROPERTY('MachineName'));")
        $instance = Invoke-CutoverScalar -Connection $connection `
            -Sql "SELECT CONVERT(NVARCHAR(256), SERVERPROPERTY('InstanceName'));"
        $instanceName = if ($null -eq $instance -or $instance -is [DBNull]) { 'MSSQLSERVER' } else { [string]$instance }
        if ("$machine\$instanceName" -cne [string]$Authorization.ServerIdentity) {
            throw 'CUTOVER_DELETE_PRIVILEGE_SERVER_MISMATCH: the broker points at another SQL Server instance.'
        }
        $existingPrincipal = [int](Invoke-CutoverScalar -Connection $connection `
            -Sql 'SELECT COUNT(*) FROM sys.server_principals WHERE name = @name;' `
            -Parameters @{ '@name' = $principalName })
        if ($existingPrincipal -ne 0) {
            throw 'CUTOVER_DELETE_RUN_PRINCIPAL_EXISTS: this CutoverRunId already has a SQL principal.'
        }

        foreach ($target in @(
                @([string]$Authorization.OldDatabaseName, [int]$Authorization.OldDatabaseId,
                    [string]$Authorization.OldDatabaseCreateDateUtc, [int]$Authorization.OldSchemaVersion,
                    [string]$Authorization.OldContractVersion, [string]$Authorization.OldHistoryEpoch, 'OLD'),
                @([string]$Authorization.NewDatabaseName, [int]$Authorization.NewDatabaseId,
                    [string]$Authorization.NewDatabaseCreateDateUtc, [int]$Authorization.NewSchemaVersion,
                    [string]$Authorization.NewContractVersion, [string]$Authorization.NewHistoryEpoch, 'NEW'))) {
            $connection.ChangeDatabase([string]$target[0])
            $identity = Get-CutoverDatabaseIdentityEvidence -Connection $connection
            $resolvedDirectories = @($identity.DataDirectories | ForEach-Object {
                [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
            } | Sort-Object -Unique)
            if ([string]$identity.ServerIdentity -cne [string]$Authorization.ServerIdentity -or
                [string]$identity.DatabaseName -cne [string]$target[0] -or
                [int]$identity.DatabaseId -ne [int]$target[1] -or
                [string]$identity.CreateDateUtc -cne [string]$target[2] -or
                [int]$identity.SchemaVersion -ne [int]$target[3] -or
                [string]$identity.ContractVersion -cne [string]$target[4] -or
                [string]$identity.HistoryEpoch -cne [string]$target[5] -or
                $resolvedDirectories.Count -ne 1 -or
                -not [string]::Equals($resolvedDirectories[0], [string]$Authorization.SqlDataDirectory,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "CUTOVER_DELETE_$($target[6])_DATABASE_WAS_REPLACED: the broker re-resolved another database identity."
            }
            $resolvedProof = Get-CutoverTombstoneProof -Tombstones @(
                Get-CutoverArchivedTombstones -Connection $connection)
            if ([string]$resolvedProof.AlgorithmVersion -cne [string]$Authorization.TombstoneAlgorithmVersion -or
                [string]$resolvedProof.KeyTokenAlgorithmVersion -cne [string]$Authorization.KeyTokenAlgorithmVersion -or
                [int]$resolvedProof.Count -ne [int]$Authorization.TombstoneCount -or
                [string]$resolvedProof.Sha256 -cne [string]$Authorization.TombstoneSha256) {
                throw "CUTOVER_DELETE_$($target[6])_TOMBSTONE_PROOF_CHANGED: the broker resolved a changed tombstone set."
            }
            if ($target[6] -eq 'OLD' -and [int]$identity.ForeignSessionCount -ne 0) {
                throw 'CUTOVER_DELETE_OLD_DATABASE_STILL_IN_USE: a connection appeared immediately before elevation.'
            }
        }
        $connection.ChangeDatabase('master')

        $passwordBytes = [byte[]]::new(48)
        [Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
        $ephemeralPassword = [Convert]::ToBase64String($passwordBytes)
        $principalCreationAttempted = $true
        Invoke-CutoverNonQuery -Connection $connection -Sql @'
DECLARE @quotedLogin nvarchar(258) = QUOTENAME(CONVERT(sysname, @login));
DECLARE @passwordLiteral nvarchar(514) = QUOTENAME(@password, NCHAR(39));
EXEC(N'CREATE LOGIN ' + @quotedLogin + N' WITH PASSWORD = ' + @passwordLiteral
    + N', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;');
EXEC(N'ALTER LOGIN ' + @quotedLogin + N' DISABLE;');
EXEC(N'DENY CONNECT SQL TO ' + @quotedLogin + N';');
'@ -Parameters @{
            '@login' = $principalName
            '@password' = $ephemeralPassword
        }
        $principalCreated = $true
        Invoke-CutoverNonQuery -Connection $connection -Sql @'
DECLARE @quotedLogin nvarchar(258) = QUOTENAME(CONVERT(sysname, @login));
DECLARE @quotedDatabase nvarchar(258) = QUOTENAME(CONVERT(sysname, @database));
EXEC(N'GRANT VIEW SERVER STATE TO ' + @quotedLogin + N';');
EXEC(N'GRANT VIEW SERVER PERFORMANCE STATE TO ' + @quotedLogin + N';');
EXEC(N'USE ' + @quotedDatabase + N'; CREATE USER ' + @quotedLogin
    + N' FOR LOGIN ' + @quotedLogin + N'; GRANT CONTROL TO ' + @quotedLogin + N';');
'@ -Parameters @{
            '@login' = $principalName
            '@database' = [string]$Authorization.OldDatabaseName
        }
        $ephemeralPassword = $null
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
        $audit.GrantedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $audit.FinalStatus = 'GRANTED_FOR_CURRENT_RUN'
        $auditEvents.Add([pscustomobject]@{
            AtUtc = $audit.GrantedAtUtc; Action = 'GRANTED';
            Detail = "principal=$principalName;database=$([string]$Authorization.OldDatabaseName)"
        })

        $oldHost = Get-Service -Name $OldHostServiceName -ErrorAction Stop
        if ($oldHost.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            throw "CUTOVER_DELETE_OLD_HOST_RESTARTED: service $OldHostServiceName is $($oldHost.Status)."
        }
        Assert-CutoverGateSet -CutoverRunId $CutoverRunId -GateResults $GateResults | Out-Null
        $connection.ChangeDatabase([string]$Authorization.OldDatabaseName)
        $brokerFinalIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $connection
        $brokerFinalDirectories = @($brokerFinalIdentity.DataDirectories | ForEach-Object {
            [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
        } | Sort-Object -Unique)
        $brokerFinalProof = Get-CutoverTombstoneProof -Tombstones @(
            Get-CutoverArchivedTombstones -Connection $connection)
        if ([int]$brokerFinalIdentity.DatabaseId -ne [int]$Authorization.OldDatabaseId -or
            [string]$brokerFinalIdentity.CreateDateUtc -cne [string]$Authorization.OldDatabaseCreateDateUtc -or
            [int]$brokerFinalIdentity.SchemaVersion -ne [int]$Authorization.OldSchemaVersion -or
            [string]$brokerFinalIdentity.ContractVersion -cne [string]$Authorization.OldContractVersion -or
            [string]$brokerFinalIdentity.HistoryEpoch -cne [string]$Authorization.OldHistoryEpoch -or
            $brokerFinalDirectories.Count -ne 1 -or
            -not [string]::Equals($brokerFinalDirectories[0], [string]$Authorization.SqlDataDirectory,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$brokerFinalProof.AlgorithmVersion -cne [string]$Authorization.TombstoneAlgorithmVersion -or
            [string]$brokerFinalProof.KeyTokenAlgorithmVersion -cne [string]$Authorization.KeyTokenAlgorithmVersion -or
            [int]$brokerFinalProof.Count -ne [int]$Authorization.TombstoneCount -or
            [string]$brokerFinalProof.Sha256 -cne [string]$Authorization.TombstoneSha256 -or
            -not [bool]$brokerFinalIdentity.HasGlobalSessionVisibility -or
            [int]$brokerFinalIdentity.ForeignSessionCount -ne 0) {
            throw 'CUTOVER_DELETE_FINAL_BROKER_REVALIDATION_FAILED: target identity or connection state changed after elevation.'
        }
        $connection.ChangeDatabase([string]$Authorization.NewDatabaseName)
        $brokerFinalNewIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $connection
        $brokerFinalNewDirectories = @($brokerFinalNewIdentity.DataDirectories | ForEach-Object {
            [IO.Path]::GetFullPath([string]$_).TrimEnd('\', '/')
        } | Sort-Object -Unique)
        $brokerFinalNewProof = Get-CutoverTombstoneProof -Tombstones @(
            Get-CutoverArchivedTombstones -Connection $connection)
        if ([int]$brokerFinalNewIdentity.DatabaseId -ne [int]$Authorization.NewDatabaseId -or
            [string]$brokerFinalNewIdentity.CreateDateUtc -cne [string]$Authorization.NewDatabaseCreateDateUtc -or
            [int]$brokerFinalNewIdentity.SchemaVersion -ne [int]$Authorization.NewSchemaVersion -or
            [string]$brokerFinalNewIdentity.ContractVersion -cne [string]$Authorization.NewContractVersion -or
            [string]$brokerFinalNewIdentity.HistoryEpoch -cne [string]$Authorization.NewHistoryEpoch -or
            $brokerFinalNewDirectories.Count -ne 1 -or
            -not [string]::Equals($brokerFinalNewDirectories[0], [string]$Authorization.SqlDataDirectory,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$brokerFinalNewProof.AlgorithmVersion -cne [string]$Authorization.TombstoneAlgorithmVersion -or
            [string]$brokerFinalNewProof.KeyTokenAlgorithmVersion -cne [string]$Authorization.KeyTokenAlgorithmVersion -or
            [int]$brokerFinalNewProof.Count -ne [int]$Authorization.TombstoneCount -or
            [string]$brokerFinalNewProof.Sha256 -cne [string]$Authorization.TombstoneSha256) {
            throw 'CUTOVER_DELETE_FINAL_NEW_DATABASE_REVALIDATION_FAILED: the new database identity or tombstone proof changed after elevation.'
        }
        $connection.ChangeDatabase('master')

        Invoke-CutoverNonQuery -Connection $connection `
            -Sql "EXECUTE AS LOGIN = '$principalName';"
        $impersonated = $true
        $connection.ChangeDatabase([string]$Authorization.OldDatabaseName)
        $elevatedIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $connection
        if ([string]$elevatedIdentity.DatabaseName -cne [string]$Authorization.OldDatabaseName -or
            [int]$elevatedIdentity.DatabaseId -ne [int]$Authorization.OldDatabaseId -or
            [string]$elevatedIdentity.CreateDateUtc -cne [string]$Authorization.OldDatabaseCreateDateUtc -or
            -not [bool]$elevatedIdentity.HasDeletePermission -or
            [int]$elevatedIdentity.ForeignSessionCount -ne 0) {
            throw ("CUTOVER_DELETE_ELEVATED_IDENTITY_MISMATCH: temporary permission did not resolve " +
                "to the exact proven old database; name=$($elevatedIdentity.DatabaseName);" +
                "databaseId=$($elevatedIdentity.DatabaseId);createDateUtc=$($elevatedIdentity.CreateDateUtc);" +
                "hasDeletePermission=$($elevatedIdentity.HasDeletePermission);" +
                "hasGlobalSessionVisibility=$($elevatedIdentity.HasGlobalSessionVisibility);" +
                "foreignSessionCount=$($elevatedIdentity.ForeignSessionCount).")
        }
        $connection.ChangeDatabase('master')
        $auditEvents.Add([pscustomobject]@{
            AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Action = 'REVERIFIED_BEFORE_DROP';
            Detail = "databaseId=$($elevatedIdentity.DatabaseId);createDateUtc=$($elevatedIdentity.CreateDateUtc)"
        })

        Invoke-CutoverDropDatabaseIfUnused `
            -Connection $connection `
            -DatabaseName ([string]$Authorization.OldDatabaseName)
        $databaseDeleted = $true
        $auditEvents.Add([pscustomobject]@{
            AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Action = 'EXACT_DATABASE_DELETED';
            Detail = "database=$([string]$Authorization.OldDatabaseName);databaseId=$([int]$Authorization.OldDatabaseId)"
        })
    } catch {
        $primaryError = $_
    } finally {
        $cleanupError = $null
        if ($null -ne $connection) {
            $connection.Dispose()
            $connection = $null
            if ($impersonated) {
                $impersonated = $false
                $auditEvents.Add([pscustomobject]@{
                    AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Action = 'IMPERSONATED_SESSION_CLOSED'; Detail = $principalName
                })
            }
        }
        foreach ($cleanupAttempt in 1..3) {
            $cleanupConnection = $null
            try {
                $cleanupConnection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
                $cleanupConnection.Open()
                if ($principalCreationAttempted) {
                    Invoke-CutoverNonQuery -Connection $cleanupConnection -Sql @'
DECLARE @quotedLogin nvarchar(258) = QUOTENAME(CONVERT(sysname, @login));
IF DB_ID(@database) IS NOT NULL
BEGIN
    DECLARE @quotedDatabase nvarchar(258) = QUOTENAME(CONVERT(sysname, @database));
    DECLARE @loginLiteral nvarchar(258) = QUOTENAME(CONVERT(sysname, @login), NCHAR(39));
    DECLARE @dropUserSql nvarchar(max) = N'USE ' + @quotedDatabase
        + N'; IF USER_ID(' + @loginLiteral + N') IS NOT NULL DROP USER ' + @quotedLogin + N';';
    EXEC sys.sp_executesql @dropUserSql;
END;
IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @login)
    EXEC(N'DROP LOGIN ' + @quotedLogin + N';');
'@ -Parameters @{
                        '@login' = $principalName
                        '@database' = [string]$Authorization.OldDatabaseName
                    }
                    $principalCreated = $false
                    $principalCreationAttempted = $false
                }
                $remaining = [int](Invoke-CutoverScalar -Connection $cleanupConnection `
                    -Sql 'SELECT COUNT(*) FROM sys.server_principals WHERE name = @name;' `
                    -Parameters @{ '@name' = $principalName })
                if ($remaining -ne 0) {
                    throw 'CUTOVER_DELETE_PERMISSION_REVOCATION_NOT_PROVEN: the temporary principal still exists.'
                }
                $cleanupError = $null
                $audit.RevokedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                $audit.FinalStatus = 'REVOKED_AND_PRINCIPAL_DROPPED'
                $audit.VerifiedAbsent = $true
                $auditEvents.Add([pscustomobject]@{
                    AtUtc = $audit.RevokedAtUtc; Action = 'VERIFIED_ABSENT';
                    Detail = "$principalName;cleanupAttempt=$cleanupAttempt"
                })
                break
            } catch {
                $cleanupError = $_
                $audit.FinalStatus = 'REVOCATION_RETRY_REQUIRED'
                $auditEvents.Add([pscustomobject]@{
                    AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Action = 'REVOCATION_ATTEMPT_FAILED';
                    Detail = "attempt=$cleanupAttempt;error=$($_.Exception.Message)"
                })
                if ($cleanupAttempt -eq 3) { $audit.FinalStatus = 'REVOCATION_FAILED' }
            } finally {
                if ($null -ne $cleanupConnection) { $cleanupConnection.Dispose() }
            }
        }

        if ($null -ne $primaryError) {
            $primaryError.Exception.Data['MesIngest.CutoverPermissionLifecycle'] = [pscustomobject]$audit
            if ($null -ne $cleanupError) {
                $primaryError.Exception.Data['MesIngest.CutoverPermissionRevocationFailure'] = $cleanupError.Exception.Message
            }
        } elseif ($null -ne $cleanupError) {
            $cleanupError.Exception.Data['MesIngest.CutoverPermissionLifecycle'] = [pscustomobject]$audit
            $primaryError = $cleanupError
        }
    }

    if ($null -ne $primaryError) { throw $primaryError }
    return [pscustomobject][ordered]@{
        CutoverRunId = $CutoverRunId
        ServerIdentity = [string]$Authorization.ServerIdentity
        OldDatabaseName = [string]$Authorization.OldDatabaseName
        OldDatabaseId = [int]$Authorization.OldDatabaseId
        OldDatabaseCreateDateUtc = [string]$Authorization.OldDatabaseCreateDateUtc
        DatabaseDeleted = $databaseDeleted
        PermissionLifecycle = [pscustomobject]$audit
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
        [Parameter(Mandatory = $true)] $Evidence,
        [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')]
        [string] $Stage
    )

    $runId = [string]$Evidence.CutoverRunId
    if ($runId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'CUTOVER_EVIDENCE_INVALID: CutoverRunId is missing or invalid.'
    }
    $directory = [IO.Path]::GetFullPath($EvidenceDirectory)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $stageSuffix = if ([string]::IsNullOrWhiteSpace($Stage)) { '' } else { ".$Stage" }
    $jsonPath = Join-Path $directory "$runId$stageSuffix.json"
    $markdownPath = Join-Path $directory "$runId$stageSuffix.md"
    if ([IO.File]::Exists($jsonPath) -or [IO.File]::Exists($markdownPath)) {
        throw "CUTOVER_EVIDENCE_EXISTS: evidence for CutoverRunId $runId is immutable."
    }

    $json = $Evidence | ConvertTo-Json -Depth 12
    if ($json -match '(?i)"(WorkType|Sublot|BusinessPayload|RawObservation|DemandRawObservation)"\s*:') {
        throw 'CUTOVER_EVIDENCE_CONTAINS_BUSINESS_DATA: evidence contains a forbidden business field.'
    }
    $permissionStatus = 'NOT_RECORDED'
    if ($Evidence -is [Collections.IDictionary] -and $Evidence.Contains('PermissionLifecycle')) {
        $permissionStatus = [string]$Evidence['PermissionLifecycle'].FinalStatus
    } elseif ($null -ne $Evidence.PSObject.Properties['PermissionLifecycle']) {
        $permissionStatus = [string]$Evidence.PermissionLifecycle.FinalStatus
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
        "- Permission lifecycle: $permissionStatus"
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
    DB_NAME(), DB_ID(), databaseIdentity.create_date,
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
INNER JOIN sys.databases AS databaseIdentity ON databaseIdentity.database_id = DB_ID()
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
            $createDateUtc = [DateTime]::SpecifyKind($reader.GetDateTime(4), [DateTimeKind]::Utc).ToString('o')
            $isSystemDatabase = $reader.GetBoolean(5)
            $schemaVersion = [Convert]::ToInt32($reader.GetValue(6), [Globalization.CultureInfo]::InvariantCulture)
            $contractVersion = $reader.GetString(7)
            $historyEpoch = $reader.GetGuid(8).ToString('D')
            $hasDeletePermission = $reader.GetBoolean(9)
            $hasGlobalSessionVisibility = $reader.GetBoolean(10)
            $executionLogin = $reader.GetString(11)

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
        CreateDateUtc = $createDateUtc
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
        [Parameter(Mandatory = $true)] [string] $KeyTokenAlgorithmVersion,
        [string] $DatabaseDeletion = 'NOT_AUTHORIZED_TICKET_23',
        [string] $PermissionStatus = 'NOT_GRANTED',
        [string] $ServerIdentity = 'NOT_RESOLVED',
        [string] $OldDatabaseIdentity = 'NOT_RESOLVED',
        [string] $NewDatabaseIdentity = 'NOT_RESOLVED',
        [string] $ContractSchemaIdentity = 'NOT_RESOLVED',
        [string] $ProjectionIdentity = 'NOT_PROVEN',
        [string] $InterfaceGateSummary = 'NOT_PROVEN',
        [string] $ExecutionIdentity = 'NOT_RESOLVED',
        [string] $StartedAtUtc = 'NOT_RECORDED',
        [string] $CompletedAtUtc = 'NOT_RECORDED'
    )

    Add-Type -AssemblyName System.Diagnostics.EventLog | Out-Null
    $message = "MesIngest CutoverRunId=$CutoverRunId Status=$Status " +
        "HistoryEpoch=$HistoryEpoch TombstoneSha256=$TombstoneSha256 " +
        "KeyTokenAlgorithmVersion=$KeyTokenAlgorithmVersion " +
        "DatabaseDeletion=$DatabaseDeletion PermissionStatus=$PermissionStatus " +
        "ServerIdentity=$ServerIdentity OldDatabase=$OldDatabaseIdentity " +
        "NewDatabase=$NewDatabaseIdentity ContractSchema=$ContractSchemaIdentity " +
        "Projection=$ProjectionIdentity Interfaces=$InterfaceGateSummary " +
        "ExecutionIdentity=$ExecutionIdentity StartedAtUtc=$StartedAtUtc CompletedAtUtc=$CompletedAtUtc"
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

function Invoke-CutoverDropDatabaseIfUnused {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Connection,
        [Parameter(Mandatory = $true)] [string] $DatabaseName
    )

    Invoke-CutoverNonQuery -Connection $Connection -Sql @'
IF DB_ID(@database) IS NULL
    THROW 51025, 'CUTOVER_DELETE_TARGET_DISAPPEARED', 1;
IF EXISTS (
    SELECT 1
    FROM sys.dm_exec_sessions
    WHERE database_id = DB_ID(@database)
      AND session_id <> @@SPID
      AND is_user_process = 1)
    THROW 51025, 'CUTOVER_DELETE_OLD_DATABASE_STILL_IN_USE', 1;
DECLARE @dropSql nvarchar(max) = N'DROP DATABASE ' + QUOTENAME(CONVERT(sysname, @database)) + N';';
EXEC sys.sp_executesql @dropSql;
'@ -Parameters @{ '@database' = $DatabaseName }
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
