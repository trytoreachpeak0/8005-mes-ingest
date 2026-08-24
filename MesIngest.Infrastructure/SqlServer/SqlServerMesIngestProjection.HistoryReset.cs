using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection :
    IHistoryResetOperations,
    IMesIngestLocalAdministration
{
    public async Task<HistoryResetStateSnapshot> ReadHistoryResetStateAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHistoryResetStateAsync(
            connection,
            transaction: null,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HistoryResetStateSnapshot> AcknowledgeHistoryResetAsync(
        HistoryResetAcknowledgementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabaseName);
        ArgumentNullException.ThrowIfNull(request.HistoryEpoch);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 512)
        {
            throw new ArgumentException(
                "An acknowledgement reason of at most 512 characters is required.",
                nameof(request));
        }

        var context = await _localAdministrationContextProvider
            .GetContextAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadHistoryResetStateAsync(
                connection,
                transaction,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
            HistoryResetAcknowledgementPolicy.Validate(context, current, request);
            if (!current.RequiresAcknowledgement)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var acknowledgedAt = _timeProvider.GetUtcNow().ToUniversalTime();
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
                        (@auditId, N'HISTORY_RESET_ACKNOWLEDGEMENT', @operationTargetId,
                         @historyEpoch, @databaseName, @executionIdentity, @databaseHost,
                         @reason, @riskAcceptance, @statusBefore, @statusAfter,
                         @occurredAt, NULL, NULL, NULL);
                    """;
                AddNVarChar(audit, "@auditId", 64, auditId);
                AddNVarChar(
                    audit,
                    "@operationTargetId",
                    128,
                    current.HistoryEpoch.Value.ToString("D"));
                audit.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value =
                    current.HistoryEpoch.Value;
                AddNVarChar(audit, "@databaseName", 128, current.DatabaseName);
                AddNVarChar(audit, "@executionIdentity", 256, context.ExecutionIdentity);
                AddNVarChar(audit, "@databaseHost", 256, context.DatabaseHost);
                AddNVarChar(audit, "@reason", 512, request.Reason.Trim());
                AddNVarChar(audit, "@riskAcceptance", 128, request.RiskAcceptance);
                AddNVarChar(
                    audit,
                    "@statusBefore",
                    32,
                    HistoryResetStatuses.AcknowledgementRequired);
                AddNVarChar(
                    audit,
                    "@statusAfter",
                    32,
                    HistoryResetStatuses.Acknowledged);
                AddDateTimeOffset(audit, "@occurredAt", acknowledgedAt);
                await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE mesingest.SchemaInfo
                    SET HistoryResetStatus = N'ACKNOWLEDGED',
                        HistoryResetAcknowledgementAuditId = @auditId
                    WHERE Id = 1
                      AND HistoryEpoch = @historyEpoch
                      AND HistoryResetRequiredAt IS NOT NULL;
                    """;
                AddNVarChar(update, "@auditId", 64, auditId);
                update.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value =
                    current.HistoryEpoch.Value;
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The HistoryReset state changed while it was being acknowledged.");
                }
            }

            var result = await ReadHistoryResetStateAsync(
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

    private static async Task<HistoryResetStateSnapshot> ReadHistoryResetStateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                CASE
                    WHEN info.HistoryResetRequiredAt IS NULL THEN N'NOT_REQUIRED'
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM mesingest.LocalAdministrationAudits AS audit
                        WHERE audit.AuditId = info.HistoryResetAcknowledgementAuditId
                          AND audit.Operation = N'HISTORY_RESET_ACKNOWLEDGEMENT'
                          AND TRY_CONVERT(UNIQUEIDENTIFIER, audit.OperationTargetId) =
                              info.HistoryEpoch
                          AND audit.HistoryEpoch = info.HistoryEpoch
                          AND audit.DatabaseName = DB_NAME()
                          AND audit.RiskAcceptance =
                              N'I_ACCEPT_UNRECOVERABLE_LOSS_OF_PRIOR_HISTORY_AND_TOMBSTONES'
                          AND audit.StatusBefore = N'ACKNOWLEDGEMENT_REQUIRED'
                          AND audit.StatusAfter = N'ACKNOWLEDGED'
                    ) THEN N'ACKNOWLEDGED'
                    ELSE N'ACKNOWLEDGEMENT_REQUIRED'
                END,
                info.HistoryEpoch,
                DB_NAME(),
                info.HistoryEpochEstablishedAt,
                info.HistoryResetAcknowledgementAuditId,
                info.HistoryResetRequiredAt
            FROM mesingest.SchemaInfo AS info{(forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty)}
            WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The HistoryEpoch singleton is missing.");
        }

        return new HistoryResetStateSnapshot(
            reader.GetString(0),
            HistoryEpoch.FromGuid(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime());
    }
}
