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

function Open-CutoverConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $ConnectionString,
        [Parameter(Mandatory = $true)] [string] $ForbiddenDatabase
    )

    Add-Type -AssemblyName System.Data | Out-Null
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    # Dropping or restoring a database from a connection that is inside it fails or, worse,
    # succeeds against a database the operator did not mean to name. Force master.
    if ($builder.InitialCatalog -and
        $builder.InitialCatalog -ne 'master' -and
        [string]::Equals($builder.InitialCatalog, $ForbiddenDatabase, [StringComparison]::OrdinalIgnoreCase)) {
        throw ("CUTOVER_CONNECTION_TARGETS_THE_DATABASE_ITSELF: connect through master, " +
            "not Initial Catalog=$($builder.InitialCatalog).")
    }

    $builder.InitialCatalog = 'master'
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
