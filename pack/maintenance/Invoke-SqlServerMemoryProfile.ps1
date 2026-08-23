#Requires -Version 5.1
<#
.SYNOPSIS
  Apply, verify, and audit the bounded MesIngest SQL Server memory profiles.

.DESCRIPTION
  ApplyNormal fixes max server memory at 1536 MB. RunMaintenance raises it to
  2048 MB only around one explicit child script, then restores 1536 MB after
  success, failure, timeout, or child-reported cancellation. Diagnose is read-only.
  Validate rejects unsupported values before opening SQL Server.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Validate', 'ApplyNormal', 'RunMaintenance', 'Diagnose')]
    [string] $Action,
    [ValidateRange(1, 2147483647)] [int] $RequestedMaxServerMemoryMb = 1536,
    [string] $ExpectedMachineName = '',
    [string] $ExpectedInstanceName = '',
    [string] $ConfirmInstance = '',
    [string] $Reason = '',
    [string] $MaintenanceScriptPath = '',
    [string[]] $MaintenanceArgument = @(),
    [ValidateRange(1, 86400)] [int] $MaintenanceTimeoutSeconds = 3600,
    [ValidateRange(1, 60)] [int] $StabilitySampleCount = 3,
    [ValidateRange(0, 300)] [int] $StabilitySampleIntervalSeconds = 10,
    [ValidateRange(1, 120)] [int] $SqlConnectionTimeoutSeconds = 5,
    [ValidateRange(1, 300)] [int] $SqlCommandTimeoutSeconds = 30,
    [string] $OutputRoot = (Join-Path $env:ProgramData 'MesIngest\evidence\sql-memory-profile')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

$normalMb = 1536
$maintenanceMb = 2048
$rejectedMb = 800
$processTargetMb = 2048
$allowed = @($normalMb, $maintenanceMb)

if ($allowed -notcontains $RequestedMaxServerMemoryMb) {
    throw ("UNSUPPORTED_MEMORY_PROFILE: only {0} MB normal and {1} MB maintenance are allowed; " +
        "{2} MB was requested. Values at or below {3} MB are known-unrunnable and are rejected without a load test." -f
        $normalMb, $maintenanceMb, $RequestedMaxServerMemoryMb, $rejectedMb)
}
if ($Action -eq 'ApplyNormal' -and $RequestedMaxServerMemoryMb -ne $normalMb) {
    throw 'PROFILE_ACTION_MISMATCH: ApplyNormal accepts only 1536 MB.'
}
if ($Action -eq 'RunMaintenance' -and $RequestedMaxServerMemoryMb -ne $maintenanceMb) {
    throw 'PROFILE_ACTION_MISMATCH: RunMaintenance accepts only 2048 MB.'
}
if ($Action -eq 'Validate') {
    Write-Output "MESINGEST_SQL_MEMORY_PROFILE_VALID: maxServerMemoryMb=$RequestedMaxServerMemoryMb"
    exit 0
}

$expectedConfirmation = "$ExpectedMachineName\$ExpectedInstanceName"
if ([string]::IsNullOrWhiteSpace($ExpectedMachineName) -or
    [string]::IsNullOrWhiteSpace($ExpectedInstanceName) -or
    [string]::IsNullOrWhiteSpace($ConfirmInstance)) {
    throw 'MISSING_TARGET_CONFIRMATION: expected machine, instance, and exact typed confirmation are required.'
}
if (-not [string]::Equals($ConfirmInstance, $expectedConfirmation, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TARGET_CONFIRMATION_MISMATCH: type the exact target '$expectedConfirmation'."
}
if ($Action -eq 'RunMaintenance') {
    if ([string]::IsNullOrWhiteSpace($Reason) -or $Reason.Length -gt 512 -or
        $Reason.IndexOfAny(@([char]13, [char]10)) -ge 0) {
        throw 'INVALID_MAINTENANCE_REASON: use one non-empty line of at most 512 characters.'
    }
    if ([string]::IsNullOrWhiteSpace($MaintenanceScriptPath)) {
        throw 'MISSING_MAINTENANCE_SCRIPT: an explicit .ps1 path is required.'
    }
    $maintenancePath = [IO.Path]::GetFullPath($MaintenanceScriptPath)
    if (-not (Test-Path -LiteralPath $maintenancePath -PathType Leaf) -or
        [IO.Path]::GetExtension($maintenancePath) -ine '.ps1') {
        throw 'INVALID_MAINTENANCE_SCRIPT: the explicit target must be an existing .ps1 file.'
    }
} else {
    $maintenancePath = ''
}

$secretConnection = [Environment]::GetEnvironmentVariable('MES_INGEST_SQLSERVER_ADMIN')
if ([string]::IsNullOrWhiteSpace($secretConnection)) {
    throw 'SQL_ADMIN_CONNECTION_NOT_CONFIGURED: set MES_INGEST_SQLSERVER_ADMIN to a master connection.'
}
try {
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($secretConnection)
} catch {
    throw 'SQL_ADMIN_CONNECTION_INVALID'
}
if ($builder.DataSource.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'LOCALDB_REJECTED'
}
if ($builder.InitialCatalog -ine 'master') {
    throw 'MASTER_CONNECTION_REQUIRED'
}
$builder['Connect Timeout'] = $SqlConnectionTimeoutSeconds
$builder['Application Name'] = 'MesIngest.SqlServerMemoryProfile'

function Invoke-Rows {
    param(
        [System.Data.SqlClient.SqlConnection] $Connection,
        [string] $Text,
        [hashtable] $Parameters = @{}
    )
    $command = $Connection.CreateCommand()
    try {
        $command.CommandTimeout = $SqlCommandTimeoutSeconds
        $command.CommandText = $Text
        foreach ($entry in $Parameters.GetEnumerator()) {
            [void]$command.Parameters.AddWithValue([string]$entry.Key, $entry.Value)
        }
        $reader = $command.ExecuteReader()
        try {
            $rows = New-Object System.Collections.ArrayList
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                    $value = $reader.GetValue($i)
                    if ($value -is [DBNull]) { $value = $null }
                    $row[$reader.GetName($i)] = $value
                }
                [void]$rows.Add([pscustomobject]$row)
            }
            return @($rows)
        } finally { $reader.Dispose() }
    } finally { $command.Dispose() }
}

function Invoke-NonQuery {
    param(
        [System.Data.SqlClient.SqlConnection] $Connection,
        [string] $Text,
        [hashtable] $Parameters = @{}
    )
    $command = $Connection.CreateCommand()
    try {
        $command.CommandTimeout = $SqlCommandTimeoutSeconds
        $command.CommandText = $Text
        foreach ($entry in $Parameters.GetEnumerator()) {
            [void]$command.Parameters.AddWithValue([string]$entry.Key, $entry.Value)
        }
        [void]$command.ExecuteNonQuery()
    } finally { $command.Dispose() }
}

function Get-One {
    param([System.Data.SqlClient.SqlConnection] $Connection, [string] $Text)
    $rows = @(Invoke-Rows $Connection $Text)
    if ($rows.Count -ne 1) { throw 'SQL_REQUIRED_SINGLE_ROW_MISSING' }
    return $rows[0]
}

function Get-Configuration {
    param([System.Data.SqlClient.SqlConnection] $Connection)
    return Get-One $Connection @"
SELECT CONVERT(int, value) AS configured_mb,
       CONVERT(int, value_in_use) AS value_in_use_mb
FROM sys.configurations
WHERE name = N'max server memory (MB)';
"@
}

function Set-Configuration {
    param([System.Data.SqlClient.SqlConnection] $Connection, [int] $TargetMb)
    if ($allowed -notcontains $TargetMb) { throw 'UNSUPPORTED_MEMORY_PROFILE' }
    $advanced = Get-One $Connection @"
SELECT CONVERT(int, value_in_use) AS value_in_use
FROM sys.configurations WHERE name = N'show advanced options';
"@
    $restoreAdvanced = [int]$advanced.value_in_use -eq 0
    try {
        if ($restoreAdvanced) {
            Invoke-NonQuery $Connection @"
EXEC sys.sp_configure N'show advanced options', 1;
RECONFIGURE;
"@
        }
        Invoke-NonQuery $Connection @"
EXEC sys.sp_configure N'max server memory (MB)', @targetMb;
RECONFIGURE;
"@ @{ '@targetMb' = $TargetMb }
    } finally {
        if ($restoreAdvanced) {
            Invoke-NonQuery $Connection @"
EXEC sys.sp_configure N'show advanced options', 0;
RECONFIGURE;
"@
        }
    }
    $result = Get-Configuration $Connection
    if ([int]$result.configured_mb -ne $TargetMb -or [int]$result.value_in_use_mb -ne $TargetMb) {
        throw 'MEMORY_PROFILE_VERIFICATION_FAILED'
    }
    return $result
}

function Get-Diagnostics {
    param([System.Data.SqlClient.SqlConnection] $Connection)
    $committed = Get-One $Connection @"
SELECT CONVERT(bigint, committed_kb) AS committed_kb,
       CONVERT(bigint, committed_target_kb) AS committed_target_kb,
       CONVERT(bigint, visible_target_kb) AS visible_target_kb
FROM sys.dm_os_sys_info;
"@
    $workspace = @(Invoke-Rows $Connection @"
SELECT RTRIM(counter_name) AS counter_name, CONVERT(bigint, cntr_value) AS value
FROM sys.dm_os_performance_counters
WHERE counter_name IN (
 N'Granted Workspace Memory (KB)', N'Maximum Workspace Memory (KB)',
 N'Memory Grants Outstanding', N'Memory Grants Pending',
 N'Total Server Memory (KB)', N'Target Server Memory (KB)')
ORDER BY counter_name;
"@)
    $grants = Get-One $Connection @"
SELECT CONVERT(bigint, COUNT_BIG(*)) AS grant_count,
 CONVERT(bigint, COALESCE(SUM(CASE WHEN grant_time IS NULL THEN 1 ELSE 0 END), 0)) AS waiting_count,
 CONVERT(bigint, COALESCE(SUM(CONVERT(bigint, requested_memory_kb)), 0)) AS requested_memory_kb,
 CONVERT(bigint, COALESCE(SUM(CONVERT(bigint, granted_memory_kb)), 0)) AS granted_memory_kb,
 CONVERT(bigint, COALESCE(MAX(wait_time_ms), 0)) AS max_wait_time_ms
FROM sys.dm_exec_query_memory_grants;
"@
    $semaphores = @(Invoke-Rows $Connection @"
SELECT resource_semaphore_id, target_memory_kb, available_memory_kb, granted_memory_kb,
 used_memory_kb, grantee_count, waiter_count, timeout_error_count, forced_grant_count
FROM sys.dm_exec_query_resource_semaphores ORDER BY resource_semaphore_id;
"@)
    $waits = @(Invoke-Rows $Connection @"
SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type IN (N'RESOURCE_SEMAPHORE', N'RESOURCE_SEMAPHORE_QUERY_COMPILE')
ORDER BY wait_type;
"@)
    $spill = Get-One $Connection @"
SELECT CONVERT(bigint, COUNT_BIG(*)) AS cached_query_count,
 CONVERT(bigint, COALESCE(SUM(CONVERT(bigint, total_spills)), 0)) AS total_spills,
 CONVERT(bigint, COALESCE(MAX(CONVERT(bigint, last_spills)), 0)) AS max_last_spills
FROM sys.dm_exec_query_stats;
"@
    $errors = @(Invoke-Rows $Connection @"
EXEC master.dbo.xp_readerrorlog 0, 1, N'Error: 701';
"@)
    $errorDates = @($errors | ForEach-Object { $_.LogDate } |
        Where-Object { $null -ne $_ } | Sort-Object)
    $samples = New-Object System.Collections.ArrayList
    for ($sampleIndex = 0; $sampleIndex -lt $StabilitySampleCount; $sampleIndex++) {
        $sample = Get-One $Connection @"
SELECT physical_memory_in_use_kb, locked_page_allocations_kb,
 large_page_allocations_kb, memory_utilization_percentage,
 process_physical_memory_low, process_virtual_memory_low
FROM sys.dm_os_process_memory;
"@
        [void]$samples.Add([pscustomobject][ordered]@{
            observedAt = [DateTimeOffset]::UtcNow.ToString('o')
            physicalMemoryInUseKb = [long]$sample.physical_memory_in_use_kb
            lockedPageAllocationsKb = [long]$sample.locked_page_allocations_kb
            largePageAllocationsKb = [long]$sample.large_page_allocations_kb
            memoryUtilizationPercentage = [int]$sample.memory_utilization_percentage
            processPhysicalMemoryLow = [bool]$sample.process_physical_memory_low
            processVirtualMemoryLow = [bool]$sample.process_virtual_memory_low
        })
        if ($sampleIndex + 1 -lt $StabilitySampleCount -and $StabilitySampleIntervalSeconds -gt 0) {
            Start-Sleep -Seconds $StabilitySampleIntervalSeconds
        }
    }
    $maxKb = [long](($samples | Measure-Object physicalMemoryInUseKb -Maximum).Maximum)
    return [pscustomobject][ordered]@{
        capturedAt = [DateTimeOffset]::UtcNow.ToString('o')
        committedMemory = [ordered]@{
            available = $true
            committedKb = [long]$committed.committed_kb
            committedTargetKb = [long]$committed.committed_target_kb
            visibleTargetKb = [long]$committed.visible_target_kb
        }
        workspaceMemory = [ordered]@{ available = $true; counters = @($workspace) }
        grantWaits = [ordered]@{
            available = $true
            grantCount = [long]$grants.grant_count
            waitingCount = [long]$grants.waiting_count
            requestedMemoryKb = [long]$grants.requested_memory_kb
            grantedMemoryKb = [long]$grants.granted_memory_kb
            maxWaitTimeMs = [long]$grants.max_wait_time_ms
        }
        resourceSemaphore = [ordered]@{
            available = $true
            semaphores = @($semaphores)
            waits = @($waits)
        }
        error701 = [ordered]@{
            available = $true
            count = $errors.Count
            firstObservedAt = if ($errorDates.Count -gt 0) {
                ([DateTime]$errorDates[0]).ToUniversalTime().ToString('o')
            } else { $null }
            lastObservedAt = if ($errorDates.Count -gt 0) {
                ([DateTime]$errorDates[-1]).ToUniversalTime().ToString('o')
            } else { $null }
            textPersisted = $false
        }
        spills = [ordered]@{
            available = $true
            cachedQueryCount = [long]$spill.cached_query_count
            totalSpills = [long]$spill.total_spills
            maxLastSpills = [long]$spill.max_last_spills
        }
        processEnvelope = [ordered]@{
            available = $true
            sampleCount = $samples.Count
            intervalSeconds = $StabilitySampleIntervalSeconds
            targetMb = $processTargetMb
            maxObservedMb = [math]::Round($maxKb / 1024.0, 2)
            satisfied = $maxKb -le ([long]$processTargetMb * 1024L)
            samples = @($samples)
        }
    }
}

function Get-FileSha256 {
    param([string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Quote-ChildArgument {
    param([AllowEmptyString()][string] $Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [Text.RegularExpressions.Regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [Text.RegularExpressions.Regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-Maintenance {
    $arguments = @()
    foreach ($item in @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', $maintenancePath) + @($MaintenanceArgument)) {
        $arguments += Quote-ChildArgument ([string]$item)
    }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'powershell.exe'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = $arguments -join ' '
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'MAINTENANCE_CHILD_START_FAILED' }
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MaintenanceTimeoutSeconds)
        while (-not $process.HasExited) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                try { $process.Kill() } catch { }
                [void]$process.WaitForExit(5000)
                return [pscustomobject]@{ ExitCode = 1460; Outcome = 'TIMED_OUT' }
            }
            [void]$process.WaitForExit(250)
        }
        $process.WaitForExit()
        $process.Refresh()
        $childExitCode = [int]$process.ExitCode
        $outcome = if ($childExitCode -eq 0) {
            'SUCCEEDED'
        } elseif ($childExitCode -eq 1223) {
            'CANCELLED'
        } else {
            'FAILED'
        }
        return [pscustomobject]@{ ExitCode = $childExitCode; Outcome = $outcome }
    } finally { $process.Dispose() }
}

$startedAt = [DateTimeOffset]::UtcNow
$runId = 'run-{0}-{1}' -f $startedAt.ToString('yyyyMMddTHHmmssZ'),
    ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $runId
if (Test-Path -LiteralPath $runDirectory) { throw 'AUDIT_RUN_ALREADY_EXISTS' }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null

$operatorName = try { [Security.Principal.WindowsIdentity]::GetCurrent().Name }
    catch { "$env:USERDOMAIN\$env:USERNAME" }
$status = 'FAILED'
$code = 'UNEXPECTED_FAILURE'
$connection = $null
$diagnostics = $null
$maintenanceDiagnostics = $null
$restoreRequired = $false
$succeeded = $false
$configuration = [ordered]@{
    beforeMb = $null
    requestedMb = $RequestedMaxServerMemoryMb
    afterMb = $null
    valueInUseMb = $null
    restoreAttempted = $false
    restoredMb = $null
    restoreSucceeded = $false
}
$target = [ordered]@{
    dataSource = $builder.DataSource
    expectedMachineName = $ExpectedMachineName
    expectedInstanceName = $ExpectedInstanceName
    actualMachineName = ''
    actualInstanceName = ''
    productVersion = ''
}
$maintenance = [ordered]@{
    reason = if ($Action -eq 'RunMaintenance') { $Reason } else { '' }
    script = if ($Action -eq 'RunMaintenance') { [IO.Path]::GetFileName($maintenancePath) } else { '' }
    startedAt = $null
    completedAt = $null
    exitCode = $null
    outcome = if ($Action -eq 'RunMaintenance') { 'NOT_STARTED' } else { 'NOT_APPLICABLE' }
}

try {
    try {
        $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        $connection.Open()
    } catch { throw 'SQL_CONNECTION_FAILED' }
    try {
        $identity = Get-One $connection @"
SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) AS machine_name,
 ISNULL(CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), N'MSSQLSERVER') AS instance_name,
 CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS product_version,
 CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER SETTINGS')) AS can_alter_settings,
 CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE')) AS can_view_server_state;
"@
    } catch { throw 'SQL_IDENTITY_READ_FAILED' }
    $target.actualMachineName = [string]$identity.machine_name
    $target.actualInstanceName = [string]$identity.instance_name
    $target.productVersion = [string]$identity.product_version
    if ($target.actualMachineName -ine $ExpectedMachineName -or
        $target.actualInstanceName -ine $ExpectedInstanceName) {
        throw 'SQL_INSTANCE_IDENTITY_MISMATCH'
    }
    if ([int]$identity.can_alter_settings -ne 1 -or [int]$identity.can_view_server_state -ne 1) {
        throw 'SQL_ADMIN_PERMISSION_REQUIRED'
    }
    try { $before = Get-Configuration $connection }
        catch { throw 'SQL_CONFIGURATION_READ_FAILED' }
    $configuration.beforeMb = [int]$before.value_in_use_mb

    if ($Action -eq 'ApplyNormal') {
        try { $applied = Set-Configuration $connection $normalMb }
            catch { throw 'NORMAL_PROFILE_APPLY_FAILED' }
        $configuration.afterMb = [int]$applied.configured_mb
        $configuration.valueInUseMb = [int]$applied.value_in_use_mb
        try { $diagnostics = Get-Diagnostics $connection }
            catch { throw 'MEMORY_DIAGNOSTICS_FAILED' }
        if (-not $diagnostics.processEnvelope.satisfied) {
            throw 'PROCESS_MEMORY_ENVELOPE_EXCEEDED'
        }
        $succeeded = $true
    } elseif ($Action -eq 'Diagnose') {
        if ($allowed -notcontains [int]$before.value_in_use_mb) {
            throw 'UNSUPPORTED_CURRENT_MEMORY_PROFILE'
        }
        $configuration.afterMb = [int]$before.configured_mb
        $configuration.valueInUseMb = [int]$before.value_in_use_mb
        try { $diagnostics = Get-Diagnostics $connection }
            catch { throw 'MEMORY_DIAGNOSTICS_FAILED' }
        $succeeded = $true
    } else {
        if ([int]$before.configured_mb -ne $normalMb -or [int]$before.value_in_use_mb -ne $normalMb) {
            throw 'NORMAL_PROFILE_REQUIRED_BEFORE_MAINTENANCE'
        }
        $restoreRequired = $true
        try { $elevated = Set-Configuration $connection $maintenanceMb }
            catch { throw 'MAINTENANCE_PROFILE_APPLY_FAILED' }
        $configuration.afterMb = [int]$elevated.configured_mb
        $configuration.valueInUseMb = [int]$elevated.value_in_use_mb
        try { $maintenanceDiagnostics = Get-Diagnostics $connection }
            catch { throw 'MEMORY_DIAGNOSTICS_FAILED' }
        $maintenance.startedAt = [DateTimeOffset]::UtcNow.ToString('o')
        $child = Invoke-Maintenance
        $maintenance.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        $maintenance.exitCode = [int]$child.ExitCode
        $maintenance.outcome = [string]$child.Outcome
        if ($child.Outcome -eq 'CANCELLED') { throw 'MAINTENANCE_CANCELLED' }
        if ($child.Outcome -eq 'TIMED_OUT') { throw 'MAINTENANCE_TIMED_OUT' }
        if ($child.Outcome -ne 'SUCCEEDED') { throw 'MAINTENANCE_COMMAND_FAILED' }
        $succeeded = $true
    }
} catch {
    $message = [string]$_.Exception.Message
    $code = if ($message -match '^([A-Z][A-Z0-9_]+)') { $Matches[1] } else { 'UNEXPECTED_FAILURE' }
    $status = if ($code -eq 'MAINTENANCE_CANCELLED') { 'CANCELLED' } else { 'FAILED' }
} finally {
    if ($Action -eq 'RunMaintenance' -and $restoreRequired -and $null -ne $connection) {
        $configuration.restoreAttempted = $true
        try {
            $restored = Set-Configuration $connection $normalMb
            $configuration.restoredMb = [int]$restored.value_in_use_mb
            $configuration.restoreSucceeded = $configuration.restoredMb -eq $normalMb
            if (-not $configuration.restoreSucceeded) { throw 'RESTORE_VERIFICATION_FAILED' }
            $diagnostics = Get-Diagnostics $connection
            if (-not $diagnostics.processEnvelope.satisfied) {
                throw 'RESTORED_PROCESS_MEMORY_ENVELOPE_EXCEEDED'
            }
        } catch {
            $status = 'RESTORE_FAILED'
            $code = 'NORMAL_PROFILE_RESTORE_FAILED'
            $succeeded = $false
            $configuration.restoreSucceeded = $false
        }
    }
    if ($null -ne $connection) { $connection.Dispose() }
    if ($succeeded -and ($Action -ne 'RunMaintenance' -or $configuration.restoreSucceeded)) {
        $status = 'SUCCEEDED'
        $code = ''
    }

    $completedAt = [DateTimeOffset]::UtcNow
    $report = [ordered]@{
        schemaVersion = 1
        runId = $runId
        action = $Action
        status = $status
        diagnosticCode = $code
        startedAt = $startedAt.ToString('o')
        completedAt = $completedAt.ToString('o')
        operator = [ordered]@{ windowsIdentity = $operatorName }
        reason = if ($Action -eq 'RunMaintenance') { $Reason } else { '' }
        target = $target
        profiles = [ordered]@{
            normalMaxServerMemoryMb = $normalMb
            maintenanceMaxServerMemoryMb = $maintenanceMb
            knownUnrunnableAtOrBelowMb = $rejectedMb
            processEnvelopeTargetMb = $processTargetMb
        }
        configuration = $configuration
        maintenance = $maintenance
        maintenanceDiagnostics = $maintenanceDiagnostics
        diagnostics = $diagnostics
        safety = [ordered]@{
            credentialsWritten = $false
            connectionStringWritten = $false
            partialApplicationReportedAsSuccess = $false
            exactInstanceConfirmed = $true
        }
    }
    $jsonPath = Join-Path $runDirectory 'sql-memory-profile.json'
    $markdownPath = Join-Path $runDirectory 'sql-memory-profile.md'
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    @(
        '# MesIngest SQL Server memory profile',
        '',
        "- Run: $runId",
        "- Action/status: $Action / $status",
        "- Diagnostic: $code",
        "- Operator: $operatorName",
        "- Target: $($target.actualMachineName)\$($target.actualInstanceName)",
        "- Configuration before/requested/after/in-use: $($configuration.beforeMb) / $($configuration.requestedMb) / $($configuration.afterMb) / $($configuration.valueInUseMb) MB",
        "- Restore attempted/succeeded/restored: $($configuration.restoreAttempted) / $($configuration.restoreSucceeded) / $($configuration.restoredMb) MB",
        "- Credentials/connection string written: false / false"
    ) | Set-Content -LiteralPath $markdownPath -Encoding UTF8
    $inventory = foreach ($path in @($jsonPath, $markdownPath)) {
        $item = Get-Item -LiteralPath $path
        [pscustomobject][ordered]@{
            file = $item.Name
            length = $item.Length
            sha256 = Get-FileSha256 $path
        }
    }
    $inventory | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $runDirectory 'sha256-inventory.json') -Encoding UTF8
}

Write-Output "MESINGEST_SQL_MEMORY_PROFILE: action=$Action status=$status diagnostic=$code run=$runDirectory"
if ($status -ne 'SUCCEEDED') {
    Write-Error "MESINGEST_SQL_MEMORY_PROFILE_FAILED: $code"
    exit 1
}
