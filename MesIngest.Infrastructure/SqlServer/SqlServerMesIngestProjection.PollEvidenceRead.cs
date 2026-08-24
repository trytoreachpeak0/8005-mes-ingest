using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    public async Task<HistoricalObjectReadResult<PollTraceSnapshot>> GetPollTraceAsync(
        string pollTraceId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(pollTraceId, nameof(pollTraceId), 128);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);

        async Task<HistoricalObjectReadResult<PollTraceSnapshot>> CompleteUnavailableAsync(
            HistoricalObjectAvailability availability,
            HistoricalReadBoundary boundary)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(availability, boundary, Value: null);
        }

        try
        {
            var boundary = await ReadHistoricalReadBoundaryAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var trace = await ReadExactPollTraceAsync(
                connection,
                transaction,
                pollTraceId,
                cancellationToken).ConfigureAwait(false);
            if (trace is null)
            {
                return await CompleteUnavailableAsync(
                    HistoricalObjectAvailability.NotFound,
                    boundary).ConfigureAwait(false);
            }

            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.PollEvidence,
                new ProjectionReadFence(
                    boundary.HistoryEpoch,
                    trace.ProjectionCommitId,
                    trace.ProjectionSequence),
                cancellationToken).ConfigureAwait(false);

            if (boundary.EarliestAvailableHostUtc is { } earliest
                && trace.CompletedAt < earliest)
            {
                return await CompleteUnavailableAsync(
                    HistoricalObjectAvailability.Expired,
                    boundary).ConfigureAwait(false);
            }

            var requiresRawMultiset = string.Equals(
                trace.Outcome,
                SuccessOutcome,
                StringComparison.Ordinal)
                && trace.RowCount > 0;
            var observations = !requiresRawMultiset
                ? []
                : await ReadExactPollTraceObservationsAsync(
                    connection,
                    transaction,
                    trace,
                    cancellationToken).ConfigureAwait(false);
            if (requiresRawMultiset && observations.Count != trace.RowCount)
            {
                return await CompleteUnavailableAsync(
                    HistoricalObjectAvailability.Expired,
                    boundary).ConfigureAwait(false);
            }

            var protectionDecisions = trace.ProjectionCommitId is null
                ? []
                : await ReadTaskTypeProtectionDecisionsAsync(
                    connection,
                    transaction,
                    trace.ProjectionCommitId,
                    cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new PollTraceSnapshot(
                trace.PollTraceId,
                trace.QueryVersion,
                trace.Outcome,
                trace.StartedAt,
                trace.CompletedAt,
                trace.RowCount,
                trace.ContentDigest,
                trace.ProjectionCommitId is null
                    ? null
                    : new ProjectionCommitSnapshot(
                        trace.ProjectionCommitId,
                        trace.PollTraceId,
                        trace.CommittedAt!.Value,
                        trace.HostSessionId!,
                        trace.RestartPhaseBefore!,
                        trace.RestartPhaseAfter!,
                        trace.AbsenceAuthority!.Value,
                        protectionDecisions,
                        trace.ProjectionSequence!.Value),
                observations,
                trace.Diagnostic);
            return new(
                HistoricalObjectAvailability.Available,
                boundary,
                snapshot);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PollTraceRow?> ReadExactPollTraceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                p.PollTraceId,
                p.QueryVersion,
                p.Outcome,
                p.StartedAt,
                p.CompletedAt,
                p.[RowCount],
                p.ContentDigest,
                p.DiagnosticStage,
                p.DiagnosticCode,
                p.DiagnosticSafeDetail,
                c.ProjectionCommitId,
                c.ProjectionSequence,
                c.CommittedAt,
                c.HostSessionId,
                c.RestartPhaseBefore,
                c.RestartPhaseAfter,
                c.AbsenceAuthority
            FROM mesingest.PollTraces AS p
            LEFT JOIN mesingest.ProjectionCommits AS c
                ON c.PollTraceId = p.PollTraceId
            WHERE p.PollTraceId = @pollTraceId;
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPollTraceRow(reader)
            : null;
    }

    private static async Task<IReadOnlyList<DemandRawObservationSnapshot>>
        ReadExactPollTraceObservationsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            PollTraceRow trace,
            CancellationToken cancellationToken)
    {
        if (trace.ProjectionCommitId is null)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                Ordinal,
                PollTraceId,
                ProjectionCommitId,
                SeriesId,
                DemandId,
                WorkType,
                Sublot,
                Area,
                Eqp,
                Step,
                MesSourceDate,
                Package,
                MesSourceDateRaw
            FROM mesingest.DemandRawObservations
            WHERE PollTraceId = @pollTraceId
              AND ProjectionCommitId = @projectionCommitId
            ORDER BY Ordinal;
            """;
        AddNVarChar(command, "@pollTraceId", 128, trace.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, trace.ProjectionCommitId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var observations = new List<DemandRawObservationSnapshot>(trace.RowCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observations.Add(new DemandRawObservationSnapshot(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) && reader.IsDBNull(4)
                    ? MesObservationAssignment.Unassigned
                    : MesObservationAssignment.Assigned,
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableDateTimeOffset(reader, 10),
                GetNullableString(reader, 11),
                trace.CompletedAt,
                GetNullableString(reader, 12)));
        }

        return observations;
    }
}
