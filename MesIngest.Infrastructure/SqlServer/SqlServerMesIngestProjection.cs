using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

/// <summary>
/// SQL Server implementation of the new MesIngest projection seam. Every round
/// result is recorded transactionally; only SUCCESS receives a projection commit
/// and can change business state.
/// </summary>
public sealed class SqlServerMesIngestProjection : IMesIngestProjection
{
    private const string SuccessOutcome = "SUCCESS";
    private const string TrackingLifecycle = "TRACKING";
    private const string VisiblePresence = "VISIBLE";
    private const string GonePresence = "GONE";
    private const string VisibleDemandStatus = "VISIBLE";
    private const string GoneDemandStatus = "GONE";
    private const string SeriesStartedEvent = "DEMAND_SERIES_STARTED";
    private const string DemandCreatedEvent = "TRANSPORT_DEMAND_CREATED";

    private readonly string _connectionString;
    private readonly string _hostSessionId = NewId();
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SemaphoreSlim _hostSessionGate = new(1, 1);
    private volatile bool _schemaEnsured;
    private volatile bool _hostSessionInitialized;

    public SqlServerMesIngestProjection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A dedicated new-MesIngest SQL Server connection string is required.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public async Task BeginHostSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_hostSessionInitialized)
        {
            return;
        }

        await _hostSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hostSessionInitialized)
            {
                return;
            }

            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lockCommand = connection.CreateCommand();
                lockCommand.Transaction = transaction;
                lockCommand.CommandText = """
                    DECLARE @result INT;
                    EXEC @result = sys.sp_getapplock
                        @Resource = N'MesIngest.NewProjection.HostSession',
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = 30000;
                    IF @result < 0
                        THROW 51020, 'Unable to establish the new-MesIngest Host session.', 1;
                    """;
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                var phase = RestartBarrierPhaseContract.Barrier;
                var startedAt = DateTimeOffset.UtcNow;

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE mesingest.HostSessions SET IsCurrent = 0 WHERE IsCurrent = 1;
                    INSERT INTO mesingest.HostSessions
                        (HostSessionId, StartedAt, RestartPhase, IsCurrent)
                    VALUES
                        (@hostSessionId, @startedAt, @restartPhase, 1);
                    """;
                AddNVarChar(command, "@hostSessionId", 64, _hostSessionId);
                AddDateTimeOffset(command, "@startedAt", startedAt);
                AddNVarChar(command, "@restartPhase", 32, phase);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await InsertAbsenceAuthorityEventAsync(
                    connection,
                    transaction,
                    RestartBarrierEventCode.Entered,
                    startedAt,
                    pollTraceId: null,
                    projectionCommitId: null,
                    phaseBefore: RestartBarrierPhaseContract.Normal,
                    phaseAfter: RestartBarrierPhaseContract.Barrier,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _hostSessionInitialized = true;
            }
            catch (Exception exception)
            {
                await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _hostSessionGate.Release();
        }
    }

    public async Task<RoundCommitReceipt> CommitRoundAsync(
        MesTaskUnionRound round,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        cancellationToken.ThrowIfCancellationRequested();
        round = round with
        {
            StartedAt = round.StartedAt.ToUniversalTime(),
            CompletedAt = round.CompletedAt.ToUniversalTime(),
        };
        var prepared = PrepareRound(round, cancellationToken);
        await BeginHostSessionAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await LoadExistingPollTraceForUpdateAsync(
                connection,
                transaction,
                round.PollTraceId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!IsSameAcceptedContent(existing, round, prepared.ContentDigest))
                {
                    throw new PollTraceConflictException(round.PollTraceId);
                }

                var replay = await ReadAcceptedReceiptAsync(
                    connection,
                    transaction,
                    existing,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            if (round.Outcome is not MesTaskUnionRoundOutcome.Success)
            {
                await InsertPollTraceAsync(
                    connection,
                    transaction,
                    round,
                    prepared.ContentDigest,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new RoundCommitReceipt(
                    round.PollTraceId,
                    round.Outcome,
                    ProjectionCommitId: null,
                    SeriesIds: [],
                    DemandIds: [],
                    IsReplay: false);
            }

            var hostSession = await LoadHostSessionForUpdateAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var restartTransition = RestartBarrierTransition.AcceptSuccess(
                RestartBarrierPhaseContract.Parse(hostSession.RestartPhase));
            var restartPhaseAfter = restartTransition.After.ToContractValue();
            var projectionCommitId = NewId();
            await InsertPollTraceAndCommitAsync(
                connection,
                transaction,
                round,
                prepared.ContentDigest,
                projectionCommitId,
                _hostSessionId,
                hostSession.RestartPhase,
                restartPhaseAfter,
                restartTransition.AbsenceAuthority,
                cancellationToken).ConfigureAwait(false);
            var bootstrapRound = await IsFirstProjectionCommitAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            foreach (var item in prepared.Observations.Where(item => item.KeyToken is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InsertRawObservationAsync(
                    connection,
                    transaction,
                    round.PollTraceId,
                    projectionCommitId,
                    identity: null,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            var groups = PrepareAssignedGroups(prepared);
            var workTypesBySublot = PrepareWorkTypeMemberships(groups);
            var seriesIds = new List<string>(groups.Count);
            var demandIds = new List<string>(groups.Count);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identityObservation = group.Observations[0].Observation;
                var workTypeMembership = workTypesBySublot[identityObservation.Sublot!];
                var uniqueObservation = group.Observations.Count == 1
                    ? identityObservation
                    : null;

                var current = await LoadCurrentProjectionForUpdateAsync(
                    connection,
                    transaction,
                    group.KeyToken,
                    cancellationToken).ConfigureAwait(false);

                ProjectedIdentity identity;
                if (current is null)
                {
                    identity = await InsertFirstGenerationAsync(
                        connection,
                        transaction,
                        round,
                        projectionCommitId,
                        group.Observations[0],
                        uniqueObservation,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (string.Equals(current.DemandStatus, GoneDemandStatus, StringComparison.Ordinal))
                {
                    EnsureReappearanceCanAdvance(current, identityObservation, round.CompletedAt);
                    identity = await InsertSuccessorGenerationAsync(
                        connection,
                        transaction,
                        current,
                        round,
                        projectionCommitId,
                        group.Observations[0],
                        uniqueObservation,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    EnsureCurrentObservationCanAdvance(current, identityObservation, round.CompletedAt);
                    if (uniqueObservation is null)
                    {
                        await AdvanceConflictingObservationAsync(
                            connection,
                            transaction,
                            current,
                            round,
                            projectionCommitId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await AdvanceLiveObservationAsync(
                            connection,
                            transaction,
                            current,
                            uniqueObservation,
                            round,
                            projectionCommitId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    identity = new ProjectedIdentity(current.SeriesId, current.DemandId);
                }

                await SynchronizeDemandConditionsAsync(
                    connection,
                    transaction,
                    identity,
                    EvaluateDemandConditions(group, workTypeMembership),
                    subjectKind => GetObservedValue(group, workTypeMembership, subjectKind),
                    hasTrustworthyLiveFieldSet: uniqueObservation is not null,
                    round,
                    projectionCommitId,
                    bootstrapRound,
                    cancellationToken).ConfigureAwait(false);

                foreach (var item in group.Observations)
                {
                    await InsertRawObservationAsync(
                        connection,
                        transaction,
                        round.PollTraceId,
                        projectionCommitId,
                        identity,
                        item,
                        cancellationToken).ConfigureAwait(false);
                }

                seriesIds.Add(identity.SeriesId);
                demandIds.Add(identity.DemandId);
            }

            if (restartTransition.AbsenceAuthority)
            {
                await MarkAbsentVisibleDemandsGoneAsync(
                    connection,
                    transaction,
                    groups.Select(group => group.KeyToken).ToHashSet(StringComparer.Ordinal),
                    round,
                    projectionCommitId,
                    seriesIds,
                    demandIds,
                    cancellationToken).ConfigureAwait(false);
            }

            await AdvanceRestartBarrierAsync(
                connection,
                transaction,
                hostSession.RestartPhase,
                restartPhaseAfter,
                restartTransition.EventCode,
                round,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RoundCommitReceipt(
                round.PollTraceId,
                round.Outcome,
                projectionCommitId,
                StableDistinct(seriesIds),
                StableDistinct(demandIds),
                IsReplay: false);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DemandSeriesSnapshot?> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(workType, nameof(workType), 128);
        ValidateRequiredText(sublot, nameof(sublot), 256);
        var keyToken = TransportDemandKeyIdentity.CreateToken(workType, sublot);
        return await ReadDemandSeriesAsync(
            "s.KeyToken = @identity",
            keyToken,
            expectedWorkType: workType,
            expectedSublot: sublot,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DemandSeriesSnapshot?> GetDemandSeriesAsync(
        string seriesId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(seriesId, nameof(seriesId), 64);
        return await ReadDemandSeriesAsync(
            "s.SeriesId = @identity",
            seriesId,
            expectedWorkType: null,
            expectedSublot: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PollTraceSnapshot?> GetPollTraceAsync(
        string pollTraceId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(pollTraceId, nameof(pollTraceId), 128);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            PollTraceRow? trace;
            await using (var command = connection.CreateCommand())
            {
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
                        c.ProjectionCommitId,
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
                trace = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadPollTraceRow(reader)
                    : null;
            }

            if (trace is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var observations = await ReadRawObservationsAsync(
                connection,
                transaction,
                "o.PollTraceId = @identity",
                pollTraceId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new PollTraceSnapshot(
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
                        trace.AbsenceAuthority!.Value),
                observations);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AbsenceAuthoritySnapshot> GetAbsenceAuthorityAsync(
        CancellationToken cancellationToken = default)
    {
        await BeginHostSessionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAbsenceAuthorityAsync(
                _hostSessionId,
                requireCurrent: true,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "This Host session is no longer current and cannot expose absence authority.");
    }

    public async Task<AbsenceAuthoritySnapshot?> GetAbsenceAuthorityAsync(
        string hostSessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostSessionId) || hostSessionId.Length > 64)
        {
            throw new ArgumentException(
                "Host session id must contain 1 to 64 non-whitespace characters.",
                nameof(hostSessionId));
        }

        await BeginHostSessionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAbsenceAuthorityAsync(
                hostSessionId,
                requireCurrent: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AbsenceAuthoritySnapshot?> ReadAbsenceAuthorityAsync(
        string hostSessionId,
        bool requireCurrent,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            HostSessionRow hostSession;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT HostSessionId, StartedAt, RestartPhase, IsCurrent
                    FROM mesingest.HostSessions
                    WHERE HostSessionId = @hostSessionId;
                    """;
                AddNVarChar(command, "@hostSessionId", 64, hostSessionId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                hostSession = new HostSessionRow(
                    reader.GetString(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetString(2),
                    reader.GetBoolean(3));
            }

            if (requireCurrent && !hostSession.IsCurrent)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var events = new List<AbsenceAuthorityEventSnapshot>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT EventId, HostSessionId, EventType, OccurredAt, PollTraceId,
                           ProjectionCommitId, PhaseBefore, PhaseAfter
                    FROM mesingest.AbsenceAuthorityEvents
                    WHERE HostSessionId = @hostSessionId
                    ORDER BY CASE EventType
                        WHEN @enteredEventCode THEN 0
                        WHEN @baselineEventCode THEN 1
                        WHEN @restoredEventCode THEN 2
                        ELSE 3
                    END, EventId;
                    """;
                AddNVarChar(command, "@hostSessionId", 64, hostSessionId);
                AddNVarChar(command, "@enteredEventCode", 128, RestartBarrierEventCode.Entered);
                AddNVarChar(command, "@baselineEventCode", 128, RestartBarrierEventCode.BaselineCompleted);
                AddNVarChar(
                    command,
                    "@restoredEventCode",
                    128,
                    RestartBarrierEventCode.AbsenceAuthorityRestored);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    events.Add(new AbsenceAuthorityEventSnapshot(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        GetNullableString(reader, 4),
                        GetNullableString(reader, 5),
                        reader.GetString(6),
                        reader.GetString(7)));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AbsenceAuthoritySnapshot(
                hostSession.HostSessionId,
                hostSession.StartedAt,
                hostSession.RestartPhase,
                hostSession.IsCurrent,
                RestartBarrierPhaseContract.Parse(hostSession.RestartPhase)
                    is RestartBarrierPhase.Normal,
                events);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DemandSeriesSnapshot?> ReadDemandSeriesAsync(
        string predicate,
        string identity,
        string? expectedWorkType,
        string? expectedSublot,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        try
        {
            DemandSeriesRow? series;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT
                        s.SeriesId,
                        s.WorkType,
                        s.Sublot,
                        s.Lifecycle,
                        s.CurrentPresence,
                        s.StartedAt,
                        s.CreatedPollTraceId,
                        s.CreatedProjectionCommitId,
                        s.LatestProjectionCommitId,
                        s.CurrentDemandId
                    FROM mesingest.DemandSeries AS s
                    WHERE {predicate};
                    """;
                if (expectedWorkType is null)
                {
                    AddNVarChar(command, "@identity", 64, identity);
                }
                else
                {
                    AddChar(command, "@identity", 64, identity);
                }
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                series = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadDemandSeriesRow(reader)
                    : null;
            }

            if (series is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (expectedWorkType is not null
                && (!string.Equals(series.WorkType, expectedWorkType, StringComparison.Ordinal)
                    || !string.Equals(series.Sublot, expectedSublot, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "TransportDemandKey token collision detected; refusing to return another series.");
            }

            var observations = await ReadRawObservationsAsync(
                connection,
                transaction,
                "o.SeriesId = @identity",
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            var events = await ReadEventsAsync(
                connection,
                transaction,
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            var errorState = await ReadErrorStateAsync(
                connection,
                transaction,
                series.SeriesId,
                cancellationToken).ConfigureAwait(false);
            var demands = await ReadDemandGenerationsAsync(
                connection,
                transaction,
                series.SeriesId,
                series.CurrentDemandId,
                errorState.CurrentConditions,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var currentDemand = demands.Single(demand =>
                string.Equals(demand.DemandId, series.CurrentDemandId, StringComparison.Ordinal));

            return new DemandSeriesSnapshot(
                series.SeriesId,
                series.WorkType,
                series.Sublot,
                series.Lifecycle,
                series.CurrentPresence,
                series.StartedAt,
                series.CreatedPollTraceId,
                series.CreatedProjectionCommitId,
                series.LatestProjectionCommitId,
                currentDemand,
                demands,
                observations,
                events,
                errorState.CurrentConditions,
                errorState.ErrorPeriods);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqlServerMesIngestSchema.EnsureAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static PreparedRound PrepareRound(
        MesTaskUnionRound round,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        ValidateRequiredText(round.PollTraceId, nameof(round.PollTraceId), 128);
        ValidateRequiredText(round.QueryVersion, nameof(round.QueryVersion), 128);
        ArgumentNullException.ThrowIfNull(round.Observations);
        if (round.CompletedAt < round.StartedAt)
        {
            throw new ArgumentException(
                "A round cannot complete before it starts.",
                nameof(round));
        }

        if (round.Outcome is not MesTaskUnionRoundOutcome.Success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedRound(
                Observations: [],
                MesTaskUnionRoundDigest.Compute(round.Observations));
        }

        var prepared = new List<PreparedObservation>(round.Observations.Count);
        for (var ordinal = 0; ordinal < round.Observations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = round.Observations[ordinal]
                ?? throw new ArgumentException("A round observation cannot be null.", nameof(round));
            var isAssigned = !string.IsNullOrWhiteSpace(observation.WorkType)
                && !string.IsNullOrWhiteSpace(observation.Sublot);
            ValidateOptionalText(observation.WorkType, nameof(observation.WorkType), 128);
            ValidateOptionalText(observation.Sublot, nameof(observation.Sublot), 256);

            var keyToken = isAssigned
                ? TransportDemandKeyIdentity.CreateToken(
                    observation.WorkType!,
                    observation.Sublot!)
                : null;
            prepared.Add(new PreparedObservation(ordinal, keyToken, observation));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedRound(prepared, MesTaskUnionRoundDigest.Compute(round.Observations));
    }

    private static IReadOnlyList<PreparedObservationGroup> PrepareAssignedGroups(PreparedRound prepared)
    {
        var byKey = new Dictionary<string, List<PreparedObservation>>(StringComparer.Ordinal);
        foreach (var item in prepared.Observations)
        {
            if (item.KeyToken is null)
            {
                continue;
            }

            if (!byKey.TryGetValue(item.KeyToken, out var observations))
            {
                observations = [];
                byKey.Add(item.KeyToken, observations);
            }
            observations.Add(item);
        }

        return byKey
            .Select(pair => new PreparedObservationGroup(pair.Key, pair.Value))
            .OrderBy(group => group.Observations[0].Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> PrepareWorkTypeMemberships(
        IReadOnlyList<PreparedObservationGroup> groups)
    {
        var bySublot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var observation = group.Observations[0].Observation;
            if (!bySublot.TryGetValue(observation.Sublot!, out var workTypes))
            {
                workTypes = new HashSet<string>(StringComparer.Ordinal);
                bySublot.Add(observation.Sublot!, workTypes);
            }

            workTypes.Add(observation.WorkType!);
        }

        return bySublot.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static async Task<PollTraceRow?> LoadExistingPollTraceForUpdateAsync(
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
                c.ProjectionCommitId,
                c.CommittedAt,
                c.HostSessionId,
                c.RestartPhaseBefore,
                c.RestartPhaseAfter,
                c.AbsenceAuthority
            FROM mesingest.PollTraces AS p WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN mesingest.ProjectionCommits AS c WITH (UPDLOCK, HOLDLOCK)
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

    private static bool IsSameAcceptedContent(
        PollTraceRow existing,
        MesTaskUnionRound round,
        string contentDigest) =>
        string.Equals(existing.QueryVersion, round.QueryVersion, StringComparison.Ordinal)
        && string.Equals(existing.Outcome, GetOutcome(round.Outcome), StringComparison.Ordinal)
        && existing.RowCount == round.Observations.Count
        && string.Equals(existing.ContentDigest, contentDigest, StringComparison.Ordinal);

    private static async Task<RoundCommitReceipt> ReadAcceptedReceiptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PollTraceRow existing,
        CancellationToken cancellationToken)
    {
        if (existing.ProjectionCommitId is null)
        {
            return new RoundCommitReceipt(
                existing.PollTraceId,
                ParseOutcome(existing.Outcome),
                ProjectionCommitId: null,
                SeriesIds: [],
                DemandIds: [],
                IsReplay: true);
        }

        var seriesIds = new List<string>();
        var demandIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT SeriesId, DemandId
                FROM mesingest.DemandRawObservations
                WHERE PollTraceId = @pollTraceId
                  AND SeriesId IS NOT NULL
                  AND DemandId IS NOT NULL
                ORDER BY Ordinal;
                """;
            AddNVarChar(command, "@pollTraceId", 128, existing.PollTraceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                seriesIds.Add(reader.GetString(0));
                demandIds.Add(reader.GetString(1));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT SeriesId, SubjectId
                FROM mesingest.DemandSeriesEvents
                WHERE PollTraceId = @pollTraceId
                  AND EventType = N'DEMAND_GONE'
                  AND SubjectId IS NOT NULL
                ORDER BY SeriesSequence, EventId;
                """;
            AddNVarChar(command, "@pollTraceId", 128, existing.PollTraceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                seriesIds.Add(reader.GetString(0));
                demandIds.Add(reader.GetString(1));
            }
        }

        return new RoundCommitReceipt(
            existing.PollTraceId,
            ParseOutcome(existing.Outcome),
            existing.ProjectionCommitId,
            StableDistinct(seriesIds),
            StableDistinct(demandIds),
            IsReplay: true);
    }

    private static IReadOnlyList<string> StableDistinct(IReadOnlyList<string> values)
    {
        var distinct = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }

        return distinct;
    }

    private static async Task<bool> IsFirstProjectionCommitAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT_BIG(*) FROM mesingest.ProjectionCommits;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task InsertPollTraceAndCommitAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string contentDigest,
        string projectionCommitId,
        string hostSessionId,
        string restartPhaseBefore,
        string restartPhaseAfter,
        bool absenceAuthority,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.PollTraces
                (PollTraceId, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
            VALUES
                (@pollTraceId, @queryVersion, N'SUCCESS', @startedAt, @completedAt, @rowCount, @contentDigest);

            INSERT INTO mesingest.ProjectionCommits
                (ProjectionCommitId, PollTraceId, CommittedAt, HostSessionId,
                 RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority)
            VALUES
                (@projectionCommitId, @pollTraceId, @completedAt, @hostSessionId,
                 @restartPhaseBefore, @restartPhaseAfter, @absenceAuthority);
            """;
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@queryVersion", 128, round.QueryVersion);
        AddDateTimeOffset(command, "@startedAt", round.StartedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt.ToUniversalTime());
        command.Parameters.Add("@rowCount", SqlDbType.Int).Value = round.Observations.Count;
        AddChar(command, "@contentDigest", 64, contentDigest);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@hostSessionId", 64, hostSessionId);
        AddNVarChar(command, "@restartPhaseBefore", 32, restartPhaseBefore);
        AddNVarChar(command, "@restartPhaseAfter", 32, restartPhaseAfter);
        command.Parameters.Add("@absenceAuthority", SqlDbType.Bit).Value = absenceAuthority;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPollTraceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string contentDigest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.PollTraces
                (PollTraceId, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
            VALUES
                (@pollTraceId, @queryVersion, @outcome, @startedAt, @completedAt, @rowCount, @contentDigest);
            """;
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@queryVersion", 128, round.QueryVersion);
        AddNVarChar(command, "@outcome", 16, GetOutcome(round.Outcome));
        AddDateTimeOffset(command, "@startedAt", round.StartedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt.ToUniversalTime());
        command.Parameters.Add("@rowCount", SqlDbType.Int).Value = round.Observations.Count;
        AddChar(command, "@contentDigest", 64, contentDigest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetOutcome(MesTaskUnionRoundOutcome outcome) => outcome switch
    {
        MesTaskUnionRoundOutcome.Success => SuccessOutcome,
        MesTaskUnionRoundOutcome.Failure => "FAILURE",
        MesTaskUnionRoundOutcome.Incomplete => "INCOMPLETE",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown round outcome."),
    };

    private static MesTaskUnionRoundOutcome ParseOutcome(string outcome) => outcome switch
    {
        SuccessOutcome => MesTaskUnionRoundOutcome.Success,
        "FAILURE" => MesTaskUnionRoundOutcome.Failure,
        "INCOMPLETE" => MesTaskUnionRoundOutcome.Incomplete,
        _ => throw new InvalidOperationException($"Stored round outcome '{outcome}' is not supported."),
    };

    private async Task<HostSessionRow> LoadHostSessionForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT HostSessionId, StartedAt, RestartPhase
            FROM mesingest.HostSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE HostSessionId = @hostSessionId AND IsCurrent = 1;
            """;
        AddNVarChar(command, "@hostSessionId", 64, _hostSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "This Host session is no longer current and cannot commit another MES round.");
        }

        return new HostSessionRow(
            reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            IsCurrent: true);
    }

    private async Task AdvanceRestartBarrierAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string phaseBefore,
        string phaseAfter,
        string? transitionEventCode,
        MesTaskUnionRound round,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(phaseBefore, phaseAfter, StringComparison.Ordinal))
        {
            return;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE mesingest.HostSessions
                SET RestartPhase = @phaseAfter
                WHERE HostSessionId = @hostSessionId
                  AND IsCurrent = 1
                  AND RestartPhase = @phaseBefore;
                """;
            AddNVarChar(command, "@phaseAfter", 32, phaseAfter);
            AddNVarChar(command, "@hostSessionId", 64, _hostSessionId);
            AddNVarChar(command, "@phaseBefore", 32, phaseBefore);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("RestartBarrier changed while committing a SUCCESS round.");
            }
        }

        if (transitionEventCode is null)
        {
            throw new InvalidOperationException(
                "A RestartBarrier phase transition must carry its stable lifecycle event code.");
        }

        await InsertAbsenceAuthorityEventAsync(
            connection,
            transaction,
            transitionEventCode,
            round.CompletedAt,
            round.PollTraceId,
            projectionCommitId,
            phaseBefore,
            phaseAfter,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertAbsenceAuthorityEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string eventType,
        DateTimeOffset occurredAt,
        string? pollTraceId,
        string? projectionCommitId,
        string phaseBefore,
        string phaseAfter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.AbsenceAuthorityEvents
                (EventId, HostSessionId, EventType, OccurredAt, PollTraceId,
                 ProjectionCommitId, PhaseBefore, PhaseAfter)
            VALUES
                (@eventId, @hostSessionId, @eventType, @occurredAt, @pollTraceId,
                 @projectionCommitId, @phaseBefore, @phaseAfter);
            """;
        AddNVarChar(command, "@eventId", 64, NewId());
        AddNVarChar(command, "@hostSessionId", 64, _hostSessionId);
        AddNVarChar(command, "@eventType", 128, eventType);
        AddDateTimeOffset(command, "@occurredAt", occurredAt);
        AddNullableNVarChar(command, "@pollTraceId", 128, pollTraceId);
        AddNullableNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@phaseBefore", 32, phaseBefore);
        AddNVarChar(command, "@phaseAfter", 32, phaseAfter);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CurrentProjectionRow?> LoadCurrentProjectionForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string keyToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                s.SeriesId,
                s.WorkType,
                s.Sublot,
                s.Lifecycle,
                s.CurrentPresence,
                s.CurrentDemandId,
                d.Generation,
                d.Status,
                d.DemandLastSeenAt,
                d.GoneConfirmedAt,
                d.Area,
                d.Eqp,
                d.Step,
                d.MesSourceDate,
                d.Package
            FROM mesingest.DemandSeries AS s WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN mesingest.TransportDemands AS d WITH (UPDLOCK, HOLDLOCK)
                ON d.DemandId = s.CurrentDemandId
            WHERE s.KeyToken = @keyToken;
            """;
        AddChar(command, "@keyToken", 64, keyToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(5))
        {
            throw new InvalidOperationException(
                "An existing DemandSeries has no current TransportDemand.");
        }

        return new CurrentProjectionRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            GetNullableDateTimeOffset(reader, 9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            GetNullableString(reader, 12),
            GetNullableDateTimeOffset(reader, 13),
            GetNullableString(reader, 14));
    }

    private static async Task<ProjectedIdentity> InsertFirstGenerationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        PreparedObservation item,
        MesTaskUnionObservation? liveObservation,
        CancellationToken cancellationToken)
    {
        var seriesId = NewId();
        var demandId = NewId();
        var observation = item.Observation;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO mesingest.DemandSeries
                    (SeriesId, KeyToken, WorkType, Sublot, Lifecycle, CurrentPresence,
                     StartedAt, CreatedPollTraceId, CreatedProjectionCommitId,
                     LatestProjectionCommitId, CurrentDemandId, LastSeriesSequence)
                VALUES
                    (@seriesId, @keyToken, @workType, @sublot, N'TRACKING', N'VISIBLE',
                     @occurredAt, @pollTraceId, @projectionCommitId,
                     @projectionCommitId, NULL, 0);

                INSERT INTO mesingest.TransportDemands
                    (DemandId, SeriesId, Generation, PredecessorDemandId, Status,
                     CreatedAt, DemandLastSeenAt, GoneConfirmedAt, CreatedPollTraceId,
                     CreatedProjectionCommitId, LatestProjectionCommitId,
                     LatestObservationProjectionCommitId,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, 1, NULL, N'VISIBLE',
                     @occurredAt, @occurredAt, NULL, @pollTraceId,
                     @projectionCommitId, @projectionCommitId, @projectionCommitId,
                     @area, @eqp, @step, @mesSourceDate, @package);

                UPDATE mesingest.DemandSeries
                SET CurrentDemandId = @demandId,
                    LastSeriesSequence = 2
                WHERE SeriesId = @seriesId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            AddChar(command, "@keyToken", 64, item.KeyToken!);
            AddNVarChar(command, "@workType", 128, observation.WorkType!);
            AddNVarChar(command, "@sublot", 256, observation.Sublot!);
            AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
            AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            AddNVarChar(command, "@demandId", 64, demandId);
            AddNullableNVarChar(command, "@area", -1, liveObservation?.Area);
            AddNullableNVarChar(command, "@eqp", -1, liveObservation?.Eqp);
            AddNullableNVarChar(command, "@step", -1, liveObservation?.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", liveObservation?.MesSourceDate);
            AddNullableNVarChar(command, "@package", -1, liveObservation?.Package);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertInitialEventAsync(
            connection,
            transaction,
            seriesId,
            sequence: 1,
            SeriesStartedEvent,
            subjectKind: "SERIES",
            subjectId: seriesId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                seriesId,
                observation.WorkType,
                observation.Sublot,
            }),
            cancellationToken).ConfigureAwait(false);
        await InsertInitialEventAsync(
            connection,
            transaction,
            seriesId,
            sequence: 2,
            DemandCreatedEvent,
            subjectKind: "DEMAND",
            subjectId: demandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                demandId,
                generation = 1,
            }),
            cancellationToken).ConfigureAwait(false);

        return new ProjectedIdentity(seriesId, demandId);
    }

    private static async Task<ProjectedIdentity> InsertSuccessorGenerationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionRound round,
        string projectionCommitId,
        PreparedObservation item,
        MesTaskUnionObservation? liveObservation,
        CancellationToken cancellationToken)
    {
        var demandId = NewId();
        var generation = checked(current.Generation + 1);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO mesingest.TransportDemands
                    (DemandId, SeriesId, Generation, PredecessorDemandId, Status,
                     CreatedAt, DemandLastSeenAt, GoneConfirmedAt, CreatedPollTraceId,
                     CreatedProjectionCommitId, LatestProjectionCommitId,
                     LatestObservationProjectionCommitId,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, @generation, @predecessorDemandId, N'VISIBLE',
                     @occurredAt, @occurredAt, NULL, @pollTraceId,
                     @projectionCommitId, @projectionCommitId, @projectionCommitId,
                     @area, @eqp, @step, @mesSourceDate, @package);

                UPDATE mesingest.DemandSeries
                SET CurrentDemandId = @demandId,
                    CurrentPresence = N'VISIBLE',
                    LatestProjectionCommitId = @projectionCommitId
                WHERE SeriesId = @seriesId
                  AND CurrentDemandId = @predecessorDemandId
                  AND Lifecycle = N'TRACKING';
                """;
            AddNVarChar(command, "@demandId", 64, demandId);
            AddNVarChar(command, "@seriesId", 64, current.SeriesId);
            command.Parameters.Add("@generation", SqlDbType.Int).Value = generation;
            AddNVarChar(command, "@predecessorDemandId", 64, current.DemandId);
            AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
            AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            AddNullableNVarChar(command, "@area", -1, liveObservation?.Area);
            AddNullableNVarChar(command, "@eqp", -1, liveObservation?.Eqp);
            AddNullableNVarChar(command, "@step", -1, liveObservation?.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", liveObservation?.MesSourceDate);
            AddNullableNVarChar(command, "@package", -1, liveObservation?.Package);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
            {
                throw new InvalidOperationException("The GONE Demand changed while creating its successor generation.");
            }
        }

        var sequence = await GetNextSeriesSequenceAsync(
            connection,
            transaction,
            current.SeriesId,
            cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            current.SeriesId,
            sequence,
            DemandCreatedEvent,
            "DEMAND",
            demandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                demandId,
                generation,
                predecessorDemandId = current.DemandId,
                reason = "PREARCHIVE_REAPPEARANCE",
            }),
            cancellationToken).ConfigureAwait(false);
        await SetLastSeriesSequenceAsync(
            connection,
            transaction,
            current.SeriesId,
            sequence,
            cancellationToken).ConfigureAwait(false);
        return new ProjectedIdentity(current.SeriesId, demandId);
    }

    private static void EnsureReappearanceCanAdvance(
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        DateTimeOffset completedAt)
    {
        if (!string.Equals(current.WorkType, observation.WorkType, StringComparison.Ordinal)
            || !string.Equals(current.Sublot, observation.Sublot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TransportDemandKey token collision detected while applying a reappearance.");
        }
        if (!string.Equals(current.Lifecycle, TrackingLifecycle, StringComparison.Ordinal)
            || !string.Equals(current.CurrentPresence, GonePresence, StringComparison.Ordinal)
            || !string.Equals(current.DemandStatus, GoneDemandStatus, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Only a tracking GONE Demand can reappear before archive.");
        }
        if (completedAt < current.DemandLastSeenAt)
        {
            throw new InvalidOperationException("A reappearance cannot predate the predecessor's last observation.");
        }
        if (current.GoneConfirmedAt is null || completedAt < current.GoneConfirmedAt.Value)
        {
            throw new InvalidOperationException("A reappearance cannot predate the predecessor's GONE confirmation.");
        }
    }

    private static Task InsertInitialEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long sequence,
        string eventType,
        string subjectKind,
        string subjectId,
        MesTaskUnionRound round,
        string projectionCommitId,
        string payloadJson,
        CancellationToken cancellationToken) =>
        InsertEventAsync(
            connection,
            transaction,
            seriesId,
            sequence,
            eventType,
            subjectKind,
            subjectId,
            round,
            projectionCommitId,
            payloadJson,
            cancellationToken);

    private static async Task<string> InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long sequence,
        string eventType,
        string subjectKind,
        string? subjectId,
        MesTaskUnionRound round,
        string projectionCommitId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var eventId = NewId();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.DemandSeriesEvents
                (EventId, SeriesId, SeriesSequence, EventType, OccurredAt,
                 SubjectKind, SubjectId, PollTraceId, ProjectionCommitId,
                 PayloadVersion, Payload)
            VALUES
                (@eventId, @seriesId, @seriesSequence, @eventType, @occurredAt,
                 @subjectKind, @subjectId, @pollTraceId, @projectionCommitId,
                 1, @payload);
            """;
        AddNVarChar(command, "@eventId", 64, eventId);
        AddNVarChar(command, "@seriesId", 64, seriesId);
        command.Parameters.Add("@seriesSequence", SqlDbType.BigInt).Value = sequence;
        AddNVarChar(command, "@eventType", 128, eventType);
        AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
        AddNVarChar(command, "@subjectKind", 64, subjectKind);
        AddNullableNVarChar(command, "@subjectId", 128, subjectId);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@payload", -1, payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return eventId;
    }

    private static void EnsureCurrentObservationCanAdvance(
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        DateTimeOffset completedAt)
    {
        if (!string.Equals(current.WorkType, observation.WorkType, StringComparison.Ordinal)
            || !string.Equals(current.Sublot, observation.Sublot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TransportDemandKey token collision detected while applying a round.");
        }

        if (!string.Equals(current.Lifecycle, TrackingLifecycle, StringComparison.Ordinal)
            || !string.Equals(current.CurrentPresence, VisiblePresence, StringComparison.Ordinal)
            || !string.Equals(current.DemandStatus, VisibleDemandStatus, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Lifecycle transitions are outside the ticket 01 tracer spine.");
        }

        if (completedAt < current.DemandLastSeenAt)
        {
            throw new InvalidOperationException(
                "A successful round cannot move DemandLastSeenAt backwards.");
        }

    }

    private static async Task AdvanceLiveObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        MesTaskUnionRound round,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        var changes = GetLiveFieldChanges(current, observation);
        var nextSequence = await GetNextSeriesSequenceAsync(
            connection,
            transaction,
            current.SeriesId,
            cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
        {
            await InsertEventAsync(
                connection,
                transaction,
                current.SeriesId,
                nextSequence++,
                "MES_FIELD_CHANGED",
                change.SubjectKind,
                current.DemandId,
                round,
                projectionCommitId,
                JsonSerializer.Serialize(new
                {
                    field = change.SubjectKind,
                    before = change.Before,
                    after = change.After,
                }),
                cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.DemandSeries
            SET LatestProjectionCommitId = @projectionCommitId,
                LastSeriesSequence = @lastSeriesSequence
            WHERE SeriesId = @seriesId;

            UPDATE mesingest.TransportDemands
            SET LatestProjectionCommitId = @projectionCommitId,
                LatestObservationProjectionCommitId = @projectionCommitId,
                DemandLastSeenAt = @completedAt,
                Area = @area,
                Eqp = @eqp,
                Step = @step,
                MesSourceDate = @mesSourceDate,
                Package = @package
            WHERE DemandId = @demandId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@seriesId", 64, current.SeriesId);
        AddNVarChar(command, "@demandId", 64, current.DemandId);
        command.Parameters.Add("@lastSeriesSequence", SqlDbType.BigInt).Value = nextSequence - 1;
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt);
        AddNullableNVarChar(command, "@area", -1, observation.Area);
        AddNullableNVarChar(command, "@eqp", -1, observation.Eqp);
        AddNullableNVarChar(command, "@step", -1, observation.Step);
        AddNullableDateTimeOffset(
            command,
            "@mesSourceDate",
            MesTaskUnionValueSemantics.SourceDatesEqual(current.MesSourceDate, observation.MesSourceDate)
                ? current.MesSourceDate
                : observation.MesSourceDate);
        AddNullableNVarChar(command, "@package", -1, observation.Package);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 2)
        {
            throw new InvalidOperationException(
                "The current DemandSeries projection changed while applying an equivalent observation.");
        }
    }

    private static async Task AdvanceConflictingObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionRound round,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.DemandSeries
            SET LatestProjectionCommitId = @projectionCommitId
            WHERE SeriesId = @seriesId;

            UPDATE mesingest.TransportDemands
            SET LatestProjectionCommitId = @projectionCommitId,
                LatestObservationProjectionCommitId = @projectionCommitId,
                DemandLastSeenAt = @completedAt
            WHERE DemandId = @demandId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@seriesId", 64, current.SeriesId);
        AddNVarChar(command, "@demandId", 64, current.DemandId);
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
        {
            throw new InvalidOperationException(
                "The current DemandSeries projection changed while applying conflicting observations.");
        }
    }

    private static IReadOnlyList<LiveFieldChange> GetLiveFieldChanges(
        CurrentProjectionRow current,
        MesTaskUnionObservation observation)
    {
        var changes = new List<LiveFieldChange>(5);
        AddStringChange(changes, "AREA", current.Area, observation.Area);
        AddStringChange(changes, "EQP", current.Eqp, observation.Eqp);
        AddStringChange(changes, "STEP", current.Step, observation.Step);
        if (!MesTaskUnionValueSemantics.SourceDatesEqual(current.MesSourceDate, observation.MesSourceDate))
        {
            changes.Add(new LiveFieldChange(
                "DATES",
                current.MesSourceDate,
                observation.MesSourceDate));
        }
        AddStringChange(changes, "PACKAGE", current.Package, observation.Package);
        return changes;
    }

    private static void AddStringChange(
        ICollection<LiveFieldChange> changes,
        string subjectKind,
        string? before,
        string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add(new LiveFieldChange(subjectKind, before, after));
        }
    }

    private static async Task SynchronizeDemandConditionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectedIdentity identity,
        IReadOnlyList<MesFieldValidationIssue> expectedIssues,
        Func<string, string?> observedValueForSubject,
        bool hasTrustworthyLiveFieldSet,
        MesTaskUnionRound round,
        string projectionCommitId,
        bool bootstrapRound,
        CancellationToken cancellationToken)
    {
        var expected = expectedIssues
            .ToDictionary(
                issue => new ConditionKey(issue.Code, issue.SubjectKind),
                issue => issue);
        var current = await LoadCurrentConditionsForUpdateAsync(
            connection,
            transaction,
            identity.SeriesId,
            identity.DemandId,
            cancellationToken).ConfigureAwait(false);

        var nextSequence = await GetNextSeriesSequenceAsync(
            connection,
            transaction,
            identity.SeriesId,
            cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var (key, existing) in current.OrderBy(pair => pair.Key, ConditionKeyComparer.Instance))
        {
            if (!hasTrustworthyLiveFieldSet && IsLiveFieldSubject(key.SubjectKind))
            {
                continue;
            }

            if (expected.TryGetValue(key, out var issue))
            {
                if (!string.Equals(existing.ObservedValue, issue.ObservedValue, StringComparison.Ordinal))
                {
                    await AppendConditionEvidenceAsync(
                        connection,
                        transaction,
                        identity,
                        existing.PeriodId,
                        key.SubjectKind,
                        existing.ExpectedRule,
                        issue.ObservedValue,
                        "CONDITION_EVIDENCE_CHANGED",
                        round,
                        projectionCommitId,
                        nextSequence++,
                        cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
                expected.Remove(key);
                continue;
            }

            await CloseConditionAsync(
                connection,
                transaction,
                identity,
                key.SubjectKind,
                existing,
                observedValueForSubject(key.SubjectKind),
                round,
                projectionCommitId,
                nextSequence++,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        foreach (var issue in expected.Values
                     .OrderBy(issue => GetFieldOrder(issue.SubjectKind))
                     .ThenBy(issue => issue.Code, StringComparer.Ordinal))
        {
            await OpenConditionAsync(
                connection,
                transaction,
                identity,
                issue,
                bootstrapRound ? "BOOTSTRAPPED_CURRENT_CONDITION" : "CONDITION_DETECTED",
                round,
                projectionCommitId,
                nextSequence++,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        if (changed)
        {
            await SetLastSeriesSequenceAsync(
                connection,
                transaction,
                identity.SeriesId,
                nextSequence - 1,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MarkAbsentVisibleDemandsGoneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlySet<string> presentKeyTokens,
        MesTaskUnionRound round,
        string projectionCommitId,
        ICollection<string> affectedSeriesIds,
        ICollection<string> affectedDemandIds,
        CancellationToken cancellationToken)
    {
        var visible = new List<AbsentDemandRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.KeyToken, s.SeriesId, d.DemandId, d.Generation, d.DemandLastSeenAt
                FROM mesingest.DemandSeries AS s WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN mesingest.TransportDemands AS d WITH (UPDLOCK, HOLDLOCK)
                    ON d.DemandId = s.CurrentDemandId
                WHERE s.Lifecycle = N'TRACKING'
                  AND s.CurrentPresence = N'VISIBLE'
                  AND d.Status = N'VISIBLE'
                ORDER BY s.SeriesId;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new AbsentDemandRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetFieldValue<DateTimeOffset>(4));
                if (!presentKeyTokens.Contains(row.KeyToken))
                {
                    visible.Add(row);
                }
            }
        }

        foreach (var absent in visible)
        {
            if (round.CompletedAt < absent.DemandLastSeenAt)
            {
                throw new InvalidOperationException(
                    "An authoritative absence cannot predate the Demand's last real observation.");
            }

            var nextSequence = await GetNextSeriesSequenceAsync(
                connection,
                transaction,
                absent.SeriesId,
                cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(
                connection,
                transaction,
                absent.SeriesId,
                nextSequence++,
                "DEMAND_GONE",
                "DEMAND",
                absent.DemandId,
                round,
                projectionCommitId,
                JsonSerializer.Serialize(new
                {
                    demandId = absent.DemandId,
                    absent.Generation,
                    demandLastSeenAt = absent.DemandLastSeenAt,
                    goneConfirmedAt = round.CompletedAt,
                }),
                cancellationToken).ConfigureAwait(false);

            nextSequence = await CloseDemandConditionsAsGoneAsync(
                connection,
                transaction,
                absent,
                round,
                projectionCommitId,
                nextSequence,
                cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE mesingest.TransportDemands
                    SET Status = N'GONE',
                        GoneConfirmedAt = @goneConfirmedAt,
                        LatestProjectionCommitId = @projectionCommitId
                    WHERE DemandId = @demandId AND Status = N'VISIBLE';

                    UPDATE mesingest.DemandSeries
                    SET CurrentPresence = N'GONE',
                        LatestProjectionCommitId = @projectionCommitId,
                        LastSeriesSequence = @lastSeriesSequence
                    WHERE SeriesId = @seriesId AND CurrentDemandId = @demandId;
                    """;
                AddDateTimeOffset(command, "@goneConfirmedAt", round.CompletedAt);
                AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
                AddNVarChar(command, "@demandId", 64, absent.DemandId);
                AddNVarChar(command, "@seriesId", 64, absent.SeriesId);
                command.Parameters.Add("@lastSeriesSequence", SqlDbType.BigInt).Value = nextSequence - 1;
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
                {
                    throw new InvalidOperationException("The absent Demand changed while marking it GONE.");
                }
            }

            affectedSeriesIds.Add(absent.SeriesId);
            affectedDemandIds.Add(absent.DemandId);
        }
    }

    private static async Task<long> CloseDemandConditionsAsGoneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AbsentDemandRow absent,
        MesTaskUnionRound round,
        string projectionCommitId,
        long nextSequence,
        CancellationToken cancellationToken)
    {
        var conditions = await LoadCurrentConditionsForUpdateAsync(
            connection,
            transaction,
            absent.SeriesId,
            absent.DemandId,
            cancellationToken).ConfigureAwait(false);
        foreach (var (key, existing) in conditions.OrderBy(pair => pair.Key, ConditionKeyComparer.Instance))
        {
            await CloseConditionAsGoneAsync(
                connection,
                transaction,
                absent.SeriesId,
                absent.DemandId,
                key.SubjectKind,
                existing,
                round,
                projectionCommitId,
                nextSequence++,
                cancellationToken).ConfigureAwait(false);
        }
        return nextSequence;
    }

    private static async Task CloseConditionAsGoneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        string demandId,
        string subjectKind,
        CurrentConditionRow existing,
        MesTaskUnionRound round,
        string projectionCommitId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var evidenceId = NewId();
        var eventId = await InsertEventAsync(
            connection,
            transaction,
            seriesId,
            sequence,
            "SERIES_ERROR_PERIOD_ENDED",
            subjectKind,
            demandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new { periodId = existing.PeriodId, endReason = "DEMAND_GONE" }),
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.SeriesErrorPeriodEvidence
                (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId,
                 ProjectionCommitId, DemandId, ObservedValue, ExpectedRule)
            VALUES
                (@evidenceId, @periodId, @eventId, N'DEMAND_GONE', @observedAt, @pollTraceId,
                 @projectionCommitId, @demandId, NULL, @expectedRule);

            DELETE FROM mesingest.DemandSeriesCurrentConditions WHERE PeriodId = @periodId;

            UPDATE mesingest.DemandSeriesErrorPeriods
            SET EndedAt = @observedAt, EndReason = N'DEMAND_GONE', ClosedEventId = @eventId
            WHERE PeriodId = @periodId AND EndedAt IS NULL;
            """;
        AddNVarChar(command, "@evidenceId", 64, evidenceId);
        AddNVarChar(command, "@periodId", 64, existing.PeriodId);
        AddNVarChar(command, "@eventId", 64, eventId);
        AddDateTimeOffset(command, "@observedAt", round.CompletedAt);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@demandId", 64, demandId);
        AddNVarChar(command, "@expectedRule", 256, existing.ExpectedRule);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 3)
        {
            throw new InvalidOperationException("The current Series error condition changed while ending it as GONE.");
        }
    }

    private static IReadOnlyList<MesFieldValidationIssue> EvaluateDemandConditions(
        PreparedObservationGroup group,
        IReadOnlyList<string> workTypeMembership)
    {
        var issues = new List<MesFieldValidationIssue>();
        if (group.Observations.Count == 1)
        {
            issues.AddRange(MesFieldValidation.Evaluate(group.Observations[0].Observation).Issues);
        }
        else
        {
            issues.Add(new MesFieldValidationIssue(
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "OBSERVATION_CONFLICT",
                "RAW_OBSERVATION_SET",
                CanonicalizeObservationMultiset(group.Observations.Select(item => item.Observation)),
                "EXACTLY_ONE_RAW_OBSERVATION_PER_TRANSPORT_DEMAND_KEY"));
        }

        if (workTypeMembership.Count > 1)
        {
            issues.Add(new MesFieldValidationIssue(
                "SUBLOT_MULTIPLE_WORK_TYPES",
                "OBSERVATION_CONFLICT",
                "WORK_TYPE_MEMBERSHIP",
                CanonicalizeWorkTypeMembership(workTypeMembership),
                "EXACTLY_ONE_WORK_TYPE_PER_SUBLOT"));
        }

        return issues;
    }

    private static string? GetObservedValue(
        PreparedObservationGroup group,
        IReadOnlyList<string> workTypeMembership,
        string subjectKind)
    {
        if (string.Equals(subjectKind, "RAW_OBSERVATION_SET", StringComparison.Ordinal))
        {
            return CanonicalizeObservationMultiset(group.Observations.Select(item => item.Observation));
        }

        if (string.Equals(subjectKind, "WORK_TYPE_MEMBERSHIP", StringComparison.Ordinal))
        {
            return CanonicalizeWorkTypeMembership(workTypeMembership);
        }

        return group.Observations.Count == 1
            ? GetObservedValue(group.Observations[0].Observation, subjectKind)
            : null;
    }

    private static string CanonicalizeObservationMultiset(
        IEnumerable<MesTaskUnionObservation> observations)
    {
        var canonicalRows = observations
            .Select(observation => new ConflictObservationEvidence(
                observation.WorkType,
                observation.Sublot,
                observation.Area,
                observation.Eqp,
                observation.Step,
                MesTaskUnionValueSemantics.NormalizeSourceDate(observation.MesSourceDate),
                observation.Package))
            .Select(row => new CanonicalEvidenceRow(JsonSerializer.Serialize(row), row))
            .OrderBy(row => row.SortKey, StringComparer.Ordinal)
            .Select(row => row.Value)
            .ToArray();
        return JsonSerializer.Serialize(canonicalRows);
    }

    private static string CanonicalizeWorkTypeMembership(IEnumerable<string> workTypes) =>
        JsonSerializer.Serialize(workTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static int GetFieldOrder(string subjectKind) => subjectKind switch
    {
        "AREA" => 0,
        "EQP" => 1,
        "STEP" => 2,
        "DATES" => 3,
        "PACKAGE" => 4,
        _ => int.MaxValue,
    };

    private static bool IsLiveFieldSubject(string subjectKind) =>
        GetFieldOrder(subjectKind) != int.MaxValue;

    private static async Task<long> GetNextSeriesSequenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LastSeriesSequence FROM mesingest.DemandSeries WITH (UPDLOCK, HOLDLOCK) WHERE SeriesId = @seriesId;";
        AddNVarChar(command, "@seriesId", 64, seriesId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("DemandSeries disappeared while allocating an event sequence.");
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) + 1;
    }

    private static async Task SetLastSeriesSequenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE mesingest.DemandSeries SET LastSeriesSequence = @sequence WHERE SeriesId = @seriesId;";
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("DemandSeries disappeared while publishing event sequence state.");
        }
    }

    private static async Task<Dictionary<ConditionKey, CurrentConditionRow>> LoadCurrentConditionsForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        string demandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.ErrorCode, c.SubjectKind, c.PeriodId, e.ObservedValue, e.ExpectedRule
            FROM mesingest.DemandSeriesCurrentConditions AS c WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN mesingest.SeriesErrorPeriodEvidence AS e
                ON e.EvidenceId = c.LatestEvidenceId
            WHERE c.SeriesId = @seriesId
              AND c.Target = @target;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        AddNVarChar(command, "@target", 128, $"DEMAND:{demandId}");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var conditions = new Dictionary<ConditionKey, CurrentConditionRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new ConditionKey(reader.GetString(0), reader.GetString(1));
            conditions.Add(
                key,
                new CurrentConditionRow(
                    reader.GetString(2),
                    GetNullableString(reader, 3),
                    reader.GetString(4)));
        }
        return conditions;
    }

    private static async Task OpenConditionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectedIdentity identity,
        MesFieldValidationIssue issue,
        string startReason,
        MesTaskUnionRound round,
        string projectionCommitId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var definition = SeriesErrorCatalog.GetRequired(issue.Code);
        var periodId = NewId();
        var evidenceId = NewId();
        var target = $"DEMAND:{identity.DemandId}";
        var eventId = await InsertEventAsync(
            connection,
            transaction,
            identity.SeriesId,
            sequence,
            "SERIES_ERROR_PERIOD_STARTED",
            issue.SubjectKind,
            identity.DemandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                periodId,
                code = issue.Code,
                category = definition.Category,
                target,
                issue.SubjectKind,
                startReason,
                observedValue = issue.ObservedValue,
                issue.ExpectedRule,
            }),
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.DemandSeriesErrorPeriods
                (PeriodId, SeriesId, ErrorCode, Category, Severity, Target, SubjectKind,
                 StartReason, StartedAt, EndedAt, EndReason, OpenedEventId, ClosedEventId)
            VALUES
                (@periodId, @seriesId, @errorCode, @category, @severity, @target, @subjectKind,
                 @startReason, @observedAt, NULL, NULL, @eventId, NULL);

            INSERT INTO mesingest.SeriesErrorPeriodEvidence
                (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId,
                 ProjectionCommitId, DemandId, ObservedValue, ExpectedRule)
            VALUES
                (@evidenceId, @periodId, @eventId, @evidenceKind, @observedAt, @pollTraceId,
                 @projectionCommitId, @demandId, @observedValue, @expectedRule);

            INSERT INTO mesingest.DemandSeriesCurrentConditions
                (SeriesId, ErrorCode, Target, SubjectKind, PeriodId, LatestEvidenceId)
            VALUES
                (@seriesId, @errorCode, @target, @subjectKind, @periodId, @evidenceId);
            """;
        AddNVarChar(command, "@periodId", 64, periodId);
        AddNVarChar(command, "@seriesId", 64, identity.SeriesId);
        AddNVarChar(command, "@errorCode", 128, issue.Code);
        AddNVarChar(command, "@category", 64, definition.Category);
        AddNVarChar(command, "@severity", 32, definition.Severity);
        AddNVarChar(command, "@target", 128, target);
        AddNVarChar(command, "@subjectKind", 64, issue.SubjectKind);
        AddNVarChar(command, "@startReason", 64, startReason);
        AddDateTimeOffset(command, "@observedAt", round.CompletedAt);
        AddNVarChar(command, "@eventId", 64, eventId);
        AddNVarChar(command, "@evidenceId", 64, evidenceId);
        AddNVarChar(command, "@evidenceKind", 64, startReason);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@demandId", 64, identity.DemandId);
        AddNullableNVarChar(command, "@observedValue", -1, issue.ObservedValue);
        AddNVarChar(command, "@expectedRule", 256, issue.ExpectedRule);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendConditionEvidenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectedIdentity identity,
        string periodId,
        string subjectKind,
        string expectedRule,
        string? observedValue,
        string evidenceKind,
        MesTaskUnionRound round,
        string projectionCommitId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var evidenceId = NewId();
        var eventId = await InsertEventAsync(
            connection,
            transaction,
            identity.SeriesId,
            sequence,
            "SERIES_ERROR_EVIDENCE_CHANGED",
            subjectKind,
            identity.DemandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new { periodId, evidenceKind, observedValue, expectedRule }),
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.SeriesErrorPeriodEvidence
                (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId,
                 ProjectionCommitId, DemandId, ObservedValue, ExpectedRule)
            VALUES
                (@evidenceId, @periodId, @eventId, @evidenceKind, @observedAt, @pollTraceId,
                 @projectionCommitId, @demandId, @observedValue, @expectedRule);

            UPDATE mesingest.DemandSeriesCurrentConditions
            SET LatestEvidenceId = @evidenceId
            WHERE PeriodId = @periodId;
            """;
        AddNVarChar(command, "@evidenceId", 64, evidenceId);
        AddNVarChar(command, "@periodId", 64, periodId);
        AddNVarChar(command, "@eventId", 64, eventId);
        AddNVarChar(command, "@evidenceKind", 64, evidenceKind);
        AddDateTimeOffset(command, "@observedAt", round.CompletedAt);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@demandId", 64, identity.DemandId);
        AddNullableNVarChar(command, "@observedValue", -1, observedValue);
        AddNVarChar(command, "@expectedRule", 256, expectedRule);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 2)
        {
            throw new InvalidOperationException("The current Series error condition changed while appending evidence.");
        }
    }

    private static async Task CloseConditionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectedIdentity identity,
        string subjectKind,
        CurrentConditionRow existing,
        string? observedValue,
        MesTaskUnionRound round,
        string projectionCommitId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var evidenceId = NewId();
        var eventId = await InsertEventAsync(
            connection,
            transaction,
            identity.SeriesId,
            sequence,
            "SERIES_ERROR_PERIOD_ENDED",
            subjectKind,
            identity.DemandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                periodId = existing.PeriodId,
                endReason = "CONDITION_CLEARED",
                observedValue,
            }),
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.SeriesErrorPeriodEvidence
                (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId,
                 ProjectionCommitId, DemandId, ObservedValue, ExpectedRule)
            VALUES
                (@evidenceId, @periodId, @eventId, N'CONDITION_CLEARED', @observedAt, @pollTraceId,
                 @projectionCommitId, @demandId, @observedValue, @expectedRule);

            DELETE FROM mesingest.DemandSeriesCurrentConditions
            WHERE PeriodId = @periodId;

            UPDATE mesingest.DemandSeriesErrorPeriods
            SET EndedAt = @observedAt,
                EndReason = N'CONDITION_CLEARED',
                ClosedEventId = @eventId
            WHERE PeriodId = @periodId AND EndedAt IS NULL;
            """;
        AddNVarChar(command, "@evidenceId", 64, evidenceId);
        AddNVarChar(command, "@periodId", 64, existing.PeriodId);
        AddNVarChar(command, "@eventId", 64, eventId);
        AddDateTimeOffset(command, "@observedAt", round.CompletedAt);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@demandId", 64, identity.DemandId);
        AddNullableNVarChar(command, "@observedValue", -1, observedValue);
        AddNVarChar(command, "@expectedRule", 256, existing.ExpectedRule);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 3)
        {
            throw new InvalidOperationException("The current Series error condition changed while closing its period.");
        }
    }

    private static string? GetObservedValue(MesTaskUnionObservation observation, string subjectKind) =>
        subjectKind switch
        {
            "AREA" => observation.Area,
            "EQP" => observation.Eqp,
            "STEP" => observation.Step,
            "DATES" => observation.MesSourceDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "PACKAGE" => observation.Package,
            _ => null,
        };

    private static async Task InsertRawObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string pollTraceId,
        string projectionCommitId,
        ProjectedIdentity? identity,
        PreparedObservation item,
        CancellationToken cancellationToken)
    {
        var observation = item.Observation;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.DemandRawObservations
                (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId,
                 WorkType, Sublot, Area, Eqp, Step, MesSourceDate, Package)
            VALUES
                (@pollTraceId, @ordinal, @projectionCommitId, @seriesId, @demandId,
                 @workType, @sublot, @area, @eqp, @step, @mesSourceDate, @package);
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        command.Parameters.Add("@ordinal", SqlDbType.Int).Value = item.Ordinal;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNullableNVarChar(command, "@seriesId", 64, identity?.SeriesId);
        AddNullableNVarChar(command, "@demandId", 64, identity?.DemandId);
        AddNullableNVarChar(command, "@workType", 128, observation.WorkType);
        AddNullableNVarChar(command, "@sublot", 256, observation.Sublot);
        AddNullableNVarChar(command, "@area", -1, observation.Area);
        AddNullableNVarChar(command, "@eqp", -1, observation.Eqp);
        AddNullableNVarChar(command, "@step", -1, observation.Step);
        AddNullableDateTimeOffset(command, "@mesSourceDate", observation.MesSourceDate);
        AddNullableNVarChar(command, "@package", -1, observation.Package);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TransportDemandSnapshot>> ReadDemandGenerationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        string currentDemandId,
        IReadOnlyList<DemandSeriesCurrentConditionSnapshot> currentConditions,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                d.DemandId,
                d.SeriesId,
                d.Generation,
                d.PredecessorDemandId,
                d.Status,
                d.CreatedAt,
                d.DemandLastSeenAt,
                d.GoneConfirmedAt,
                d.CreatedPollTraceId,
                d.CreatedProjectionCommitId,
                d.LatestProjectionCommitId,
                d.LatestObservationProjectionCommitId,
                d.Area,
                d.Eqp,
                d.Step,
                d.MesSourceDate,
                d.Package,
                (SELECT COUNT_BIG(*)
                 FROM mesingest.DemandRawObservations AS latestObservation
                 WHERE latestObservation.DemandId = d.DemandId
                   AND latestObservation.ProjectionCommitId =
                       d.LatestObservationProjectionCommitId)
            FROM mesingest.TransportDemands AS d
            WHERE d.SeriesId = @seriesId
            ORDER BY d.Generation;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var demands = new List<TransportDemandSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var demandId = reader.GetString(0);
            var status = reader.GetString(4);
            var isCurrent = string.Equals(demandId, currentDemandId, StringComparison.Ordinal);
            var latestObservationCount = reader.GetInt64(17);
            var blockers = isCurrent
                ? currentConditions
                    .Where(condition => string.Equals(condition.DemandId, demandId, StringComparison.Ordinal))
                    .Select(condition => condition.Code)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];
            if (string.Equals(status, GoneDemandStatus, StringComparison.Ordinal))
            {
                blockers.Add("DEMAND_GONE");
            }

            demands.Add(new TransportDemandSnapshot(
                demandId,
                reader.GetString(1),
                reader.GetInt32(2),
                GetNullableString(reader, 3),
                status,
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                GetNullableDateTimeOffset(reader, 7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                latestObservationCount == 1
                    ? new LiveMesFieldSetSnapshot(
                        GetNullableString(reader, 12),
                        GetNullableString(reader, 13),
                        GetNullableString(reader, 14),
                        GetNullableDateTimeOffset(reader, 15),
                        GetNullableString(reader, 16))
                    : null,
                blockers.Count == 0 ? "READABLE" : "NOT_READABLE",
                blockers));
        }

        return demands;
    }

    private static async Task<IReadOnlyList<DemandRawObservationSnapshot>> ReadRawObservationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string predicate,
        string identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                o.Ordinal,
                o.PollTraceId,
                o.ProjectionCommitId,
                o.SeriesId,
                o.DemandId,
                o.WorkType,
                o.Sublot,
                o.Area,
                o.Eqp,
                o.Step,
                o.MesSourceDate,
                o.Package
            FROM mesingest.DemandRawObservations AS o
            INNER JOIN mesingest.PollTraces AS p
                ON p.PollTraceId = o.PollTraceId
            WHERE {predicate}
            ORDER BY p.CompletedAt, o.PollTraceId, o.Ordinal;
            """;
        AddNVarChar(command, "@identity", 256, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var observations = new List<DemandRawObservationSnapshot>();
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
                GetNullableString(reader, 11)));
        }

        return observations;
    }

    private static async Task<IReadOnlyList<DemandSeriesEventSnapshot>> ReadEventsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                EventId,
                SeriesId,
                SeriesSequence,
                EventType,
                OccurredAt,
                SubjectKind,
                SubjectId,
                PollTraceId,
                ProjectionCommitId,
                PayloadVersion,
                Payload
            FROM mesingest.DemandSeriesEvents
            WHERE SeriesId = @seriesId
            ORDER BY SeriesSequence;
            """;
        AddNVarChar(command, "@seriesId", 64, seriesId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var events = new List<DemandSeriesEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new DemandSeriesEventSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10)));
        }

        return events;
    }

    private static async Task<ErrorStateSnapshot> ReadErrorStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var currentConditions = new List<DemandSeriesCurrentConditionSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    p.PeriodId, p.ErrorCode, p.Category, p.Severity, p.Target, p.SubjectKind,
                    p.StartedAt, e.ObservedAt, e.PollTraceId, e.ProjectionCommitId,
                    e.DemandId, e.ObservedValue, e.ExpectedRule
                FROM mesingest.DemandSeriesCurrentConditions AS c
                INNER JOIN mesingest.DemandSeriesErrorPeriods AS p ON p.PeriodId = c.PeriodId
                INNER JOIN mesingest.SeriesErrorPeriodEvidence AS e ON e.EvidenceId = c.LatestEvidenceId
                WHERE c.SeriesId = @seriesId
                ORDER BY p.StartedAt, p.PeriodId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currentConditions.Add(new DemandSeriesCurrentConditionSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    GetNullableString(reader, 11),
                    reader.GetString(12)));
            }
        }

        var periodRows = new List<ErrorPeriodRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    PeriodId, ErrorCode, Category, Severity, Target, SubjectKind,
                    StartReason, StartedAt, EndedAt, EndReason
                FROM mesingest.DemandSeriesErrorPeriods
                WHERE SeriesId = @seriesId
                ORDER BY StartedAt, PeriodId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                periodRows.Add(new ErrorPeriodRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    GetNullableDateTimeOffset(reader, 8),
                    GetNullableString(reader, 9)));
            }
        }

        var evidenceByPeriod = periodRows.ToDictionary(
            period => period.PeriodId,
            _ => new List<SeriesErrorPeriodEvidenceSnapshot>(),
            StringComparer.Ordinal);
        if (periodRows.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    e.PeriodId, e.EvidenceId, e.EvidenceKind, e.ObservedAt,
                    e.PollTraceId, e.ProjectionCommitId, e.DemandId,
                    e.ObservedValue, e.ExpectedRule
                FROM mesingest.SeriesErrorPeriodEvidence AS e
                INNER JOIN mesingest.DemandSeriesErrorPeriods AS p ON p.PeriodId = e.PeriodId
                WHERE p.SeriesId = @seriesId
                ORDER BY e.ObservedAt, e.EvidenceId;
                """;
            AddNVarChar(command, "@seriesId", 64, seriesId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                evidenceByPeriod[reader.GetString(0)].Add(new SeriesErrorPeriodEvidenceSnapshot(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    GetNullableString(reader, 7),
                    reader.GetString(8)));
            }
        }

        return new ErrorStateSnapshot(
            currentConditions,
            periodRows.Select(period => new DemandSeriesErrorPeriodSnapshot(
                    period.PeriodId,
                    period.Code,
                    period.Category,
                    period.Severity,
                    period.Target,
                    period.SubjectKind,
                    period.StartReason,
                    period.StartedAt,
                    period.EndedAt,
                    period.EndReason,
                    evidenceByPeriod[period.PeriodId]))
                .ToArray());
    }

    private static DemandSeriesRow ReadDemandSeriesRow(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9));

    private static PollTraceRow ReadPollTraceRow(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt32(5),
            reader.GetString(6),
            GetNullableString(reader, 7),
            GetNullableDateTimeOffset(reader, 8),
            GetNullableString(reader, 9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetBoolean(12));

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNVarChar(
        SqlCommand command,
        string name,
        int size,
        string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableNVarChar(
        SqlCommand command,
        string name,
        int size,
        string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value =
            value is null ? DBNull.Value : value;

    private static void AddChar(
        SqlCommand command,
        string name,
        int size,
        string value) =>
        command.Parameters.Add(name, SqlDbType.Char, size).Value = value;

    private static void AddDateTimeOffset(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value = value;

    private static void AddNullableDateTimeOffset(
        SqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, SqlDbType.DateTimeOffset).Value =
            value is null ? DBNull.Value : value.Value;

    private static void ValidateRequiredText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value exceeds the SQL contract maximum of {maxLength} characters.");
        }
    }

    private static void ValidateOptionalText(string? value, string parameterName, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value exceeds the SQL contract maximum of {maxLength} characters.");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed record PreparedRound(
        IReadOnlyList<PreparedObservation> Observations,
        string ContentDigest);

    private sealed record PreparedObservation(
        int Ordinal,
        string? KeyToken,
        MesTaskUnionObservation Observation);

    private sealed record PreparedObservationGroup(
        string KeyToken,
        IReadOnlyList<PreparedObservation> Observations);

    private sealed record ConflictObservationEvidence(
        [property: JsonPropertyName("workType")] string? WorkType,
        [property: JsonPropertyName("sublot")] string? Sublot,
        [property: JsonPropertyName("area")] string? Area,
        [property: JsonPropertyName("eqp")] string? Eqp,
        [property: JsonPropertyName("step")] string? Step,
        [property: JsonPropertyName("mesSourceDate")] DateTimeOffset? MesSourceDate,
        [property: JsonPropertyName("package")] string? Package);

    private sealed record CanonicalEvidenceRow(
        string SortKey,
        ConflictObservationEvidence Value);

    private sealed record ProjectedIdentity(string SeriesId, string DemandId);

    private sealed record LiveFieldChange(string SubjectKind, object? Before, object? After);

    private readonly record struct ConditionKey(string Code, string SubjectKind);

    private sealed class ConditionKeyComparer : IComparer<ConditionKey>
    {
        public static ConditionKeyComparer Instance { get; } = new();

        public int Compare(ConditionKey x, ConditionKey y)
        {
            var fieldComparison = GetFieldOrder(x.SubjectKind).CompareTo(GetFieldOrder(y.SubjectKind));
            return fieldComparison != 0
                ? fieldComparison
                : string.Compare(x.Code, y.Code, StringComparison.Ordinal);
        }
    }

    private sealed record CurrentConditionRow(
        string PeriodId,
        string? ObservedValue,
        string ExpectedRule);

    private sealed record ErrorPeriodRow(
        string PeriodId,
        string Code,
        string Category,
        string Severity,
        string Target,
        string SubjectKind,
        string StartReason,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string? EndReason);

    private sealed record ErrorStateSnapshot(
        IReadOnlyList<DemandSeriesCurrentConditionSnapshot> CurrentConditions,
        IReadOnlyList<DemandSeriesErrorPeriodSnapshot> ErrorPeriods);

    private sealed record CurrentProjectionRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        string Lifecycle,
        string CurrentPresence,
        string DemandId,
        int Generation,
        string DemandStatus,
        DateTimeOffset DemandLastSeenAt,
        DateTimeOffset? GoneConfirmedAt,
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);

    private sealed record DemandSeriesRow(
        string SeriesId,
        string WorkType,
        string Sublot,
        string Lifecycle,
        string CurrentPresence,
        DateTimeOffset StartedAt,
        string CreatedPollTraceId,
        string CreatedProjectionCommitId,
        string LatestProjectionCommitId,
        string CurrentDemandId);

    private sealed record PollTraceRow(
        string PollTraceId,
        string QueryVersion,
        string Outcome,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        int RowCount,
        string ContentDigest,
        string? ProjectionCommitId,
        DateTimeOffset? CommittedAt,
        string? HostSessionId,
        string? RestartPhaseBefore,
        string? RestartPhaseAfter,
        bool? AbsenceAuthority);

    private sealed record HostSessionRow(
        string HostSessionId,
        DateTimeOffset StartedAt,
        string RestartPhase,
        bool IsCurrent);

    private sealed record AbsentDemandRow(
        string KeyToken,
        string SeriesId,
        string DemandId,
        int Generation,
        DateTimeOffset DemandLastSeenAt);
}
