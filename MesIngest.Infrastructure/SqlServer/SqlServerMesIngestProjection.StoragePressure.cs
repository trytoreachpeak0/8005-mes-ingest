using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection :
    IStoragePressureOperations
{
    public async Task<ResolvedDatabaseVolume> ResolveDatabaseVolumeAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, physical_name
            FROM sys.database_files
            WHERE state = 0
            ORDER BY file_id;
            """;
        var files = new List<DatabaseFileLocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new DatabaseFileLocation(reader.GetString(0), reader.GetString(1)));
        }

        return DatabaseVolumeResolver.Resolve(files);
    }

    public async Task<StoragePressureStateSnapshot> ObserveStoragePressureAsync(
        ResolvedDatabaseVolume databaseVolume,
        VolumeSpaceSample sample,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateObservation(databaseVolume, sample);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadStoragePressureStateAsync(
                connection,
                transaction,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
            var decision = StoragePressurePolicy.Evaluate(sample);
            var status = current.IsPaused
                ? StoragePressureStatuses.Paused
                : decision switch
                {
                    StoragePressureDecision.EnterPause => StoragePressureStatuses.Paused,
                    StoragePressureDecision.CriticalWarning => StoragePressureStatuses.Warning,
                    _ => StoragePressureStatuses.Healthy,
                };
            var entersPause = !current.IsPaused
                && string.Equals(status, StoragePressureStatuses.Paused, StringComparison.Ordinal);
            var pauseId = entersPause ? NewId() : current.PauseId;
            var pausedAt = entersPause ? observedAt.ToUniversalTime() : current.PausedAt;
            var pauseReason = entersPause
                ? $"AVAILABLE_SPACE_BELOW_{StoragePressurePolicy.PauseThresholdPercent:0}_PERCENT"
                : current.PauseReason;
            var recoveryAuditId = entersPause ? null : current.RecoveryAuditId;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE mesingest.StoragePressureState
                SET StoragePressureStatus = @status,
                    DatabaseName = DB_NAME(),
                    DatabaseFilePath = @databaseFilePath,
                    VolumeRoot = @volumeRoot,
                    TotalBytes = @totalBytes,
                    AvailableBytes = @availableBytes,
                    ObservedAt = @observedAt,
                    PausedAt = @pausedAt,
                    PauseId = @pauseId,
                    PauseReason = @pauseReason,
                    RecoveryAuditId = @recoveryAuditId
                WHERE Id = 1;
                """;
            AddNVarChar(command, "@status", 32, status);
            AddNVarChar(command, "@databaseFilePath", 1024, JoinDatabasePaths(databaseVolume));
            AddNVarChar(command, "@volumeRoot", 512, sample.VolumeRoot);
            command.Parameters.Add("@totalBytes", SqlDbType.BigInt).Value = sample.TotalBytes;
            command.Parameters.Add("@availableBytes", SqlDbType.BigInt).Value = sample.AvailableBytes;
            AddDateTimeOffset(command, "@observedAt", observedAt.ToUniversalTime());
            AddNullableDateTimeOffset(command, "@pausedAt", pausedAt);
            AddNullableNVarChar(command, "@pauseId", 64, pauseId);
            AddNullableNVarChar(command, "@pauseReason", 256, pauseReason);
            AddNullableNVarChar(command, "@recoveryAuditId", 64, recoveryAuditId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var result = await ReadStoragePressureStateAsync(
                connection,
                transaction,
                forUpdate: false,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<StoragePressureStateSnapshot> ReadStoragePressureStateAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStoragePressureStateAsync(
            connection,
            transaction: null,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoragePressureStateSnapshot> ResumeStoragePressureAsync(
        StoragePressureRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabaseName);
        ArgumentNullException.ThrowIfNull(request.HistoryEpoch);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 512)
        {
            throw new ArgumentException("A recovery reason of at most 512 characters is required.", nameof(request));
        }

        var context = await _localAdministrationContextProvider
            .GetContextAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        var databaseVolume = await ResolveDatabaseVolumeAsync(cancellationToken).ConfigureAwait(false);
        var sample = _volumeSpaceReader.Read(databaseVolume.VolumeRoot);
        ValidateObservation(databaseVolume, sample);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadStoragePressureStateAsync(
                connection,
                transaction,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
            var databaseHealthy = await IsDatabaseHealthyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            StoragePressureRecoveryPolicy.Validate(
                context,
                current,
                request,
                sample,
                databaseHealthy);

            if (!current.IsPaused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var recoveredAt = _timeProvider.GetUtcNow();
            var auditId = NewId();
            await using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText = """
                    INSERT INTO mesingest.LocalAdministrationAudits
                        (AuditId, Operation, OperationTargetId, HistoryEpoch, DatabaseName,
                         ExecutionIdentity, DatabaseHost, Reason, RiskAcceptance,
                         StatusBefore, StatusAfter, OccurredAt,
                         VolumeRoot, TotalBytes, AvailableBytes)
                    VALUES
                        (@auditId, N'STORAGE_PRESSURE_RECOVERY', @pauseId,
                         @historyEpoch, @databaseName, @executionIdentity, @databaseHost,
                         @reason, NULL, @statusBefore, @statusAfter, @recoveredAt,
                         @volumeRoot, @totalBytes, @availableBytes);
                    """;
                AddNVarChar(audit, "@auditId", 64, auditId);
                AddNVarChar(audit, "@pauseId", 64, current.PauseId!);
                audit.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value = current.HistoryEpoch.Value;
                AddNVarChar(audit, "@databaseName", 128, current.DatabaseName);
                AddNVarChar(audit, "@executionIdentity", 256, context.ExecutionIdentity);
                AddNVarChar(audit, "@databaseHost", 256, context.DatabaseHost);
                AddNVarChar(audit, "@reason", 512, request.Reason.Trim());
                AddNVarChar(audit, "@statusBefore", 32, StoragePressureStatuses.Paused);
                AddNVarChar(audit, "@statusAfter", 32, StoragePressureStatuses.Healthy);
                AddDateTimeOffset(audit, "@recoveredAt", recoveredAt);
                AddNVarChar(audit, "@volumeRoot", 512, sample.VolumeRoot);
                audit.Parameters.Add("@totalBytes", SqlDbType.BigInt).Value = sample.TotalBytes;
                audit.Parameters.Add("@availableBytes", SqlDbType.BigInt).Value = sample.AvailableBytes;
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE mesingest.StoragePressureState
                    SET StoragePressureStatus = N'HEALTHY',
                        TotalBytes = @totalBytes,
                        AvailableBytes = @availableBytes,
                        ObservedAt = @observedAt,
                        RecoveryAuditId = @auditId
                    WHERE Id = 1 AND PauseId = @pauseId;
                    """;
                update.Parameters.Add("@totalBytes", SqlDbType.BigInt).Value = sample.TotalBytes;
                update.Parameters.Add("@availableBytes", SqlDbType.BigInt).Value = sample.AvailableBytes;
                AddDateTimeOffset(update, "@observedAt", recoveredAt);
                AddNVarChar(update, "@auditId", 64, auditId);
                AddNVarChar(update, "@pauseId", 64, current.PauseId!);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = await ReadStoragePressureStateAsync(
                connection,
                transaction,
                forUpdate: false,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateObservation(
        ResolvedDatabaseVolume databaseVolume,
        VolumeSpaceSample sample)
    {
        ArgumentNullException.ThrowIfNull(databaseVolume);
        ArgumentNullException.ThrowIfNull(sample);
        sample.Validate();
        if (!string.Equals(
                databaseVolume.VolumeRoot,
                sample.VolumeRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The volume-space sample does not belong to the resolved database volume.");
        }
    }

    private static string JoinDatabasePaths(ResolvedDatabaseVolume volume)
    {
        var value = string.Join('|', volume.DatabaseFilePaths);
        return value.Length <= 1024
            ? value
            : throw new InvalidDataException("The resolved database file identity is too long.");
    }

    private static async Task<StoragePressureStateSnapshot> ReadStoragePressureStateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT s.StoragePressureStatus, s.HistoryEpoch, s.DatabaseName,
                s.DatabaseFilePath, s.VolumeRoot, s.TotalBytes, s.AvailableBytes,
                s.ObservedAt, s.PausedAt, s.PauseId, s.PauseReason, s.RecoveryAuditId,
                CASE WHEN s.PauseId IS NOT NULL AND NOT EXISTS
                (
                    SELECT 1 FROM mesingest.LocalAdministrationAudits AS audit
                    WHERE audit.AuditId = s.RecoveryAuditId
                      AND audit.Operation = N'STORAGE_PRESSURE_RECOVERY'
                      AND audit.OperationTargetId = s.PauseId
                      AND audit.HistoryEpoch = s.HistoryEpoch
                      AND audit.DatabaseName = s.DatabaseName
                      AND audit.StatusBefore = N'STORAGE_PRESSURE_PAUSE'
                      AND audit.StatusAfter = N'HEALTHY'
                ) THEN 1 ELSE 0 END AS HasUnrecoveredPause
            FROM mesingest.StoragePressureState AS s{(forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty)}
            WHERE s.Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("StoragePressureState singleton is missing.");
        }

        var status = reader.GetInt32(12) != 0
            ? StoragePressureStatuses.Paused
            : reader.GetString(0);
        var epoch = HistoryEpoch.FromGuid(reader.GetGuid(1));
        var observedAt = reader.IsDBNull(7)
            ? DateTimeOffset.MinValue
            : reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime();
        return new StoragePressureStateSnapshot(
            status,
            epoch,
            reader.GetString(2),
            reader.GetString(3),
            new VolumeSpaceSample(reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6)),
            observedAt,
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8).ToUniversalTime(),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static async Task<bool> IsDatabaseHealthyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CONVERT(NVARCHAR(60), DATABASEPROPERTYEX(DB_NAME(), 'Status')),
                   CONVERT(NVARCHAR(60), DATABASEPROPERTYEX(DB_NAME(), 'Updateability'));
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && string.Equals(reader.GetString(0), "ONLINE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(reader.GetString(1), "READ_WRITE", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed class SqlServerLocalAdministrationContextProvider :
    ILocalAdministrationContextProvider
{
    public async Task<LocalAdministrationContext> GetContextAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ORIGINAL_LOGIN(), CONVERT(NVARCHAR(256), SERVERPROPERTY('MachineName')),
                   ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0), ISNULL(IS_MEMBER(N'db_owner'), 0);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Could not resolve the local administration identity.");
        }

        var databaseHost = reader.GetString(1);
        return new LocalAdministrationContext(
            reader.GetString(0),
            databaseHost,
            string.Equals(databaseHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            builder.IntegratedSecurity && (reader.GetInt32(2) == 1 || reader.GetInt32(3) == 1));
    }
}
