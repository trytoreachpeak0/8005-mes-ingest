using System.Data;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

public sealed partial class SqlServerMesIngestProjection
{
    private static readonly string ReadabilityBlockerCatalogJson = JsonSerializer.Serialize(
        ReadabilityBlockerCatalog.Definitions.Select(definition => new
        {
            definition.Code,
            definition.Priority,
        }));

    public async Task<ReadabilityAuditListSnapshot> ListReadabilityAuditAsync(
        ReadabilityAuditQuery query,
        CancellationToken cancellationToken = default)
    {
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
            if (query.SnapshotReference is null)
            {
                await AcquireCommitRoundReadFenceLockAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            var signingKey = await ReadSnapshotTokenSigningKeyAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var resolvedSnapshot = await ResolveReadabilityAuditSnapshotAsync(
                connection,
                transaction,
                query.SnapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            var snapshot = resolvedSnapshot.Identity;
            var useCurrentReadModel = await IsLatestProjectionSnapshotAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            if (query.SnapshotReference is not null)
            {
                await EnsureHistoricalPollTraceAvailableAsync(
                    connection,
                    transaction,
                    snapshot.PollTraceId,
                    cancellationToken,
                    resolvedSnapshot.RawAvailabilityCutoff).ConfigureAwait(false);
                await EnsureHistoricalSnapshotRetentionLeaseAvailableAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    resolvedSnapshot.RawAvailabilityCutoff,
                    cancellationToken).ConfigureAwait(false);
            }
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.ReadabilityAudit,
                new ProjectionReadFence(
                    snapshot.HistoryEpoch,
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.CatalogRevision),
                cancellationToken).ConfigureAwait(false);

            var pageNumber = query.PageNumber;
            ReadabilityAuditCursor? cursor = null;
            if (query.Cursor is not null)
            {
                if (!ReadabilityAuditTokenCodec.TryReadCursor(
                        query.Cursor,
                        snapshot,
                        query.Filter,
                        query.Order,
                        query.PageSize,
                        signingKey,
                        out cursor,
                        out var tokenError))
                {
                    throw new ReadabilityAuditException(tokenError!.Code, tokenError.Message);
                }
                pageNumber = cursor!.TargetPageNumber;
            }

            var page = await ReadAuditPageAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                useCurrentReadModel,
                query.Filter,
                pageNumber,
                query.PageSize,
                cursor,
                resolvedSnapshot.RawAvailabilityCutoff,
                cancellationToken).ConfigureAwait(false);
            if (query.SnapshotReference is not null
                && useCurrentReadModel
                && !await IsLatestProjectionSnapshotAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    cancellationToken).ConfigureAwait(false))
            {
                page = await ReadAuditPageAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    false,
                    query.Filter,
                    pageNumber,
                    query.PageSize,
                    cursor,
                    resolvedSnapshot.RawAvailabilityCutoff,
                    cancellationToken).ConfigureAwait(false);
            }
            var exactTotal = page.ExactTotalDemandCount;
            var totalPages = exactTotal == 0
                ? 0
                : checked((int)((exactTotal + query.PageSize - 1) / query.PageSize));
            var pageItems = page.Items.Select(ToAuditListItem).ToArray();
            var hasMore = pageNumber < totalPages;
            var snapshotReference = query.SnapshotReference
                ?? ReadabilityAuditTokenCodec.CreateSnapshotReference(
                    snapshot,
                    resolvedSnapshot.RawAvailabilityCutoff,
                    signingKey);
            var nextCursor = hasMore && pageItems.Length > 0
                ? ReadabilityAuditTokenCodec.CreateCursor(
                    snapshot,
                    query.Filter,
                    query.Order,
                    query.PageSize,
                    checked(pageNumber + 1),
                    GetReadabilityRank(pageItems[^1].ExternalReadabilityState),
                    GetLeadPriority(pageItems[^1].LeadReadabilityBlocker),
                    pageItems[^1].DemandLastSeenAt,
                    pageItems[^1].DemandId,
                    signingKey)
                : null;

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ReadabilityAuditListSnapshot(
                snapshot,
                snapshotReference,
                query.Filter,
                query.Order,
                exactTotal,
                page.Facets,
                query.PageSize,
                pageNumber,
                totalPages,
                pageItems,
                nextCursor,
                hasMore);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ReadabilityAuditDetailSnapshot?> GetReadabilityAuditDetailAsync(
        string demandId,
        string snapshotReference,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(demandId, nameof(demandId), 64);
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
            var resolvedSnapshot = await ResolveReadabilityAuditSnapshotAsync(
                connection,
                transaction,
                snapshotReference,
                signingKey,
                cancellationToken).ConfigureAwait(false);
            var snapshot = resolvedSnapshot.Identity;
            await EnsureHistoricalPollTraceAvailableAsync(
                connection,
                transaction,
                snapshot.PollTraceId,
                cancellationToken,
                resolvedSnapshot.RawAvailabilityCutoff).ConfigureAwait(false);
            await EnsureHistoricalSnapshotRetentionLeaseAvailableAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                resolvedSnapshot.RawAvailabilityCutoff,
                cancellationToken).ConfigureAwait(false);
            await _readBoundaryObserver.OnFenceSelectedAsync(
                ProjectionReadSurface.ReadabilityAudit,
                new ProjectionReadFence(
                    snapshot.HistoryEpoch,
                    snapshot.ProjectionCommitId,
                    snapshot.ProjectionSequence,
                    snapshot.CatalogRevision),
                cancellationToken).ConfigureAwait(false);
            var useCurrentReadModel = await IsLatestProjectionSnapshotAsync(
                connection,
                transaction,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var states = useCurrentReadModel
                ? (await ReadCurrentAuditPageAsync(
                    connection,
                    transaction,
                    new ReadabilityAuditFilter { DemandId = demandId.Trim() },
                    pageNumber: 1,
                    pageSize: 1,
                    cancellationToken).ConfigureAwait(false)).Items
                : await ReadAuditStatesAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    demandId.Trim(),
                    cancellationToken).ConfigureAwait(false);
            if (useCurrentReadModel
                && !await IsLatestProjectionSnapshotAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    cancellationToken).ConfigureAwait(false))
            {
                states = await ReadAuditStatesAsync(
                    connection,
                    transaction,
                    snapshot.ProjectionSequence,
                    demandId.Trim(),
                    cancellationToken).ConfigureAwait(false);
            }
            var state = states
                .SingleOrDefault(candidate =>
                    string.Equals(candidate.DemandId, demandId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await EnsureHistoricalPollTraceAvailableAsync(
                connection,
                transaction,
                state.LatestObservationPollTraceId,
                cancellationToken,
                resolvedSnapshot.RawAvailabilityCutoff).ConfigureAwait(false);

            var latestRaw = await ReadLatestAuditRawObservationsAsync(
                connection,
                transaction,
                state.DemandId,
                state.LatestObservationProjectionCommitId,
                resolvedSnapshot.RawAvailabilityCutoff,
                cancellationToken).ConfigureAwait(false);
            var blockerEvidence = await ReadAuditBlockerEvidenceAsync(
                connection,
                transaction,
                state,
                snapshot.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
            var pollTrace = await ReadAuditPollTraceAsync(
                connection,
                transaction,
                state.LatestObservationPollTraceId,
                cancellationToken).ConfigureAwait(false);
            var qualificationChecks = ReadabilityQualificationCheckCatalog.Definitions
                .Select(definition => new ReadabilityQualificationCheckSnapshot(
                    definition.Code,
                    definition.BlockingCode,
                    GetQualificationCheckResult(state, definition)))
                .ToArray();
            var detail = new ReadabilityAuditDetailSnapshot(
                snapshot,
                snapshotReference,
                ToAuditListItem(state),
                new ReadabilityAuditSeriesSnapshot(
                    state.SeriesId,
                    state.WorkType,
                    state.Sublot,
                    state.SeriesLifecycle,
                    state.SeriesCurrentPresence,
                    state.SeriesStartedAt,
                    state.SeriesArchivedAt,
                    state.CurrentDemandId),
                qualificationChecks,
                blockerEvidence,
                latestRaw,
                pollTrace);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return detail;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ResolvedReadabilityAuditSnapshot> ResolveReadabilityAuditSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? snapshotReference,
        byte[] signingKey,
        CancellationToken cancellationToken)
    {
        ReadabilityAuditSnapshotIdentity? requested = null;
        var rawAvailabilityCutoff = DateTimeOffset.MinValue;
        if (snapshotReference is not null
            && !ReadabilityAuditTokenCodec.TryReadSnapshotReference(
                snapshotReference,
                signingKey,
                out requested,
                out rawAvailabilityCutoff,
                out var tokenError))
        {
            throw new ReadabilityAuditException(tokenError!.Code, tokenError.Message);
        }

        if (requested is not null && requested.HistoryEpoch != _historyEpoch)
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.SnapshotMismatch,
                "The audit snapshot reference belongs to another history epoch.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = requested is null
            ? """
              SELECT TOP (1) ProjectionCommitId, ProjectionSequence, CommittedAt,
                     PollTraceId, CatalogRevision, HistoryEpoch
              FROM mesingest.ProjectionCommits
              ORDER BY ProjectionSequence DESC;
              """
            : """
              SELECT ProjectionCommitId, ProjectionSequence, CommittedAt,
                     PollTraceId, CatalogRevision, HistoryEpoch
              FROM mesingest.ProjectionCommits
              WHERE ProjectionCommitId = @projectionCommitId
                AND ProjectionSequence = @projectionSequence
                AND HistoryEpoch = @historyEpoch;
              """;
        if (requested is not null)
        {
            AddNVarChar(command, "@projectionCommitId", 64, requested.ProjectionCommitId);
            command.Parameters.Add("@projectionSequence", SqlDbType.BigInt).Value =
                requested.ProjectionSequence;
            command.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value =
                requested.HistoryEpoch.Value;
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ReadabilityAuditException(
                requested is null
                    ? ReadabilityAuditErrorCodes.ProjectionNotAvailable
                    : ReadabilityAuditErrorCodes.SnapshotNotFound,
                requested is null
                    ? "No successful projection commit is available."
                    : "The referenced projection commit is not retained.");
        }
        var resolved = new ReadabilityAuditSnapshotIdentity(
            HistoryEpoch.FromGuid(reader.GetGuid(5)),
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetInt64(4));
        if (requested is not null
            && (!string.Equals(requested.PollTraceId, resolved.PollTraceId, StringComparison.Ordinal)
                || requested.ProjectionCommittedAt != resolved.ProjectionCommittedAt
                || requested.CatalogRevision != resolved.CatalogRevision))
        {
            throw new ReadabilityAuditException(
                ReadabilityAuditErrorCodes.SnapshotMismatch,
                "The audit snapshot metadata does not match the retained commit.");
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        if (snapshotReference is null)
        {
            rawAvailabilityCutoff = await ReadSnapshotRawAvailabilityCutoffAsync(
                connection,
                transaction,
                resolved.ProjectionSequence,
                cancellationToken).ConfigureAwait(false);
        }
        return new ResolvedReadabilityAuditSnapshot(resolved, rawAvailabilityCutoff);
    }

    private sealed record ResolvedReadabilityAuditSnapshot(
        ReadabilityAuditSnapshotIdentity Identity,
        DateTimeOffset RawAvailabilityCutoff);

    private static async Task<AuditPageState> ReadAuditPageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        bool useCurrentReadModel,
        ReadabilityAuditFilter filter,
        int pageNumber,
        int pageSize,
        ReadabilityAuditCursor? cursor,
        DateTimeOffset availabilityAtSnapshot,
        CancellationToken cancellationToken)
    {
        if (useCurrentReadModel)
        {
            return await ReadCurrentAuditPageAsync(
                connection,
                transaction,
                filter,
                pageNumber,
                pageSize,
                cancellationToken).ConfigureAwait(false);
        }

        var normalized = filter.Normalize();
        var offset = checked((long)(pageNumber - 1) * pageSize);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE #ReadabilityBlockerCatalog
            (
                Code NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Priority INT NOT NULL
            );
            INSERT INTO #ReadabilityBlockerCatalog (Code, Priority)
            SELECT catalog.Code, catalog.Priority
            FROM OPENJSON(@blockerCatalogJson)
            WITH
            (
                Code NVARCHAR(128) '$.Code',
                Priority INT '$.Priority'
            ) AS catalog;

            CREATE TABLE #AuditStates
            (
                DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Generation INT NOT NULL,
                PredecessorDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
                IsCurrentGeneration BIT NOT NULL,
                DemandCreatedAt DATETIMEOFFSET(7) NOT NULL,
                DemandLastSeenAt DATETIMEOFFSET(7) NOT NULL,
                GoneConfirmedAt DATETIMEOFFSET(7) NULL,
                LatestObservationProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                LatestObservationPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RawObservationCount BIGINT NOT NULL,
                Area NVARCHAR(512) NULL,
                Eqp NVARCHAR(512) NULL,
                Step NVARCHAR(512) NULL,
                MesSourceDate DATETIMEOFFSET(7) NULL,
                Package NVARCHAR(512) NULL,
                IsAreaTrusted BIT NOT NULL,
                SeriesStartedAt DATETIMEOFFSET(7) NULL,
                SeriesArchivedAt DATETIMEOFFSET(7) NULL,
                CurrentDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                DemandStatus NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SeriesLifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SeriesCurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ReadabilityRank INT NOT NULL DEFAULT (1),
                LeadPriority INT NULL
            );

            WITH EligibleDemands AS
            (
                SELECT d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                    d.CreatedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.SeriesId ORDER BY d.Generation DESC) AS CurrentRank
                FROM mesingest.TransportDemands AS d
                INNER JOIN mesingest.ProjectionCommits AS createdCommit
                    ON createdCommit.ProjectionCommitId = d.CreatedProjectionCommitId
                WHERE createdCommit.ProjectionSequence <= @snapshotSequence
            ),
            LatestObservations AS
            (
                SELECT d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.DemandId ORDER BY observationCommit.ProjectionSequence DESC) AS rn
                FROM EligibleDemands AS d
                INNER JOIN mesingest.DemandRawObservations AS observation
                    ON observation.DemandId = d.DemandId
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = observation.ProjectionCommitId
                INNER JOIN mesingest.PollTraces AS observationTrace
                    ON observationTrace.PollTraceId = observation.PollTraceId
                WHERE observationCommit.ProjectionSequence <= @snapshotSequence
                  AND observationTrace.CompletedAt > @availabilityAtSnapshot
                GROUP BY d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt
            ),
            LatestObservationFields AS
            (
                SELECT latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt,
                    COUNT_BIG(observation.Ordinal) AS ObservationCount,
                    MAX(observation.Area) AS Area, MAX(observation.Eqp) AS Eqp,
                    MAX(observation.Step) AS Step,
                    MAX(SWITCHOFFSET(observation.MesSourceDate, '+00:00')) AS MesSourceDate,
                    MAX(observation.Package) AS Package
                FROM LatestObservations AS latest
                INNER JOIN mesingest.DemandRawObservations AS observation
                    ON observation.DemandId = latest.DemandId
                   AND observation.ProjectionCommitId = latest.ProjectionCommitId
                WHERE latest.rn = 1
                GROUP BY latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt
            ),
            GoneState AS
            (
                SELECT eventRow.SubjectId AS DemandId, MAX(eventRow.OccurredAt) AS GoneConfirmedAt
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventRow.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND eventRow.EventType = N'DEMAND_GONE'
                GROUP BY eventRow.SubjectId
            ),
            ArchiveState AS
            (
                SELECT
                    eventRow.SeriesId,
                    MIN(CASE WHEN eventRow.EventType = N'GONE_TIMEOUT_ARCHIVED'
                        THEN eventRow.OccurredAt END) AS ArchivedAt,
                    MAX(CONVERT(INT, 1)) AS HasArchiveConclusion
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventRow.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND eventRow.EventType IN
                    (N'GONE_TIMEOUT_ARCHIVED', N'ARCHIVED_DEMAND_KEY_REAPPEARED')
                GROUP BY eventRow.SeriesId
            )
            INSERT INTO #AuditStates
                (DemandId, SeriesId, WorkType, Sublot, Generation, PredecessorDemandId,
                 IsCurrentGeneration, DemandCreatedAt, DemandLastSeenAt, GoneConfirmedAt,
                 LatestObservationProjectionCommitId, LatestObservationPollTraceId,
                 RawObservationCount, Area, Eqp, Step, MesSourceDate, Package, IsAreaTrusted,
                 SeriesStartedAt, SeriesArchivedAt, CurrentDemandId, DemandStatus,
                 SeriesLifecycle, SeriesCurrentPresence)
            SELECT d.DemandId, d.SeriesId, series.WorkType, series.Sublot,
                d.Generation, d.PredecessorDemandId,
                CONVERT(BIT, CASE WHEN d.CurrentRank = 1 THEN 1 ELSE 0 END),
                d.CreatedAt, COALESCE(fields.CommittedAt, d.CreatedAt), gone.GoneConfirmedAt,
                fields.ProjectionCommitId, fields.PollTraceId,
                COALESCE(fields.ObservationCount, 0),
                fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package,
                CONVERT(BIT, CASE
                    WHEN fields.ObservationCount = 1 AND
                    (
                        (DATALENGTH(fields.Area) = 8
                         AND fields.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9]')
                        OR (DATALENGTH(fields.Area) = 10
                            AND fields.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9]')
                        OR (DATALENGTH(fields.Area) = 10
                            AND fields.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9]-[1-9][0-9]')
                        OR (DATALENGTH(fields.Area) = 12
                            AND fields.Area COLLATE Latin1_General_100_BIN2 LIKE N'[A-Z][1-9][0-9]-[1-9][0-9]')
                    ) THEN 1 ELSE 0 END),
                series.StartedAt, COALESCE(archive.ArchivedAt, series.ArchivedAt),
                currentDemand.DemandId,
                CASE
                    WHEN gone.GoneConfirmedAt IS NOT NULL THEN N'GONE'
                    WHEN archive.HasArchiveConclusion IS NOT NULL
                         AND d.CreatedAt >= COALESCE(archive.ArchivedAt, series.ArchivedAt)
                        THEN N'LONG_GONE_BUT_VISIBLE'
                    ELSE N'VISIBLE'
                END,
                CASE WHEN archive.HasArchiveConclusion IS NULL
                    THEN N'TRACKING' ELSE N'ARCHIVED' END,
                CASE
                    WHEN currentGone.GoneConfirmedAt IS NOT NULL THEN N'GONE'
                    WHEN archive.HasArchiveConclusion IS NOT NULL
                        THEN N'LONG_GONE_BUT_VISIBLE'
                    ELSE N'VISIBLE'
                END
            FROM EligibleDemands AS d
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = d.SeriesId
            INNER JOIN EligibleDemands AS currentDemand
                ON currentDemand.SeriesId = d.SeriesId AND currentDemand.CurrentRank = 1
            INNER JOIN LatestObservationFields AS fields ON fields.DemandId = d.DemandId
            LEFT JOIN GoneState AS gone ON gone.DemandId = d.DemandId
            LEFT JOIN ArchiveState AS archive ON archive.SeriesId = d.SeriesId
            LEFT JOIN GoneState AS currentGone ON currentGone.DemandId = currentDemand.DemandId;

            CREATE TABLE #AuditBlockers
            (
                DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Code NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Priority INT NOT NULL,
                CONSTRAINT PK_AuditBlockers PRIMARY KEY (DemandId, Code)
            );

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT DISTINCT evidence.DemandId, catalog.Code, catalog.Priority
            FROM mesingest.DemandSeriesErrorPeriods AS period
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = period.ErrorCode
            INNER JOIN mesingest.DemandSeriesEvents AS opened
                ON opened.EventId = period.OpenedEventId
            INNER JOIN mesingest.ProjectionCommits AS openedCommit
                ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
            LEFT JOIN mesingest.DemandSeriesEvents AS closed
                ON closed.EventId = period.ClosedEventId
            LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.PeriodId = period.PeriodId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            INNER JOIN #AuditStates AS state ON state.DemandId = evidence.DemandId
            WHERE openedCommit.ProjectionSequence <= @snapshotSequence
              AND (closedCommit.ProjectionSequence IS NULL
                   OR closedCommit.ProjectionSequence > @snapshotSequence)
              AND evidenceCommit.ProjectionSequence <= @snapshotSequence;

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = N'DEMAND_GONE'
            WHERE state.GoneConfirmedAt IS NOT NULL
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId
                     AND existing.Code = catalog.Code);

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = N'SERIES_ARCHIVED'
            WHERE state.SeriesArchivedAt IS NOT NULL
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId
                     AND existing.Code = catalog.Code);

            INSERT INTO #AuditBlockers (DemandId, Code, Priority)
            SELECT state.DemandId, catalog.Code, catalog.Priority
            FROM #AuditStates AS state
            INNER JOIN #ReadabilityBlockerCatalog AS catalog
                ON catalog.Code = N'LONG_GONE_BUT_VISIBLE'
            WHERE state.SeriesArchivedAt IS NOT NULL
              AND state.GoneConfirmedAt IS NULL
              AND state.DemandCreatedAt >= state.SeriesArchivedAt
              AND NOT EXISTS
                  (SELECT 1 FROM #AuditBlockers AS existing
                   WHERE existing.DemandId = state.DemandId
                     AND existing.Code = catalog.Code);

            UPDATE state
            SET ReadabilityRank = CASE WHEN lead.Priority IS NULL THEN 1 ELSE 0 END,
                LeadPriority = COALESCE(lead.Priority, 2147483647)
            FROM #AuditStates AS state
            OUTER APPLY
            (
                SELECT MIN(blocker.Priority) AS Priority
                FROM #AuditBlockers AS blocker
                WHERE blocker.DemandId = state.DemandId
            ) AS lead;

            CREATE INDEX IX_AuditStates_Order
                ON #AuditStates
                    (ReadabilityRank, LeadPriority, DemandLastSeenAt DESC, DemandId ASC);

            """ + ReadabilityAuditFilterAndFacetSql + """
            SELECT state.DemandId, state.SeriesId, state.WorkType, state.Sublot,
                state.Generation, state.PredecessorDemandId, state.IsCurrentGeneration,
                state.DemandCreatedAt, state.DemandLastSeenAt, state.GoneConfirmedAt,
                state.LatestObservationProjectionCommitId,
                state.LatestObservationPollTraceId, state.RawObservationCount,
                state.Area, state.Eqp, state.Step, state.MesSourceDate, state.Package,
                state.SeriesStartedAt, state.SeriesArchivedAt, state.CurrentDemandId,
                state.DemandStatus, state.SeriesLifecycle, state.SeriesCurrentPresence,
                blockerCodes.Codes
            FROM #ExactMatches AS exact
            INNER JOIN #AuditStates AS state ON state.DemandId = exact.DemandId
            OUTER APPLY
            (
                SELECT STRING_AGG(CONVERT(NVARCHAR(128), blocker.Code), NCHAR(31))
                    WITHIN GROUP (ORDER BY blocker.Priority, blocker.Code) AS Codes
                FROM #AuditBlockers AS blocker
                WHERE blocker.DemandId = state.DemandId
            ) AS blockerCodes
            WHERE @afterReadabilityRank IS NULL
               OR state.ReadabilityRank > @afterReadabilityRank
               OR (state.ReadabilityRank = @afterReadabilityRank
                   AND state.LeadPriority > @afterLeadPriority)
               OR (state.ReadabilityRank = @afterReadabilityRank
                   AND state.LeadPriority = @afterLeadPriority
                   AND state.DemandLastSeenAt < @afterDemandLastSeenAt)
               OR (state.ReadabilityRank = @afterReadabilityRank
                   AND state.LeadPriority = @afterLeadPriority
                   AND state.DemandLastSeenAt = @afterDemandLastSeenAt
                   AND state.DemandId > @afterDemandId COLLATE Latin1_General_100_BIN2)
            ORDER BY state.ReadabilityRank, state.LeadPriority,
                state.DemandLastSeenAt DESC, state.DemandId ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        AddDateTimeOffset(command, "@availabilityAtSnapshot", availabilityAtSnapshot);
        BindReadabilityAuditFilterParameters(command, normalized);
        command.Parameters.Add("@offset", SqlDbType.BigInt).Value = cursor is null ? offset : 0;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
        AddNullableInt(command, "@afterReadabilityRank", cursor?.AfterReadabilityRank);
        AddNullableInt(command, "@afterLeadPriority", cursor?.AfterLeadBlockerPriority);
        AddNullableDateTimeOffset(command, "@afterDemandLastSeenAt", cursor?.AfterDemandLastSeenAt);
        AddNullableNVarChar(command, "@afterDemandId", 64, cursor?.AfterDemandId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAuditPageResultsAsync(reader, pageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AuditPageState> ReadAuditPageResultsAsync(
        SqlDataReader reader,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The readability audit total is missing.");
        }
        var exactTotal = reader.GetInt64(0);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The readability state facets are missing.");
        }
        var stateFacets = new List<ReadabilityStateFacetSnapshot>(2);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stateFacets.Add(new ReadabilityStateFacetSnapshot(
                reader.GetString(0), reader.GetInt64(1)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The readability blocker facets are missing.");
        }
        var blockerFacets = new List<ReadabilityBlockerFacetSnapshot>(7);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blockerFacets.Add(new ReadabilityBlockerFacetSnapshot(
                reader.GetString(0), reader.GetInt64(1)));
        }

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The readability audit page is missing.");
        }
        var states = new List<AuditDemandState>(pageSize);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rawObservationCount = checked((int)reader.GetInt64(12));
            var fields = rawObservationCount == 1
                ? new LiveMesFieldSetSnapshot(
                    GetNullableString(reader, 13), GetNullableString(reader, 14),
                    GetNullableString(reader, 15), GetNullableDateTimeOffset(reader, 16),
                    GetNullableString(reader, 17))
                : null;
            var blockers = reader.IsDBNull(24)
                ? Array.Empty<string>()
                : reader.GetString(24).Split((char)31, StringSplitOptions.RemoveEmptyEntries);
            states.Add(new AuditDemandState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), GetNullableString(reader, 5), reader.GetBoolean(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8),
                GetNullableDateTimeOffset(reader, 9), reader.GetString(10), reader.GetString(11),
                rawObservationCount, fields, reader.GetString(22), reader.GetString(23),
                GetNullableDateTimeOffset(reader, 18), GetNullableDateTimeOffset(reader, 19),
                reader.GetString(20), reader.GetString(21), blockers));
        }

        return new AuditPageState(
            exactTotal,
            new ReadabilityAuditFacets(stateFacets, blockerFacets),
            states);
    }

    private static void AddNullableInt(SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static async Task<IReadOnlyList<AuditDemandState>> ReadAuditStatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        string? demandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH EligibleDemands AS
            (
                SELECT d.DemandId, d.SeriesId, d.Generation, d.PredecessorDemandId,
                    d.CreatedAt, d.CreatedPollTraceId, d.CreatedProjectionCommitId,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.SeriesId ORDER BY d.Generation DESC) AS CurrentRank
                FROM mesingest.TransportDemands AS d
                INNER JOIN mesingest.ProjectionCommits AS createdCommit
                    ON createdCommit.ProjectionCommitId = d.CreatedProjectionCommitId
                WHERE createdCommit.ProjectionSequence <= @snapshotSequence
            ),
            LatestObservations AS
            (
                SELECT d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt,
                    ROW_NUMBER() OVER
                        (PARTITION BY d.DemandId ORDER BY observationCommit.ProjectionSequence DESC) AS rn
                FROM EligibleDemands AS d
                INNER JOIN mesingest.DemandRawObservations AS observation
                    ON observation.DemandId = d.DemandId
                INNER JOIN mesingest.ProjectionCommits AS observationCommit
                    ON observationCommit.ProjectionCommitId = observation.ProjectionCommitId
                WHERE observationCommit.ProjectionSequence <= @snapshotSequence
                GROUP BY d.DemandId, observationCommit.ProjectionCommitId,
                    observationCommit.ProjectionSequence, observationCommit.PollTraceId,
                    observationCommit.CommittedAt
            ),
            LatestObservationFields AS
            (
                SELECT latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt,
                    COUNT_BIG(observation.Ordinal) AS ObservationCount,
                    MAX(observation.Area) AS Area, MAX(observation.Eqp) AS Eqp,
                    MAX(observation.Step) AS Step,
                    MAX(SWITCHOFFSET(observation.MesSourceDate, '+00:00')) AS MesSourceDate,
                    MAX(observation.Package) AS Package
                FROM LatestObservations AS latest
                INNER JOIN mesingest.DemandRawObservations AS observation
                    ON observation.DemandId = latest.DemandId
                   AND observation.ProjectionCommitId = latest.ProjectionCommitId
                WHERE latest.rn = 1
                GROUP BY latest.DemandId, latest.ProjectionCommitId,
                    latest.PollTraceId, latest.CommittedAt
            ),
            GoneState AS
            (
                SELECT eventRow.SubjectId AS DemandId, MAX(eventRow.OccurredAt) AS GoneConfirmedAt
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventRow.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND eventRow.EventType = N'DEMAND_GONE'
                GROUP BY eventRow.SubjectId
            ),
            ArchiveState AS
            (
                SELECT
                    eventRow.SeriesId,
                    MIN(CASE WHEN eventRow.EventType = N'GONE_TIMEOUT_ARCHIVED'
                        THEN eventRow.OccurredAt END) AS ArchivedAt,
                    MAX(CONVERT(INT, 1)) AS HasArchiveConclusion
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventRow.ProjectionCommitId
                WHERE eventCommit.ProjectionSequence <= @snapshotSequence
                  AND eventRow.EventType IN
                    (N'GONE_TIMEOUT_ARCHIVED', N'ARCHIVED_DEMAND_KEY_REAPPEARED')
                GROUP BY eventRow.SeriesId
            )
            SELECT d.DemandId, d.SeriesId, series.WorkType, series.Sublot,
                d.Generation, d.PredecessorDemandId, d.CurrentRank,
                d.CreatedAt, fields.CommittedAt, gone.GoneConfirmedAt,
                fields.ProjectionCommitId, fields.PollTraceId,
                COALESCE(fields.ObservationCount, 0),
                fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package,
                series.StartedAt, COALESCE(archive.ArchivedAt, series.ArchivedAt),
                currentDemand.DemandId AS CurrentDemandId,
                currentGone.GoneConfirmedAt AS CurrentDemandGoneConfirmedAt
            FROM EligibleDemands AS d
            INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = d.SeriesId
            INNER JOIN EligibleDemands AS currentDemand
                ON currentDemand.SeriesId = d.SeriesId AND currentDemand.CurrentRank = 1
            LEFT JOIN LatestObservationFields AS fields ON fields.DemandId = d.DemandId
            LEFT JOIN GoneState AS gone ON gone.DemandId = d.DemandId
            LEFT JOIN ArchiveState AS archive ON archive.SeriesId = d.SeriesId
            LEFT JOIN GoneState AS currentGone ON currentGone.DemandId = currentDemand.DemandId
            WHERE @demandId IS NULL
               OR d.DemandId = @demandId COLLATE Latin1_General_100_CI_AS
            ORDER BY d.DemandId;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        AddNullableNVarChar(command, "@demandId", 64, demandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<AuditDemandBaseRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AuditDemandBaseRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), GetNullableString(reader, 5), reader.GetInt64(6) == 1,
                reader.GetFieldValue<DateTimeOffset>(7),
                GetNullableDateTimeOffset(reader, 8) ?? reader.GetFieldValue<DateTimeOffset>(7),
                GetNullableDateTimeOffset(reader, 9),
                reader.GetString(10), reader.GetString(11), checked((int)reader.GetInt64(12)),
                GetNullableString(reader, 13), GetNullableString(reader, 14),
                GetNullableString(reader, 15), GetNullableDateTimeOffset(reader, 16),
                GetNullableString(reader, 17), GetNullableDateTimeOffset(reader, 18),
                GetNullableDateTimeOffset(reader, 19), reader.GetString(20),
                GetNullableDateTimeOffset(reader, 21)));
        }
        await reader.CloseAsync().ConfigureAwait(false);

        var activeCodesByDemand = await ReadActiveAuditConditionCodesAsync(
            connection,
            transaction,
            snapshotSequence,
            demandId,
            cancellationToken).ConfigureAwait(false);
        var states = new List<AuditDemandState>(rows.Count);
        foreach (var row in rows)
        {
            var blockers = activeCodesByDemand.TryGetValue(row.DemandId, out var activeCodes)
                ? new List<string>(activeCodes)
                : [];
            if (row.GoneConfirmedAt is not null)
            {
                blockers.Add("DEMAND_GONE");
            }
            if (row.SeriesArchivedAt is not null)
            {
                blockers.Add("SERIES_ARCHIVED");
            }
            var isPostarchiveVisible = row.SeriesArchivedAt is not null
                && row.GoneConfirmedAt is null
                && row.DemandCreatedAt >= row.SeriesArchivedAt;
            if (isPostarchiveVisible)
            {
                blockers.Add("LONG_GONE_BUT_VISIBLE");
            }
            var normalizedBlockers = ReadabilityBlockerCatalog.NormalizeMatchedCodes(blockers);
            var demandStatus = row.GoneConfirmedAt is not null
                ? DemandSeriesLifecycleContract.Gone
                : isPostarchiveVisible
                    ? DemandSeriesLifecycleContract.LongGoneButVisible
                    : DemandSeriesLifecycleContract.Visible;
            var seriesLifecycle = row.SeriesArchivedAt is null
                ? DemandSeriesLifecycleContract.Tracking
                : DemandSeriesLifecycleContract.Archived;
            var seriesPresence = row.CurrentDemandGoneConfirmedAt is not null
                ? DemandSeriesLifecycleContract.Gone
                : row.SeriesArchivedAt is null
                    ? DemandSeriesLifecycleContract.Visible
                    : DemandSeriesLifecycleContract.LongGoneButVisible;
            var liveFields = row.RawObservationCount == 1
                ? new LiveMesFieldSetSnapshot(
                    row.Area, row.Eqp, row.Step, row.MesSourceDate, row.Package)
                : null;
            states.Add(new AuditDemandState(
                row.DemandId, row.SeriesId, row.WorkType, row.Sublot, row.Generation,
                row.PredecessorDemandId, row.IsCurrentGeneration, row.DemandCreatedAt,
                row.DemandLastSeenAt, row.GoneConfirmedAt, row.LatestObservationProjectionCommitId,
                row.LatestObservationPollTraceId, row.RawObservationCount, liveFields,
                seriesLifecycle, seriesPresence, row.SeriesStartedAt, row.SeriesArchivedAt,
                row.CurrentDemandId, demandStatus, normalizedBlockers));
        }
        return states;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        ReadActiveAuditConditionCodesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotSequence,
        string? demandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT evidence.DemandId, period.ErrorCode
            FROM mesingest.DemandSeriesErrorPeriods AS period
            INNER JOIN mesingest.DemandSeriesEvents AS opened
                ON opened.EventId = period.OpenedEventId
            INNER JOIN mesingest.ProjectionCommits AS openedCommit
                ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
            LEFT JOIN mesingest.DemandSeriesEvents AS closed
                ON closed.EventId = period.ClosedEventId
            LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS evidence
                ON evidence.PeriodId = period.PeriodId
            INNER JOIN mesingest.ProjectionCommits AS evidenceCommit
                ON evidenceCommit.ProjectionCommitId = evidence.ProjectionCommitId
            WHERE openedCommit.ProjectionSequence <= @snapshotSequence
              AND (closedCommit.ProjectionSequence IS NULL
                   OR closedCommit.ProjectionSequence > @snapshotSequence)
              AND evidence.DemandId IS NOT NULL
              AND evidenceCommit.ProjectionSequence <= @snapshotSequence
              AND (@demandId IS NULL
                   OR evidence.DemandId = @demandId COLLATE Latin1_General_100_CI_AS)
            ORDER BY evidence.DemandId, period.ErrorCode;
            """;
        command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
        AddNullableNVarChar(command, "@demandId", 64, demandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var mutable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evidenceDemandId = reader.GetString(0);
            if (!mutable.TryGetValue(evidenceDemandId, out var codes))
            {
                codes = [];
                mutable.Add(evidenceDemandId, codes);
            }
            codes.Add(reader.GetString(1));
        }
        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private static ReadabilityAuditListItemSnapshot ToAuditListItem(AuditDemandState state) =>
        new(
            state.DemandId,
            state.SeriesId,
            state.WorkType,
            state.Sublot,
            state.Generation,
            state.PredecessorDemandId,
            state.DemandStatus,
            state.SeriesLifecycle,
            state.SeriesCurrentPresence,
            state.IsCurrentGeneration,
            state.DemandCreatedAt,
            state.DemandLastSeenAt,
            state.GoneConfirmedAt,
            state.LiveMesFields,
            state.RawObservationCount,
            GetReadabilityState(state.Blockers),
            ReadabilityBlockerCatalog.SelectLead(state.Blockers),
            state.Blockers,
            state.LatestObservationPollTraceId,
            state.LatestObservationProjectionCommitId,
            state.DemandLastSeenAt);

    private static string GetReadabilityState(IReadOnlyCollection<string> blockers) =>
        blockers.Count == 0
            ? ExternalReadabilityStates.Readable
            : ExternalReadabilityStates.NotReadable;

    private static string GetQualificationCheckResult(
        AuditDemandState state,
        ReadabilityQualificationCheckDefinition definition)
    {
        if ((definition.Code is "REQUIRED_MES_FIELDS_PRESENT" or "MES_FIELD_FORMAT_VALID")
            && state.RawObservationCount != 1)
        {
            return ReadabilityQualificationCheckResults.NotEvaluated;
        }
        return state.Blockers.Contains(definition.BlockingCode, StringComparer.Ordinal)
            ? ReadabilityQualificationCheckResults.Failed
            : ReadabilityQualificationCheckResults.Passed;
    }

    private static int GetReadabilityRank(string state) =>
        string.Equals(state, ExternalReadabilityStates.NotReadable, StringComparison.Ordinal) ? 0 : 1;

    private static int GetLeadPriority(string? lead) =>
        lead is null ? int.MaxValue : ReadabilityBlockerCatalog.GetRequired(lead).Priority;

    private static async Task<IReadOnlyList<DemandRawObservationSnapshot>>
        ReadLatestAuditRawObservationsAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string demandId,
            string projectionCommitId,
            DateTimeOffset availabilityAtSnapshot,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observation.Ordinal, observation.PollTraceId,
                observation.ProjectionCommitId, observation.SeriesId,
                observation.DemandId, observation.WorkType, observation.Sublot,
                observation.Area, observation.Eqp, observation.Step,
                observation.MesSourceDate, observation.Package, poll.CompletedAt,
                observation.MesSourceDateRaw
            FROM mesingest.DemandRawObservations AS observation
            INNER JOIN mesingest.PollTraces AS poll
                ON poll.PollTraceId = observation.PollTraceId
            WHERE observation.DemandId = @demandId
              AND observation.ProjectionCommitId = @projectionCommitId
              AND poll.CompletedAt > @availabilityAtSnapshot
            ORDER BY observation.Ordinal;
            """;
        AddNVarChar(command, "@demandId", 64, demandId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddDateTimeOffset(command, "@availabilityAtSnapshot", availabilityAtSnapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<DemandRawObservationSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DemandRawObservationSnapshot(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                MesObservationAssignment.Assigned, GetNullableString(reader, 3),
                GetNullableString(reader, 4), GetNullableString(reader, 5),
                GetNullableString(reader, 6), GetNullableString(reader, 7),
                GetNullableString(reader, 8), GetNullableString(reader, 9),
                GetNullableDateTimeOffset(reader, 10), GetNullableString(reader, 11),
                reader.GetFieldValue<DateTimeOffset>(12), GetNullableString(reader, 13)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<ReadabilityBlockerEvidenceSnapshot>>
        ReadAuditBlockerEvidenceAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            AuditDemandState state,
            long snapshotSequence,
            CancellationToken cancellationToken)
    {
        var evidence = state.Blockers.ToDictionary(
            code => code,
            _ => new List<ReadabilityEvidenceItemSnapshot>(),
            StringComparer.Ordinal);
        if (evidence.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT period.ErrorCode, period.SubjectKind, item.ObservedValue,
                    item.ExpectedRule, item.ObservedAt, item.PollTraceId,
                    item.ProjectionCommitId
                FROM mesingest.DemandSeriesErrorPeriods AS period
                INNER JOIN mesingest.SeriesErrorPeriodEvidence AS item
                    ON item.PeriodId = period.PeriodId
                INNER JOIN mesingest.ProjectionCommits AS itemCommit
                    ON itemCommit.ProjectionCommitId = item.ProjectionCommitId
                INNER JOIN mesingest.DemandSeriesEvents AS opened
                    ON opened.EventId = period.OpenedEventId
                INNER JOIN mesingest.ProjectionCommits AS openedCommit
                    ON openedCommit.ProjectionCommitId = opened.ProjectionCommitId
                LEFT JOIN mesingest.DemandSeriesEvents AS closed
                    ON closed.EventId = period.ClosedEventId
                LEFT JOIN mesingest.ProjectionCommits AS closedCommit
                    ON closedCommit.ProjectionCommitId = closed.ProjectionCommitId
                WHERE item.DemandId = @demandId
                  AND itemCommit.ProjectionSequence <= @snapshotSequence
                  AND openedCommit.ProjectionSequence <= @snapshotSequence
                  AND (closedCommit.ProjectionSequence IS NULL
                       OR closedCommit.ProjectionSequence > @snapshotSequence)
                ORDER BY itemCommit.ProjectionSequence, item.EvidenceId;
                """;
            AddNVarChar(command, "@demandId", 64, state.DemandId);
            command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var code = reader.GetString(0);
                if (!evidence.TryGetValue(code, out var items))
                {
                    continue;
                }
                items.Add(new ReadabilityEvidenceItemSnapshot(
                    reader.GetString(1), GetNullableString(reader, 2), reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        if (state.Blockers.Contains("DEMAND_GONE", StringComparer.Ordinal)
            || state.Blockers.Contains("SERIES_ARCHIVED", StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT eventRow.EventType, eventRow.SubjectKind, eventRow.SubjectId,
                    eventRow.OccurredAt, eventRow.PollTraceId,
                    eventRow.ProjectionCommitId
                FROM mesingest.DemandSeriesEvents AS eventRow
                INNER JOIN mesingest.ProjectionCommits AS eventCommit
                    ON eventCommit.ProjectionCommitId = eventRow.ProjectionCommitId
                WHERE eventRow.SeriesId = @seriesId
                  AND eventCommit.ProjectionSequence <= @snapshotSequence
                  AND
                  (
                      (eventRow.EventType = N'DEMAND_GONE'
                       AND eventRow.SubjectId = @demandId)
                      OR eventRow.EventType IN
                        (N'GONE_TIMEOUT_ARCHIVED', N'ARCHIVED_DEMAND_KEY_REAPPEARED')
                  )
                ORDER BY eventCommit.ProjectionSequence, eventRow.SeriesSequence;
                """;
            AddNVarChar(command, "@seriesId", 64, state.SeriesId);
            AddNVarChar(command, "@demandId", 64, state.DemandId);
            command.Parameters.Add("@snapshotSequence", SqlDbType.BigInt).Value = snapshotSequence;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var eventType = reader.GetString(0);
                var code = string.Equals(eventType, "DEMAND_GONE", StringComparison.Ordinal)
                    ? "DEMAND_GONE"
                    : "SERIES_ARCHIVED";
                if (!evidence.TryGetValue(code, out var items))
                {
                    continue;
                }
                items.Add(new ReadabilityEvidenceItemSnapshot(
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    code == "DEMAND_GONE" ? "VISIBLE_REQUIRED" : "TRACKING_REQUIRED",
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }
        if (state.Blockers.Contains("LONG_GONE_BUT_VISIBLE", StringComparer.Ordinal)
            && evidence["LONG_GONE_BUT_VISIBLE"].Count == 0)
        {
            evidence["LONG_GONE_BUT_VISIBLE"].Add(new ReadabilityEvidenceItemSnapshot(
                "ARCHIVED_SERIES_VISIBILITY", state.DemandId, "ARCHIVED_SERIES_MUST_NOT_REAPPEAR",
                state.DemandCreatedAt, state.LatestObservationPollTraceId,
                state.LatestObservationProjectionCommitId));
        }

        return state.Blockers
            .Select(code => new ReadabilityBlockerEvidenceSnapshot(
                code,
                ReadabilityBlockerCatalog.GetRequired(code).Priority,
                evidence[code]))
            .ToArray();
    }

    private static async Task<ReadabilityAuditPollTraceSnapshot> ReadAuditPollTraceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT poll.PollTraceId, poll.QueryVersion, poll.Outcome,
                poll.StartedAt, poll.CompletedAt, poll.[RowCount], poll.ContentDigest,
                commitRow.ProjectionCommitId, commitRow.ProjectionSequence
            FROM mesingest.PollTraces AS poll
            INNER JOIN mesingest.ProjectionCommits AS commitRow
                ON commitRow.PollTraceId = poll.PollTraceId
            WHERE poll.PollTraceId = @pollTraceId;
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The audit observation PollTrace is missing.");
        }
        return new ReadabilityAuditPollTraceSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8));
    }

    private sealed record AuditDemandBaseRow(
        string DemandId,
        string SeriesId,
        string WorkType,
        string Sublot,
        int Generation,
        string? PredecessorDemandId,
        bool IsCurrentGeneration,
        DateTimeOffset DemandCreatedAt,
        DateTimeOffset DemandLastSeenAt,
        DateTimeOffset? GoneConfirmedAt,
        string LatestObservationProjectionCommitId,
        string LatestObservationPollTraceId,
        int RawObservationCount,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package,
        DateTimeOffset? SeriesStartedAt,
        DateTimeOffset? SeriesArchivedAt,
        string CurrentDemandId,
        DateTimeOffset? CurrentDemandGoneConfirmedAt);

    private sealed record AuditDemandState(
        string DemandId,
        string SeriesId,
        string WorkType,
        string Sublot,
        int Generation,
        string? PredecessorDemandId,
        bool IsCurrentGeneration,
        DateTimeOffset DemandCreatedAt,
        DateTimeOffset DemandLastSeenAt,
        DateTimeOffset? GoneConfirmedAt,
        string LatestObservationProjectionCommitId,
        string LatestObservationPollTraceId,
        int RawObservationCount,
        LiveMesFieldSetSnapshot? LiveMesFields,
        string SeriesLifecycle,
        string SeriesCurrentPresence,
        DateTimeOffset? SeriesStartedAt,
        DateTimeOffset? SeriesArchivedAt,
        string CurrentDemandId,
        string DemandStatus,
        IReadOnlyList<string> Blockers);

    private sealed record AuditPageState(
        long ExactTotalDemandCount,
        ReadabilityAuditFacets Facets,
        IReadOnlyList<AuditDemandState> Items);

}
