#Requires -Version 7
<#
.SYNOPSIS
  Empty-database cutover drill: back up, verify, drop, and recreate one confirmed
  MesIngest database so the new Host bootstraps an empty schema (ADR-mes-0017).

.DESCRIPTION
  This is the ONLY place in the product that deletes a database. The Host, the Watch
  client, and install/uninstall never do. Deletion here is deliberately attended:

    * the operator must have already stopped the old Service, every Watch, and every
      external consumer, and must say so through -DowntimeAcknowledgement;
    * the script resolves the real server and database identity from the connection
      itself and prints it;
    * the operator must then type that resolved identity back, at the console, before
      anything is dropped. There is no switch that skips this. A redirected or
      non-interactive stdin cannot satisfy it, so an unattended run stops at the
      confirmation instead of dropping an unconfirmed database;
    * a full backup with CHECKSUM is taken and RESTORE VERIFYONLY'd first, and its
      SHA-256 is recorded. That backup is the rollback asset — keep it independent of
      the new database.

  After the drop the script creates an empty database of the same name and proves it
  has zero user tables. It never creates MesIngest schema itself: the first start of
  the new Host does that, and the first complete SUCCESS round is the earliest
  provable point of the new history.

.PARAMETER ConnectionString
  Connection string for the SQL Server instance that holds the database being replaced.
  It must NOT name the target database (connect through master), so the drop is not
  performed from inside the database it removes.

.PARAMETER DatabaseName
  The database to back up, drop, and recreate empty.

.PARAMETER BackupPath
  Full path, on the SQL Server host, for the pre-cutover full backup.

.PARAMETER DowntimeAcknowledgement
  Must be exactly OLD_HOST_WATCH_AND_CONSUMERS_STOPPED.

.PARAMETER EvidenceDirectory
  Directory that receives cutover-evidence.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConnectionString,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$')]
    [string] $DatabaseName,

    [Parameter(Mandatory = $true)]
    [string] $BackupPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('OLD_HOST_WATCH_AND_CONSUMERS_STOPPED')]
    [string] $DowntimeAcknowledgement,

    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CutoverSqlTools.ps1')

$startedAt = [DateTimeOffset]::UtcNow
$connection = Open-CutoverConnection -ConnectionString $ConnectionString -ForbiddenDatabase $DatabaseName

try {
    $identity = Get-CutoverTargetIdentity -Connection $connection -DatabaseName $DatabaseName
    if (-not $identity.Exists) {
        throw "CUTOVER_TARGET_NOT_FOUND: [$DatabaseName] does not exist on $($identity.ServerIdentity)."
    }

    Write-Host "CUTOVER_TARGET_RESOLVED"
    Write-Host "  server   = $($identity.ServerIdentity)"
    Write-Host "  database = $($identity.DatabaseName)"
    Write-Host "  created  = $($identity.CreateDate)"
    Write-Host "  userTables = $($identity.UserTableCount)"

    $sessions = Get-CutoverForeignSessionCount -Connection $connection -DatabaseName $DatabaseName
    if ($sessions -gt 0) {
        throw ("CUTOVER_TARGET_STILL_IN_USE: $sessions other session(s) are connected to " +
            "[$DatabaseName]. Stop the old Service, every Watch, and every external consumer first.")
    }

    # Back up first, then ask. The backup is not destructive, so doing it before the
    # confirmation spends that confirmation on the drop alone, and an unusable backup
    # path fails before anyone is asked to authorize anything.
    Write-Host "CUTOVER_BACKUP_STARTED: $BackupPath"
    Invoke-CutoverFullBackup -Connection $connection -DatabaseName $DatabaseName -BackupPath $BackupPath
    Invoke-CutoverBackupVerify -Connection $connection -BackupPath $BackupPath
    $backupSha256 = Get-CutoverBackupSha256 -BackupPath $BackupPath
    Write-Host "CUTOVER_BACKUP_VERIFIED: sha256=$backupSha256"

    Assert-CutoverOperatorConfirmation -Identity $identity -Action 'DESTROY'

    Invoke-CutoverDropDatabase -Connection $connection -DatabaseName $DatabaseName
    Write-Host "CUTOVER_OLD_DATABASE_DROPPED: $($identity.ServerIdentity)/$DatabaseName"

    Invoke-CutoverCreateEmptyDatabase -Connection $connection -DatabaseName $DatabaseName
    $after = Get-CutoverTargetIdentity -Connection $connection -DatabaseName $DatabaseName
    if (-not $after.Exists -or $after.UserTableCount -ne 0) {
        throw "CUTOVER_EMPTY_DATABASE_NOT_CLEAN: [$DatabaseName] does not present zero user tables."
    }

    Write-Host "CUTOVER_EMPTY_DATABASE_READY: userTables=0"

    New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
    $evidencePath = Join-Path $EvidenceDirectory 'cutover-evidence.json'
    [ordered]@{
        schemaVersion = 1
        drill = 'EMPTY_DATABASE_CUTOVER'
        status = 'EMPTY_DATABASE_READY_FOR_NEW_HOST'
        startedAtUtc = $startedAt.ToString('o')
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        operator = "$env:USERDOMAIN\$env:USERNAME"
        downtimeAcknowledgement = $DowntimeAcknowledgement
        target = [ordered]@{
            serverIdentity = $identity.ServerIdentity
            databaseName = $identity.DatabaseName
            userTablesBefore = $identity.UserTableCount
            userTablesAfter = $after.UserTableCount
        }
        backup = [ordered]@{
            path = $BackupPath
            sha256 = $backupSha256
            verified = $true
            role = 'INDEPENDENT_ROLLBACK_ASSET'
        }
        nextStep = 'Start the new Host; it bootstraps the schema and the first complete SUCCESS round begins the new history.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    Write-Host "CUTOVER_EVIDENCE_WRITTEN: $evidencePath"
    Write-Host "EMPTY_DATABASE_CUTOVER_PASSED"
    exit 0
}
finally {
    $connection.Dispose()
}
