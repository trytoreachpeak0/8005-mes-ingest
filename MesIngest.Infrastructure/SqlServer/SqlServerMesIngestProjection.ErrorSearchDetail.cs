using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private const int ErrorSearchScalarMaximumBytes = 512;
    private const string RedactedMarker = "[REDACTED]";

    private static readonly Regex ErrorSearchBearerCredentialPattern = new(
        "\\b(?:authorization\\s*(?:=|:|\\s)\\s*)?bearer\\s+(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ErrorSearchSecretPattern = new(
        "\\b(password|pwd|sharedsecret|token|authorization|bearer)\\b\\s*(?:=|:|\\s)\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<ErrorSearchDetailSnapshot?> GetErrorSearchDetailAsync(
        string seriesId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(seriesId, nameof(seriesId), 64);
        ValidateRequiredText(snapshotReference, nameof(snapshotReference), 4096);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await ResolveReferencedErrorSearchSnapshotAsync(
                connection,
                transaction,
                snapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            var series = await ReadErrorSearchSeriesMatchAsync(
                connection,
                transaction,
                snapshot,
                seriesId.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (series is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var periods = await ReadErrorSearchDetailPeriodsAsync(
                connection,
                transaction,
                snapshot,
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ErrorSearchDetailSnapshot(
                snapshotReference,
                snapshot.Snapshot,
                snapshot.Filter,
                snapshot.Window,
                snapshot.Order,
                ToErrorSearchListItem(series),
                periods);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ErrorSearchRawEvidenceSnapshot?> GetErrorSearchRawEvidenceAsync(
        string seriesId,
        string evidenceId,
        string snapshotReference,
        ErrorSearchRawEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(seriesId, nameof(seriesId), 64);
        ValidateRequiredText(evidenceId, nameof(evidenceId), 64);
        ValidateRequiredText(snapshotReference, nameof(snapshotReference), 4096);
        ArgumentNullException.ThrowIfNull(query);
        query = query.NormalizeAndValidate();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await ResolveReferencedErrorSearchSnapshotAsync(
                connection,
                transaction,
                snapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            var series = await ReadErrorSearchSeriesMatchAsync(
                connection,
                transaction,
                snapshot,
                seriesId.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (series is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var matchedEvidence = await ReadExactErrorSearchEvidenceAsync(
                connection,
                transaction,
                snapshot,
                series.SeriesId,
                evidenceId.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (matchedEvidence is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var items = await ReadBoundedRawEvidenceItemsAsync(
                connection,
                transaction,
                matchedEvidence.Evidence,
                query,
                cancellationToken).ConfigureAwait(false);
            var limits = new ErrorSearchRawEvidenceLimitsSnapshot(
                query.MaxItems,
                ErrorSearchRawEvidenceLimits.MaximumItemBytes,
                ErrorSearchRawEvidenceLimits.MaximumTotalBytes);
            var result = new ErrorSearchRawEvidenceSnapshot(
                snapshotReference,
                snapshot.Snapshot,
                series.SeriesId,
                matchedEvidence.PeriodId,
                matchedEvidence.Evidence.EvidenceId,
                matchedEvidence.Evidence.PollTraceId,
                matchedEvidence.Evidence.ProjectionCommitId,
                matchedEvidence.Evidence.DemandId,
                query.Fields,
                items.Count,
                limits,
                0,
                items);
            result = SetRawEvidencePayloadBytes(result);
            if (result.PayloadBytes > ErrorSearchRawEvidenceLimits.MaximumTotalBytes)
            {
                throw RawLimitExceeded();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<RawEvidenceMatch?> ReadExactErrorSearchEvidenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ErrorSearchSnapshotReference snapshot,
        string seriesId,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        var filter = snapshot.Filter.Normalize();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                period.PeriodId,
                evidence.EvidenceId,
                evidence.EvidenceKind,
                period.SubjectKind,
                evidence.ObservedAt,
                evidence.PollTraceId,
                evidence.ProjectionCommitId,
                evidence.DemandId,
                demandSeries.WorkType,
                evidence.ObservedValue,
                evidence.ExpectedRule
            FROM mesingest.SeriesErrorPeriodEvidence AS evidence
            INNER JOIN mesingest.DemandSeriesErrorPeriods AS period
                ON period.PeriodId = evidence.PeriodId
            INNER JOIN mesingest.DemandSeriesEvents AS opened
                ON opened.EventId = period.OpenedEventId
            INNER JOIN mesingest.ProjectionCommits AS openedCommit
                ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
            LEFT JOIN mesingest.DemandSeriesEvents AS closed
                ON closed.EventId = period.ClosedEventId
            LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = evidence.DemandId
            INNER JOIN mesingest.DemandSeries AS demandSeries
                ON demandSeries.SeriesId = demand.SeriesId
            WHERE evidence.EvidenceId = @evidenceId
              AND period.SeriesId = @detailSeriesId COLLATE Latin1_General_100_CI_AS
              AND openedCommit.ProjectionSequence <= @snapshotSequence
              AND evidenceCommit.ProjectionSequence <= @snapshotSequence
              AND period.StartedAt <= @asOf
              AND evidence.ObservedAt <= @asOf
              AND period.StartedAt < @windowTo
              AND (@windowFrom IS NULL OR COALESCE(
                    CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                           AND period.EndedAt <= @asOf THEN period.EndedAt END,
                    @asOf) > @windowFrom)
              AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@categoriesJson))
                   OR period.Category IN (SELECT [value] COLLATE Latin1_General_100_BIN2
                                          FROM OPENJSON(@categoriesJson)))
              AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@errorCodesJson))
                   OR period.ErrorCode IN (SELECT [value] COLLATE Latin1_General_100_BIN2
                                           FROM OPENJSON(@errorCodesJson)))
              AND (@demandId IS NULL
                   OR evidence.DemandId = @demandId COLLATE Latin1_General_100_CI_AS);
            """;
        AddNVarChar(command, "@evidenceId", 64, evidenceId);
        AddNVarChar(command, "@detailSeriesId", 64, seriesId);
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value =
            snapshot.Snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@asOf", snapshot.Snapshot.ErrorSearchAsOf);
        AddNullableDateTimeOffset(command, "@windowFrom", snapshot.Window.FromUtc);
        AddDateTimeOffset(command, "@windowTo", snapshot.Window.ToUtc);
        AddNVarChar(command, "@categoriesJson", -1, JsonSerializer.Serialize(filter.Categories));
        AddNVarChar(command, "@errorCodesJson", -1, JsonSerializer.Serialize(filter.ErrorCodes));
        AddNullableNVarChar(command, "@demandId", 64, filter.DemandId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RawEvidenceMatch(
            reader.GetString(0),
            CreateDetailEvidence(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                GetNullableString(reader, 9),
                reader.GetString(10),
                rawEvidenceAvailable: true));
    }

    private async Task<ErrorSearchSnapshotReference> ResolveReferencedErrorSearchSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string snapshotReference,
        byte[] signingKey,
        CancellationToken cancellationToken)
    {
        if (!ErrorSearchTokenCodec.TryReadSnapshotReference(
                snapshotReference,
                signingKey,
                out var requested,
                out var tokenError))
        {
            throw new ErrorSearchException(tokenError!.Code, tokenError.Message);
        }

        var window = string.Equals(
            requested!.Window.Kind,
            ErrorSearchWindowKinds.Custom,
            StringComparison.Ordinal)
            ? ErrorSearchWindowSelection.Custom(requested.Window.FromUtc, requested.Window.ToUtc)
            : new ErrorSearchWindowSelection(requested.Window.Kind);
        var query = new ErrorSearchQuery(
            requested.Filter,
            window,
            SnapshotReference: snapshotReference,
            Order: requested.Order).NormalizeAndValidate();
        return await ResolveErrorSearchSnapshotAsync(
            connection,
            transaction,
            query,
            signingKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ErrorSearchSeriesRow?> ReadErrorSearchSeriesMatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ErrorSearchSnapshotReference snapshot,
        string seriesId,
        CancellationToken cancellationToken)
    {
        if (snapshot.Filter.SeriesId is not null
            && !string.Equals(snapshot.Filter.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var narrowed = snapshot with
        {
            Filter = snapshot.Filter with { SeriesId = seriesId.ToUpperInvariant() },
        };
        var page = await ReadErrorSearchPageAsync(
            connection,
            transaction,
            narrowed,
            1,
            null,
            cancellationToken).ConfigureAwait(false);
        return page.Rows.SingleOrDefault(row =>
            string.Equals(row.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase));
    }

    private static ErrorSearchListItemSnapshot ToErrorSearchListItem(ErrorSearchSeriesRow row) =>
        new(
            row.SeriesId,
            row.WorkType,
            row.Sublot,
            row.ActivityRank == 0 ? ErrorSearchActivityStates.Active : ErrorSearchActivityStates.Ended,
            row.MatchedErrors,
            row.LatestMatchedEvidenceAt,
            row.MatchedPeriodCount,
            row.MatchedDemandGenerationCount,
            row.MesArea,
            row.MesAreaAvailability);

    private static async Task<IReadOnlyList<ErrorSearchDetailPeriodSnapshot>>
        ReadErrorSearchDetailPeriodsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            ErrorSearchSnapshotReference snapshot,
            string seriesId,
            CancellationToken cancellationToken)
    {
        var filter = snapshot.Filter.Normalize();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE #DetailPeriods
            (
                PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                ErrorCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Category NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Severity NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Target NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SubjectKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                StartReason NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                StartedAt DATETIMEOFFSET(7) NOT NULL,
                EndedAt DATETIMEOFFSET(7) NULL,
                EndReason NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL
            );

            INSERT INTO #DetailPeriods
                (PeriodId, ErrorCode, Category, Severity, Target, SubjectKind,
                 StartReason, StartedAt, EndedAt, EndReason)
            SELECT period.PeriodId, period.ErrorCode, period.Category, period.Severity,
                period.Target, period.SubjectKind, period.StartReason, period.StartedAt,
                CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                       AND period.EndedAt <= @asOf THEN period.EndedAt END,
                CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                       AND period.EndedAt <= @asOf THEN period.EndReason END
            FROM mesingest.DemandSeriesErrorPeriods AS period
            INNER JOIN mesingest.DemandSeriesEvents AS opened
                ON opened.EventId = period.OpenedEventId
            INNER JOIN mesingest.ProjectionCommits AS openedCommit
                ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
            LEFT JOIN mesingest.DemandSeriesEvents AS closed
                ON closed.EventId = period.ClosedEventId
            LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
            WHERE period.SeriesId = @detailSeriesId COLLATE Latin1_General_100_CI_AS
              AND openedCommit.ProjectionSequence <= @snapshotSequence
              AND period.StartedAt <= @asOf
              AND period.StartedAt < @windowTo
              AND (@windowFrom IS NULL OR COALESCE(
                    CASE WHEN closedCommit.ProjectionSequence <= @snapshotSequence
                           AND period.EndedAt <= @asOf THEN period.EndedAt END,
                    @asOf) > @windowFrom)
              AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@categoriesJson))
                   OR period.Category IN (SELECT [value] COLLATE Latin1_General_100_BIN2
                                          FROM OPENJSON(@categoriesJson)))
              AND (NOT EXISTS (SELECT 1 FROM OPENJSON(@errorCodesJson))
                   OR period.ErrorCode IN (SELECT [value] COLLATE Latin1_General_100_BIN2
                                           FROM OPENJSON(@errorCodesJson)));

            CREATE TABLE #DetailEvidence
            (
                EvidenceId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
            );
            INSERT INTO #DetailEvidence (EvidenceId, PeriodId)
            SELECT evidence.EvidenceId, evidence.PeriodId
            FROM mesingest.SeriesErrorPeriodEvidence AS evidence
            INNER JOIN #DetailPeriods AS period ON period.PeriodId = evidence.PeriodId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            WHERE evidenceCommit.ProjectionSequence <= @snapshotSequence
              AND evidence.ObservedAt <= @asOf
              AND (@demandId IS NULL
                   OR evidence.DemandId = @demandId COLLATE Latin1_General_100_CI_AS);

            DELETE period FROM #DetailPeriods AS period
            WHERE NOT EXISTS (SELECT 1 FROM #DetailEvidence AS evidence
                              WHERE evidence.PeriodId = period.PeriodId);

            SELECT PeriodId, ErrorCode, Category, Severity, Target, SubjectKind,
                StartReason, StartedAt, EndedAt, EndReason
            FROM #DetailPeriods
            ORDER BY StartedAt, PeriodId;

            SELECT evidence.PeriodId, evidence.EvidenceId, evidence.EvidenceKind,
                period.SubjectKind, evidence.ObservedAt, evidence.PollTraceId,
                evidence.ProjectionCommitId, evidence.DemandId, demandSeries.WorkType,
                evidence.ObservedValue, evidence.ExpectedRule,
                CONVERT(BIT, CASE WHEN EXISTS
                (
                    SELECT 1 FROM mesingest.DemandRawObservations AS raw
                    WHERE raw.PollTraceId = evidence.PollTraceId
                      AND raw.ProjectionCommitId = evidence.ProjectionCommitId
                      AND raw.DemandId = evidence.DemandId
                ) THEN 1 ELSE 0 END) AS RawEvidenceAvailable
            FROM #DetailEvidence AS eligible
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.EvidenceId = eligible.EvidenceId
            INNER JOIN #DetailPeriods AS period ON period.PeriodId = evidence.PeriodId
            INNER JOIN mesingest.DemandSeriesEvents AS eventRow
                ON eventRow.EventId = evidence.EventId
            INNER JOIN mesingest.TransportDemands AS demand
                ON demand.DemandId = evidence.DemandId
            INNER JOIN mesingest.DemandSeries AS demandSeries
                ON demandSeries.SeriesId = demand.SeriesId
            ORDER BY period.StartedAt, period.PeriodId, eventRow.SeriesSequence, evidence.EvidenceId;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value =
            snapshot.Snapshot.ProjectionSequence;
        AddDateTimeOffset(command, "@asOf", snapshot.Snapshot.ErrorSearchAsOf);
        AddNullableDateTimeOffset(command, "@windowFrom", snapshot.Window.FromUtc);
        AddDateTimeOffset(command, "@windowTo", snapshot.Window.ToUtc);
        AddNVarChar(command, "@categoriesJson", -1, JsonSerializer.Serialize(filter.Categories));
        AddNVarChar(command, "@errorCodesJson", -1, JsonSerializer.Serialize(filter.ErrorCodes));
        AddNullableNVarChar(command, "@demandId", 64, filter.DemandId);
        AddNVarChar(command, "@detailSeriesId", 64, seriesId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<MutableErrorSearchDetailPeriod>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MutableErrorSearchDetailPeriod(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime(),
                GetNullableDateTimeOffset(reader, 8), GetNullableString(reader, 9)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Error Search detail evidence result is missing.");
        }
        var byPeriod = rows.ToDictionary(row => row.PeriodId, StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var period = byPeriod[reader.GetString(0)];
            period.Evidence.Add(CreateDetailEvidence(
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                GetNullableString(reader, 9), reader.GetString(10), reader.GetBoolean(11)));
        }

        return rows.Select(row => row.Freeze(snapshot.Window, snapshot.Snapshot.ErrorSearchAsOf))
            .ToArray();
    }

    private static ErrorSearchDetailEvidenceSnapshot CreateDetailEvidence(
        string evidenceId,
        string evidenceKind,
        string subjectKind,
        DateTimeOffset observedAt,
        string pollTraceId,
        string projectionCommitId,
        string demandId,
        string relatedWorkType,
        string? observedValue,
        string expectedRule,
        bool rawEvidenceAvailable)
    {
        IReadOnlyList<string> workTypes = [relatedWorkType];
        ErrorSearchDiagnosticValueSnapshot diagnostic;
        if (string.Equals(subjectKind, ErrorSearchDiagnosticValueKinds.RawObservationSet, StringComparison.Ordinal))
        {
            var count = ReadJsonArrayLength(observedValue);
            var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(observedValue ?? "[]"))).ToLowerInvariant();
            diagnostic = new ErrorSearchDiagnosticValueSnapshot(
                ErrorSearchDiagnosticValueKinds.RawObservationSet,
                ObservationCount: count,
                Sha256Digest: digest);
        }
        else if (string.Equals(
            subjectKind,
            ErrorSearchDiagnosticValueKinds.WorkTypeMembership,
            StringComparison.Ordinal))
        {
            workTypes = ReadWorkTypes(observedValue);
            diagnostic = new ErrorSearchDiagnosticValueSnapshot(
                ErrorSearchDiagnosticValueKinds.WorkTypeMembership);
        }
        else
        {
            diagnostic = new ErrorSearchDiagnosticValueSnapshot(
                ErrorSearchDiagnosticValueKinds.Scalar,
                BoundAndRedact(observedValue, ErrorSearchScalarMaximumBytes));
        }

        return new ErrorSearchDetailEvidenceSnapshot(
            evidenceId,
            evidenceKind,
            subjectKind,
            observedAt,
            pollTraceId,
            projectionCommitId,
            demandId,
            workTypes,
            diagnostic,
            expectedRule,
            rawEvidenceAvailable);
    }

    private static async Task<IReadOnlyList<ErrorSearchRawEvidenceItemSnapshot>>
        ReadBoundedRawEvidenceItemsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            ErrorSearchDetailEvidenceSnapshot evidence,
            ErrorSearchRawEvidenceQuery query,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TOP (@take) observation.Ordinal, observation.PollTraceId,
                observation.ProjectionCommitId, observation.DemandId,
                poll.CompletedAt, observation.WorkType, observation.Sublot,
                observation.Area, observation.Eqp, observation.Step,
                observation.MesSourceDate, observation.Package
            FROM mesingest.DemandRawObservations AS observation
            INNER JOIN mesingest.PollTraces AS poll
                ON poll.PollTraceId = observation.PollTraceId
            WHERE observation.PollTraceId = @pollTraceId
              AND observation.ProjectionCommitId = @projectionCommitId
              AND observation.DemandId = @demandId
            ORDER BY observation.Ordinal;
            """;
        command.Parameters.Add("@take", SqlDbType.Int).Value = checked(query.MaxItems + 1);
        AddNVarChar(command, "@pollTraceId", 128, evidence.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, evidence.ProjectionCommitId);
        AddNVarChar(command, "@demandId", 64, evidence.DemandId);

        var items = new List<ErrorSearchRawEvidenceItemSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count == query.MaxItems)
            {
                throw RawLimitExceeded();
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ErrorSearchRawEvidenceFields.WorkType] = GetNullableString(reader, 5),
                [ErrorSearchRawEvidenceFields.Sublot] = GetNullableString(reader, 6),
                [ErrorSearchRawEvidenceFields.Area] = GetNullableString(reader, 7),
                [ErrorSearchRawEvidenceFields.Eqp] = GetNullableString(reader, 8),
                [ErrorSearchRawEvidenceFields.Step] = GetNullableString(reader, 9),
                [ErrorSearchRawEvidenceFields.MesSourceDate] = GetNullableDateTimeOffset(reader, 10)
                    ?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                [ErrorSearchRawEvidenceFields.Package] = GetNullableString(reader, 11),
            };
            var selected = query.Fields.ToDictionary(
                field => field,
                field => BoundAndRedact(values[field], ErrorSearchRawEvidenceLimits.MaximumItemBytes),
                StringComparer.Ordinal);
            var item = new ErrorSearchRawEvidenceItemSnapshot(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(), selected);
            if (JsonSerializer.SerializeToUtf8Bytes(item).Length
                > ErrorSearchRawEvidenceLimits.MaximumItemBytes)
            {
                throw RawLimitExceeded();
            }
            items.Add(item);
        }
        return items;
    }

    private static ErrorSearchRawEvidenceSnapshot SetRawEvidencePayloadBytes(
        ErrorSearchRawEvidenceSnapshot snapshot)
    {
        var current = snapshot;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(current).Length;
            if (bytes == current.PayloadBytes)
            {
                return current;
            }
            current = current with { PayloadBytes = bytes };
        }
        return current with { PayloadBytes = JsonSerializer.SerializeToUtf8Bytes(current).Length };
    }

    private static int ReadJsonArrayLength(string? value)
    {
        using var document = JsonDocument.Parse(value ?? "[]");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Stored Error Search diagnostic evidence is not an array.");
        }
        return document.RootElement.GetArrayLength();
    }

    private static IReadOnlyList<string> ReadWorkTypes(string? value)
    {
        using var document = JsonDocument.Parse(value ?? "[]");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Stored WorkType membership evidence is not an array.");
        }
        return document.RootElement.EnumerateArray()
            .Select(item => BoundAndRedact(item.GetString(), ErrorSearchScalarMaximumBytes) ?? string.Empty)
            .ToArray();
    }

    private static string? BoundAndRedact(string? value, int maximumUtf8Bytes)
    {
        if (value is null)
        {
            return null;
        }
        var redacted = ErrorSearchBearerCredentialPattern.Replace(value, RedactedMarker);
        redacted = ErrorSearchSecretPattern.Replace(redacted, match =>
            $"{match.Groups[1].Value}={RedactedMarker}");
        if (Encoding.UTF8.GetByteCount(redacted) <= maximumUtf8Bytes)
        {
            return redacted;
        }

        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in redacted.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maximumUtf8Bytes)
            {
                break;
            }
            result.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private static ErrorSearchException RawLimitExceeded() => new(
        ErrorSearchErrorCodes.RawLimitExceeded,
        "The raw evidence exceeds the bounded response limits.");

    private sealed record RawEvidenceMatch(
        string PeriodId,
        ErrorSearchDetailEvidenceSnapshot Evidence);

    private sealed class MutableErrorSearchDetailPeriod(
        string periodId,
        string code,
        string category,
        string severity,
        string target,
        string subjectKind,
        string startReason,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        string? endReason)
    {
        public string PeriodId { get; } = periodId;

        public List<ErrorSearchDetailEvidenceSnapshot> Evidence { get; } = [];

        public ErrorSearchDetailPeriodSnapshot Freeze(
            ErrorSearchResolvedWindow window,
            DateTimeOffset asOf) => new(
                PeriodId,
                code,
                category,
                severity,
                target,
                subjectKind,
                startReason,
                startedAt,
                endedAt,
                endReason,
                window.FromUtc is not null && startedAt < window.FromUtc.Value,
                (endedAt ?? asOf) > window.ToUtc,
                endedAt is null || endedAt > asOf,
                Evidence.ToArray());
    }
}
