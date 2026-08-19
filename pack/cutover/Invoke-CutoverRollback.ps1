#Requires -Version 5.1
<#
.SYNOPSIS
  Whole-deployment rollback drill: stop the new Service, attest that the old programs
  and old configuration are back, and restore the independent pre-cutover backup.

.DESCRIPTION
  Rollback is not a downgrade path inside the new product. It is a deployment-level
  restore of the previous deployment as a whole:

    * the new Service must be stopped and stay stopped;
    * the old programs and the old configuration must already be restored by the
      operator, attested through -PreviousDeploymentRestored;
    * the old database is restored from the independent backup this drill's cutover
      recorded.

  The new binaries never read the restored old database and the old binaries never
  read the new one. There is no mixed or rolling mode, and this script refuses to run
  against a database that currently carries the new MesIngest schema, so it cannot be
  used to "roll back" by overwriting live new-contract data by mistake.

  Restoring requires the same attended, typed confirmation of the resolved server and
  database identity as the cutover. There is no switch that skips it.

.PARAMETER ConnectionString
  Connection string for the SQL Server instance holding the database to restore.
  It must connect through master, not the target database.

.PARAMETER DatabaseName
  The database to restore from BackupPath.

.PARAMETER BackupPath
  Full path, on the SQL Server host, of the pre-cutover full backup.

.PARAMETER NewServiceName
  Windows service name of the new MesIngest Host. Must be absent or stopped.

.PARAMETER PreviousDeploymentRestored
  Must be exactly OLD_PROGRAMS_AND_OLD_CONFIGURATION_RESTORED.

.PARAMETER EvidenceDirectory
  Directory that receives rollback-evidence.json.
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
    [ValidateSet('OLD_PROGRAMS_AND_OLD_CONFIGURATION_RESTORED')]
    [string] $PreviousDeploymentRestored,

    [string] $NewServiceName = 'MesIngest',

    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CutoverSqlTools.ps1')

$startedAt = [DateTimeOffset]::UtcNow

$newService = Get-Service -Name $NewServiceName -ErrorAction SilentlyContinue
$newServiceState = if ($null -eq $newService) { 'NOT_INSTALLED' } else { [string]$newService.Status }
if ($null -ne $newService -and $newService.Status -ne 'Stopped') {
    throw ("ROLLBACK_NEW_SERVICE_STILL_RUNNING: '$NewServiceName' is $newServiceState. " +
        'Stop it before restoring the previous deployment; the two versions never run together.')
}

$connection = Open-CutoverConnection -ConnectionString $ConnectionString -ForbiddenDatabase $DatabaseName

try {
    $identity = Get-CutoverTargetIdentity -Connection $connection -DatabaseName $DatabaseName

    Write-Host "ROLLBACK_TARGET_RESOLVED"
    Write-Host "  server   = $($identity.ServerIdentity)"
    Write-Host "  database = $($identity.DatabaseName)"
    Write-Host "  exists   = $($identity.Exists)"
    Write-Host "  userTables = $($identity.UserTableCount)"

    Assert-CutoverOperatorConfirmation -Identity $identity -Action 'OVERWRITE FROM THE PRE-CUTOVER BACKUP'

    if ($identity.Exists) {
        $sessions = Get-CutoverForeignSessionCount -Connection $connection -DatabaseName $DatabaseName
        if ($sessions -gt 0) {
            throw ("ROLLBACK_TARGET_STILL_IN_USE: $sessions other session(s) are connected to " +
                "[$DatabaseName]. Stop every client first.")
        }
    }

    Invoke-CutoverRestoreDatabase -Connection $connection -DatabaseName $DatabaseName -BackupPath $BackupPath
    $restored = Get-CutoverTargetIdentity -Connection $connection -DatabaseName $DatabaseName
    if (-not $restored.Exists) {
        throw "ROLLBACK_RESTORE_DID_NOT_PRODUCE_A_DATABASE: [$DatabaseName] is absent after RESTORE."
    }

    # A restored pre-cutover database is the old deployment's data. If it presents the
    # new contract's schema instead, the operator restored the wrong backup.
    $newSchemaPresent = [int](Invoke-CutoverScalar -Connection $connection -Sql (
        "SELECT COUNT(*) FROM [$DatabaseName].sys.schemas WHERE name = 'mesingest';"))
    if ($newSchemaPresent -ne 0) {
        throw ('ROLLBACK_BACKUP_IS_NOT_THE_PREVIOUS_DEPLOYMENT: the restored database carries the ' +
            'current MesIngest schema. Restore the pre-cutover backup instead.')
    }

    Write-Host "ROLLBACK_DATABASE_RESTORED: userTables=$($restored.UserTableCount)"

    New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
    $evidencePath = Join-Path $EvidenceDirectory 'rollback-evidence.json'
    [ordered]@{
        schemaVersion = 1
        drill = 'WHOLE_DEPLOYMENT_ROLLBACK'
        status = 'PREVIOUS_DEPLOYMENT_RESTORED'
        startedAtUtc = $startedAt.ToString('o')
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        operator = "$env:USERDOMAIN\$env:USERNAME"
        newServiceName = $NewServiceName
        newServiceState = $newServiceState
        previousDeploymentRestored = $PreviousDeploymentRestored
        target = [ordered]@{
            serverIdentity = $identity.ServerIdentity
            databaseName = $identity.DatabaseName
            userTablesAfterRestore = $restored.UserTableCount
            currentSchemaPresent = $false
        }
        backup = [ordered]@{
            path = $BackupPath
            sha256 = (Get-CutoverBackupSha256 -BackupPath $BackupPath)
        }
        mixedModeSupported = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    Write-Host "ROLLBACK_EVIDENCE_WRITTEN: $evidencePath"
    Write-Host "CUTOVER_ROLLBACK_PASSED"
    exit 0
}
finally {
    $connection.Dispose()
}
