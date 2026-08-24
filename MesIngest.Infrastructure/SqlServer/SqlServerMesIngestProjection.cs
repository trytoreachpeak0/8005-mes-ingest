using System.Data;
using System.Globalization;
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
public sealed partial class SqlServerMesIngestProjection : IMesIngestProjection
{
    private const string SuccessOutcome = "SUCCESS";
    private const string TrackingLifecycle = DemandSeriesLifecycleContract.Tracking;
    private const string ArchivedLifecycle = DemandSeriesLifecycleContract.Archived;
    private const string VisiblePresence = DemandSeriesLifecycleContract.Visible;
    private const string GonePresence = DemandSeriesLifecycleContract.Gone;
    private const string LongGoneButVisiblePresence = DemandSeriesLifecycleContract.LongGoneButVisible;
    private const string VisibleDemandStatus = DemandSeriesLifecycleContract.Visible;
    private const string GoneDemandStatus = DemandSeriesLifecycleContract.Gone;
    private const string LongGoneButVisibleDemandStatus = DemandSeriesLifecycleContract.LongGoneButVisible;
    private const string SeriesStartedEvent = "DEMAND_SERIES_STARTED";
    private const string DemandCreatedEvent = "TRANSPORT_DEMAND_CREATED";
    private const string SeriesArchivedEvent = DemandSeriesLifecycleContract.GoneTimeoutArchivedEvent;
    private const string LongGoneButVisibleError = DemandSeriesLifecycleContract.LongGoneButVisible;
    private const string ArchivedSeriesVisibilitySubject = DemandSeriesLifecycleContract.ArchivedSeriesVisibilitySubject;
    private const string PostarchiveReappearanceReason = DemandSeriesLifecycleContract.PostarchiveReappearanceReason;
    private const string SeriesArchivedBlocker = DemandSeriesLifecycleContract.SeriesArchivedBlocker;
    private const int CurrentMesFieldMaximumLength = 512;
    private const int RawMesSourceDateMaximumLength = 128;
    private const int SqlFilterJsonMaximumLength = 4000;

    private readonly string _connectionString;
    private readonly int _zeroDropEnterThreshold;
    private readonly TimeProvider _timeProvider;
    private readonly IWatchOverviewReadBoundaryObserver _overviewReadBoundaryObserver;
    private readonly IProjectionCommitCheckpointObserver _checkpointObserver;
    private readonly IProjectionReadBoundaryObserver _readBoundaryObserver;
    private readonly HistoryEpochBootstrapIntent? _historyEpochBootstrapIntent;
    private readonly string _hostSessionId = NewId();
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SemaphoreSlim _hostSessionGate = new(1, 1);
    private volatile bool _schemaEnsured;
    private volatile bool _hostSessionInitialized;
    private HistoryEpoch _historyEpoch = null!;

    public SqlServerMesIngestProjection(
        string connectionString,
        int zeroDropEnterThreshold = 10,
        TimeProvider? timeProvider = null,
        IWatchOverviewReadBoundaryObserver? overviewReadBoundaryObserver = null,
        IProjectionCommitCheckpointObserver? checkpointObserver = null,
        IProjectionReadBoundaryObserver? readBoundaryObserver = null,
        HistoryEpochBootstrapIntent? historyEpochBootstrapIntent = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A dedicated new-MesIngest SQL Server connection string is required.",
                nameof(connectionString));
        }

        if (zeroDropEnterThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroDropEnterThreshold),
                zeroDropEnterThreshold,
                "The TaskTypeProtection zero-drop threshold must be positive.");
        }

        _connectionString = connectionString;
        _zeroDropEnterThreshold = zeroDropEnterThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _overviewReadBoundaryObserver = overviewReadBoundaryObserver
            ?? NoopWatchOverviewReadBoundaryObserver.Instance;
        _checkpointObserver = checkpointObserver
            ?? NoopProjectionCommitCheckpointObserver.Instance;
        _readBoundaryObserver = readBoundaryObserver
            ?? NoopProjectionReadBoundaryObserver.Instance;
        _historyEpochBootstrapIntent = historyEpochBootstrapIntent;
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

                await using (var protectionCommand = connection.CreateCommand())
                {
                    protectionCommand.Transaction = transaction;
                    protectionCommand.CommandText = """
                        UPDATE mesingest.TaskTypeProtectionStates
                        SET EffectiveAbsenceAuthorityAvailable = 0
                        WHERE EffectiveAbsenceAuthorityAvailable = 1;
                        """;
                    await protectionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

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
            await AcquireCommitRoundOrderLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
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
                    _historyEpoch,
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
                await UpdateCurrentOverviewActivityAsync(
                    connection,
                    transaction,
                    round,
                    projectionCommitId: null,
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
                _historyEpoch,
                cancellationToken).ConfigureAwait(false);
            var checkpointContext = new ProjectionCommitCheckpointContext(
                round.PollTraceId,
                projectionCommitId,
                _historyEpoch);
            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.RoundEvidencePersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var bootstrapRound = await IsFirstProjectionCommitAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var groups = PrepareAssignedGroups(prepared);
            var protectionDecisions = await ApplyTaskTypeProtectionAsync(
                connection,
                transaction,
                groups,
                round,
                projectionCommitId,
                restartTransition.AbsenceAuthority,
                _zeroDropEnterThreshold,
                cancellationToken).ConfigureAwait(false);
            var effectiveAuthorityByWorkType = protectionDecisions.ToDictionary(
                decision => decision.WorkType,
                decision => decision.EffectiveAbsenceAuthorityAvailable,
                StringComparer.Ordinal);

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

            await ReconcileUnassignedObservationAttentionAsync(
                connection,
                transaction,
                round,
                projectionCommitId,
                prepared.UnassignedObservations,
                cancellationToken).ConfigureAwait(false);
            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.ProtectionAndUnassignedPersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

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
                var longGoneButVisible = false;
                var demandRevisionAdvancedThisRound = true;
                if (current is null)
                {
                    identity = await InsertFirstGenerationAsync(
                        connection,
                        transaction,
                        round,
                        projectionCommitId,
                        group.Observations[0],
                        uniqueObservation,
                        group.Observations.Count,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (string.Equals(current.DemandStatus, GoneDemandStatus, StringComparison.Ordinal))
                {
                    if (string.Equals(current.Lifecycle, ArchivedLifecycle, StringComparison.Ordinal))
                    {
                        EnsurePostarchiveReappearanceCanAdvance(
                            current,
                            identityObservation,
                            round.CompletedAt);
                        identity = await InsertPostarchiveSuccessorGenerationAsync(
                            connection,
                            transaction,
                            current,
                            round,
                            projectionCommitId,
                            uniqueObservation,
                            group.Observations.Count,
                            cancellationToken).ConfigureAwait(false);
                        longGoneButVisible = true;
                    }
                    else
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
                            group.Observations.Count,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    EnsureCurrentObservationCanAdvance(current, identityObservation, round.CompletedAt);
                    longGoneButVisible = string.Equals(
                        current.Lifecycle,
                        ArchivedLifecycle,
                        StringComparison.Ordinal);
                    if (uniqueObservation is null)
                    {
                        demandRevisionAdvancedThisRound = await AdvanceConflictingObservationAsync(
                            connection,
                            transaction,
                            current,
                            round,
                            projectionCommitId,
                            group.Observations.Count,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        demandRevisionAdvancedThisRound = await AdvanceLiveObservationAsync(
                            connection,
                            transaction,
                            current,
                            uniqueObservation,
                            round,
                            projectionCommitId,
                            group.Observations.Count,
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
                    demandRevisionAdvancedThisRound,
                    cancellationToken).ConfigureAwait(false);

                if (longGoneButVisible)
                {
                    await EnsureLongGoneButVisibleConditionAsync(
                        connection,
                        transaction,
                        identity,
                        round,
                        projectionCommitId,
                        cancellationToken).ConfigureAwait(false);
                }

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

            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.DemandProjectionPersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (effectiveAuthorityByWorkType.Values.Any(value => value))
            {
                await MarkAbsentVisibleDemandsGoneAsync(
                    connection,
                    transaction,
                    groups.Select(group => group.KeyToken).ToHashSet(StringComparer.Ordinal),
                    effectiveAuthorityByWorkType,
                    round,
                    projectionCommitId,
                    seriesIds,
                    demandIds,
                    cancellationToken).ConfigureAwait(false);
            }

            await ArchiveOverdueGoneSeriesAsync(
                connection,
                transaction,
                round,
                projectionCommitId,
                effectiveAuthorityByWorkType,
                seriesIds,
                demandIds,
                cancellationToken).ConfigureAwait(false);
            await RefreshSeriesRetentionEligibilityAsync(
                connection,
                transaction,
                round.CompletedAt,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await UpdateOverviewErrorSummaryAsync(
                connection,
                transaction,
                projectionCommitId,
                round.CompletedAt,
                cancellationToken).ConfigureAwait(false);
            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.AbsenceAndArchivePersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await ReconcileExternallyReadableDemandCatalogAsync(
                connection,
                transaction,
                round,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await UpdateCurrentOverviewReadabilityAsync(
                connection,
                transaction,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.CatalogPersisted,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await AdvanceRestartBarrierAsync(
                connection,
                transaction,
                hostSession.RestartPhase,
                restartPhaseAfter,
                restartTransition.EventCode,
                round,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await UpdateCurrentOverviewActivityAsync(
                connection,
                transaction,
                round,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);

            var projectionSequence = await ReadProjectionSequenceAsync(
                connection,
                transaction,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            await _checkpointObserver.OnCheckpointAsync(
                ProjectionCommitCheckpoint.BeforeCommit,
                checkpointContext,
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RoundCommitReceipt(
                round.PollTraceId,
                round.Outcome,
                projectionCommitId,
                StableDistinct(seriesIds),
                StableDistinct(demandIds),
                IsReplay: false,
                projectionSequence,
                _historyEpoch);
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task AcquireCommitRoundOrderLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken) =>
        await AcquireCommitRoundLockAsync(
            connection,
            transaction,
            lockMode: "Exclusive",
            cancellationToken).ConfigureAwait(false);

    private static async Task AcquireCommitRoundReadFenceLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken) =>
        await AcquireCommitRoundLockAsync(
            connection,
            transaction,
            lockMode: "Shared",
            cancellationToken).ConfigureAwait(false);

    private static async Task AcquireCommitRoundLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string lockMode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result INT;
            EXEC @result = sys.sp_getapplock
                @Resource = N'mesingest.CommitRoundOrder.v16',
                @LockMode = @lockMode,
                @LockOwner = N'Transaction',
                @LockTimeout = 30000;
            SELECT @result;
            """;
        command.Parameters.Add("@lockMode", SqlDbType.NVarChar, 32).Value = lockMode;
        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (result < 0)
        {
            throw new TimeoutException(
                $"Unable to acquire the MES ingest commit-order {lockMode} lock "
                + $"(sp_getapplock={result}).");
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
            "series.KeyToken = @identity",
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
            "series.SeriesId = @identity",
            seriesId,
            expectedWorkType: null,
            expectedSublot: null,
            cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<TaskTypeProtectionSnapshot>> ListTaskTypeProtectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await BeginHostSessionAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = await ReadTaskTypeProtectionsAsync(
                connection,
                transaction,
                workType: null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshots;
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TaskTypeProtectionSnapshot?> GetTaskTypeProtectionAsync(
        string workType,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(workType, nameof(workType), 128);
        await BeginHostSessionAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = await ReadTaskTypeProtectionsAsync(
                connection,
                transaction,
                workType,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshots.SingleOrDefault();
        }
        catch (Exception exception)
        {
            await transaction.RollbackBestEffortAsync(exception).ConfigureAwait(false);
            throw;
        }
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
            HostSessionRow? hostSession = null;
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
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    hostSession = new HostSessionRow(
                        reader.GetString(0),
                        reader.GetFieldValue<DateTimeOffset>(1),
                        reader.GetString(2),
                        reader.GetBoolean(3));
                }
            }

            if (hostSession is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
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

    private static async Task<IReadOnlyList<TaskTypeProtectionSnapshot>> ReadTaskTypeProtectionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? workType,
        CancellationToken cancellationToken)
    {
        var states = new List<TaskTypeProtectionSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT WorkType, Phase, LastHealthyNonZeroCount, LatestObservedCount,
                       RecoveryStreak, EnterThreshold, EpisodeId, EnteredAt,
                       ProtectionAllowsAbsenceAuthority,
                       EffectiveAbsenceAuthorityAvailable,
                       LatestPollTraceId, LatestProjectionCommitId
                FROM mesingest.TaskTypeProtectionStates
                WHERE @workType IS NULL OR WorkType = @workType
                ORDER BY WorkType;
                """;
            AddNullableNVarChar(command, "@workType", 128, workType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                states.Add(new TaskTypeProtectionSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    !string.Equals(
                        reader.GetString(1),
                        TaskTypeProtectionPhaseContract.Monitoring,
                        StringComparison.Ordinal),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    TaskTypeProtectionPolicy.RequiredRecoveryStreak,
                    reader.GetInt32(5),
                    GetNullableString(reader, 6),
                    GetNullableDateTimeOffset(reader, 7),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    Events: []));
            }
        }

        if (states.Count == 0)
        {
            return states;
        }

        var eventsByWorkType = states.ToDictionary(
            state => state.WorkType,
            _ => new List<TaskTypeProtectionEventSnapshot>(),
            StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EventId, EpisodeId, WorkType, WorkTypeSequence, EventType,
                       OccurredAt, PollTraceId, ProjectionCommitId, PhaseBefore,
                       PhaseAfter, ObservedCount, LastHealthyNonZeroCount,
                       RecoveryStreak, RequiredRecoveryStreak, EnterThreshold
                FROM mesingest.TaskTypeProtectionEvents
                WHERE @workType IS NULL OR WorkType = @workType
                ORDER BY WorkType, WorkTypeSequence, EventId;
                """;
            AddNullableNVarChar(command, "@workType", 128, workType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var snapshot = ReadTaskTypeProtectionEvent(reader);
                eventsByWorkType[snapshot.WorkType].Add(snapshot);
            }
        }

        return states.Select(state => state with
        {
            Events = eventsByWorkType[state.WorkType],
        }).ToArray();
    }

    private static async Task<IReadOnlyList<TaskTypeProtectionDecisionSnapshot>> ReadTaskTypeProtectionDecisionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        var eventIdsByWorkType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT WorkType, EventId
                FROM mesingest.TaskTypeProtectionEvents
                WHERE ProjectionCommitId = @projectionCommitId
                ORDER BY WorkType, WorkTypeSequence, EventId;
                """;
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var eventWorkType = reader.GetString(0);
                if (!eventIdsByWorkType.TryGetValue(eventWorkType, out var eventIds))
                {
                    eventIds = [];
                    eventIdsByWorkType.Add(eventWorkType, eventIds);
                }
                eventIds.Add(reader.GetString(1));
            }
        }

        var decisions = new List<TaskTypeProtectionDecisionSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT WorkType, PhaseBefore, PhaseAfter, ObservedCount,
                       LastHealthyNonZeroCount, RecoveryStreakBefore,
                       RecoveryStreakAfter, ProtectionAllowsAbsenceAuthority,
                       EffectiveAbsenceAuthorityAvailable
                FROM mesingest.ProjectionCommitTaskTypeProtectionDecisions
                WHERE ProjectionCommitId = @projectionCommitId
                ORDER BY WorkType;
                """;
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var decisionWorkType = reader.GetString(0);
                decisions.Add(new TaskTypeProtectionDecisionSnapshot(
                    decisionWorkType,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8),
                    eventIdsByWorkType.GetValueOrDefault(decisionWorkType) ?? []));
            }
        }

        return decisions;
    }

    private static TaskTypeProtectionEventSnapshot ReadTaskTypeProtectionEvent(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14));

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
            await AcquireCommitRoundReadFenceLockAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentDemandSeriesDetailAsync(
                connection,
                transaction,
                predicate,
                identity,
                expectedWorkType,
                expectedSublot,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
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
            _historyEpoch = await SqlServerMesIngestSchema.EnsureAsync(
                    connection,
                    _historyEpochBootstrapIntent,
                    cancellationToken)
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

        if (round.Outcome is MesTaskUnionRoundOutcome.Success && round.Diagnostic is not null)
        {
            throw new ArgumentException(
                "A successful round cannot carry a failure diagnostic.",
                nameof(round));
        }
        if (round.Diagnostic is not null)
        {
            ValidateRequiredText(round.Diagnostic.Stage, nameof(round.Diagnostic.Stage), 64);
            ValidateRequiredText(round.Diagnostic.Code, nameof(round.Diagnostic.Code), 128);
            ValidateRequiredText(round.Diagnostic.SafeDetail, nameof(round.Diagnostic.SafeDetail), 512);
        }

        if (round.Outcome is not MesTaskUnionRoundOutcome.Success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedRound(
                Observations: [],
                MesTaskUnionRoundDigest.Compute(round.Observations),
                UnassignedObservations: UnassignedObservationFact.Empty);
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
            ValidateOptionalText(
                observation.Area,
                nameof(observation.Area),
                CurrentMesFieldMaximumLength);
            ValidateOptionalText(
                observation.Eqp,
                nameof(observation.Eqp),
                CurrentMesFieldMaximumLength);
            ValidateOptionalText(
                observation.Step,
                nameof(observation.Step),
                CurrentMesFieldMaximumLength);
            ValidateOptionalText(
                observation.Package,
                nameof(observation.Package),
                CurrentMesFieldMaximumLength);
            ValidateOptionalText(
                observation.MesSourceDateRaw,
                nameof(observation.MesSourceDateRaw),
                RawMesSourceDateMaximumLength);

            var keyToken = isAssigned
                ? TransportDemandKeyIdentity.CreateToken(
                    observation.WorkType!,
                    observation.Sublot!)
                : null;
            prepared.Add(new PreparedObservation(ordinal, keyToken, observation));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var unassigned = prepared
            .Where(item => item.KeyToken is null)
            .Select(item => item.Observation)
            .ToArray();
        return new PreparedRound(
            prepared,
            MesTaskUnionRoundDigest.Compute(round.Observations),
            unassigned.Length == 0
                ? UnassignedObservationFact.Empty
                : new UnassignedObservationFact(
                    unassigned.Length,
                    MesTaskUnionRoundDigest.Compute(unassigned)));
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

    private static async Task<IReadOnlyList<TaskTypeProtectionDecisionSnapshot>> ApplyTaskTypeProtectionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<PreparedObservationGroup> groups,
        MesTaskUnionRound round,
        string projectionCommitId,
        bool restartAbsenceAuthority,
        int enterThreshold,
        CancellationToken cancellationToken)
    {
        var observedCounts = groups
            .GroupBy(
                group => group.Observations[0].Observation.WorkType!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        var stored = await LoadTaskTypeProtectionStatesForUpdateAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var workTypes = stored.Keys
            .Concat(observedCounts.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var decisions = new List<TaskTypeProtectionDecisionSnapshot>(workTypes.Length);

        foreach (var workType in workTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = stored.GetValueOrDefault(workType);
            var before = existing is null
                ? TaskTypeProtectionState.Initial
                : new TaskTypeProtectionState(
                    TaskTypeProtectionPhaseContract.Parse(existing.Phase),
                    existing.LastHealthyNonZeroCount,
                    existing.LatestObservedCount,
                    existing.RecoveryStreak);
            var observedCount = observedCounts.GetValueOrDefault(workType);
            var transition = TaskTypeProtectionPolicy.AcceptSuccess(
                before,
                observedCount,
                enterThreshold,
                restartAbsenceAuthority);
            var episodeId = existing?.EpisodeId;
            var enteredAt = existing?.EnteredAt;
            if (transition.EventCodes.Contains(TaskTypeProtectionEventCode.Entered, StringComparer.Ordinal))
            {
                episodeId = NewId();
                enteredAt = round.CompletedAt;
            }
            var eventEpisodeId = episodeId;
            if (transition.After.Phase is TaskTypeProtectionPhase.Monitoring)
            {
                episodeId = null;
                enteredAt = null;
            }

            var nextSequence = existing?.LastSequence ?? 0;
            var eventIds = new List<string>(transition.EventCodes.Count);
            await UpsertTaskTypeProtectionStateAsync(
                connection,
                transaction,
                workType,
                transition,
                episodeId,
                enteredAt,
                nextSequence + transition.EventCodes.Count,
                enterThreshold,
                round.PollTraceId,
                projectionCommitId,
                cancellationToken).ConfigureAwait(false);
            foreach (var eventCode in transition.EventCodes)
            {
                if (eventEpisodeId is null)
                {
                    throw new InvalidOperationException(
                        "TaskTypeProtection lifecycle event is missing its episode identity.");
                }

                var eventId = NewId();
                nextSequence++;
                await InsertTaskTypeProtectionEventAsync(
                    connection,
                    transaction,
                    eventId,
                    eventEpisodeId,
                    workType,
                    nextSequence,
                    eventCode,
                    round,
                    projectionCommitId,
                    transition,
                    enterThreshold,
                    cancellationToken).ConfigureAwait(false);
                eventIds.Add(eventId);
            }

            await InsertTaskTypeProtectionDecisionAsync(
                connection,
                transaction,
                workType,
                projectionCommitId,
                transition,
                cancellationToken).ConfigureAwait(false);
            decisions.Add(new TaskTypeProtectionDecisionSnapshot(
                workType,
                transition.Before.Phase.ToContractValue(),
                transition.After.Phase.ToContractValue(),
                transition.After.LatestObservedCount,
                transition.After.LastHealthyNonZeroCount,
                transition.Before.RecoveryStreak,
                transition.After.RecoveryStreak,
                transition.ProtectionAllowsAbsenceAuthority,
                transition.EffectiveAbsenceAuthorityAvailable,
                eventIds));
        }

        return decisions;
    }

    private static async Task<Dictionary<string, TaskTypeProtectionStateRow>> LoadTaskTypeProtectionStatesForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TaskTypeProtectionStateRow>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT WorkType, Phase, LastHealthyNonZeroCount, LatestObservedCount,
                   RecoveryStreak, EpisodeId, EnteredAt, LastSequence
            FROM mesingest.TaskTypeProtectionStates WITH (UPDLOCK, HOLDLOCK)
            ORDER BY WorkType;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new TaskTypeProtectionStateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                GetNullableString(reader, 5),
                GetNullableDateTimeOffset(reader, 6),
                reader.GetInt64(7));
            states.Add(row.WorkType, row);
        }

        return states;
    }

    private static async Task UpsertTaskTypeProtectionStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string workType,
        TaskTypeProtectionTransition transition,
        string? episodeId,
        DateTimeOffset? enteredAt,
        long lastSequence,
        int enterThreshold,
        string pollTraceId,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.TaskTypeProtectionStates
            SET Phase = @phase,
                LastHealthyNonZeroCount = @lastHealthyNonZeroCount,
                LatestObservedCount = @latestObservedCount,
                RecoveryStreak = @recoveryStreak,
                EpisodeId = @episodeId,
                EnteredAt = @enteredAt,
                LastSequence = @lastSequence,
                EnterThreshold = @enterThreshold,
                ProtectionAllowsAbsenceAuthority = @protectionAllows,
                EffectiveAbsenceAuthorityAvailable = @effectiveAuthority,
                LatestPollTraceId = @pollTraceId,
                LatestProjectionCommitId = @projectionCommitId
            WHERE WorkType = @workType;

            IF @@ROWCOUNT = 0
                INSERT INTO mesingest.TaskTypeProtectionStates
                    (WorkType, Phase, LastHealthyNonZeroCount, LatestObservedCount,
                     RecoveryStreak, EpisodeId, EnteredAt, LastSequence, EnterThreshold,
                     ProtectionAllowsAbsenceAuthority, EffectiveAbsenceAuthorityAvailable,
                     LatestPollTraceId, LatestProjectionCommitId)
                VALUES
                    (@workType, @phase, @lastHealthyNonZeroCount, @latestObservedCount,
                     @recoveryStreak, @episodeId, @enteredAt, @lastSequence, @enterThreshold,
                     @protectionAllows, @effectiveAuthority, @pollTraceId, @projectionCommitId);
            """;
        AddNVarChar(command, "@workType", 128, workType);
        AddNVarChar(command, "@phase", 32, transition.After.Phase.ToContractValue());
        command.Parameters.Add("@lastHealthyNonZeroCount", SqlDbType.Int).Value = transition.After.LastHealthyNonZeroCount;
        command.Parameters.Add("@latestObservedCount", SqlDbType.Int).Value = transition.After.LatestObservedCount;
        command.Parameters.Add("@recoveryStreak", SqlDbType.Int).Value = transition.After.RecoveryStreak;
        AddNullableNVarChar(command, "@episodeId", 64, episodeId);
        AddNullableDateTimeOffset(command, "@enteredAt", enteredAt);
        command.Parameters.Add("@lastSequence", SqlDbType.BigInt).Value = lastSequence;
        command.Parameters.Add("@enterThreshold", SqlDbType.Int).Value = enterThreshold;
        command.Parameters.Add("@protectionAllows", SqlDbType.Bit).Value = transition.ProtectionAllowsAbsenceAuthority;
        command.Parameters.Add("@effectiveAuthority", SqlDbType.Bit).Value = transition.EffectiveAbsenceAuthorityAvailable;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTaskTypeProtectionEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string eventId,
        string episodeId,
        string workType,
        long workTypeSequence,
        string eventType,
        MesTaskUnionRound round,
        string projectionCommitId,
        TaskTypeProtectionTransition transition,
        int enterThreshold,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.TaskTypeProtectionEvents
                (EventId, EpisodeId, WorkType, WorkTypeSequence, EventType, OccurredAt,
                 PollTraceId, ProjectionCommitId, PhaseBefore, PhaseAfter, ObservedCount,
                 LastHealthyNonZeroCount, RecoveryStreak, RequiredRecoveryStreak, EnterThreshold)
            VALUES
                (@eventId, @episodeId, @workType, @workTypeSequence, @eventType, @occurredAt,
                 @pollTraceId, @projectionCommitId, @phaseBefore, @phaseAfter, @observedCount,
                 @lastHealthyNonZeroCount, @recoveryStreak, @requiredRecoveryStreak, @enterThreshold);
            """;
        AddNVarChar(command, "@eventId", 64, eventId);
        AddNVarChar(command, "@episodeId", 64, episodeId);
        AddNVarChar(command, "@workType", 128, workType);
        command.Parameters.Add("@workTypeSequence", SqlDbType.BigInt).Value = workTypeSequence;
        AddNVarChar(command, "@eventType", 128, eventType);
        AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@phaseBefore", 32, transition.Before.Phase.ToContractValue());
        AddNVarChar(command, "@phaseAfter", 32, transition.After.Phase.ToContractValue());
        command.Parameters.Add("@observedCount", SqlDbType.Int).Value = transition.After.LatestObservedCount;
        command.Parameters.Add("@lastHealthyNonZeroCount", SqlDbType.Int).Value = transition.After.LastHealthyNonZeroCount;
        command.Parameters.Add("@recoveryStreak", SqlDbType.Int).Value = transition.After.RecoveryStreak;
        command.Parameters.Add("@requiredRecoveryStreak", SqlDbType.Int).Value = TaskTypeProtectionPolicy.RequiredRecoveryStreak;
        command.Parameters.Add("@enterThreshold", SqlDbType.Int).Value = enterThreshold;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTaskTypeProtectionDecisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string workType,
        string projectionCommitId,
        TaskTypeProtectionTransition transition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.ProjectionCommitTaskTypeProtectionDecisions
                (ProjectionCommitId, WorkType, PhaseBefore, PhaseAfter, ObservedCount,
                 LastHealthyNonZeroCount, RecoveryStreakBefore, RecoveryStreakAfter,
                 ProtectionAllowsAbsenceAuthority, EffectiveAbsenceAuthorityAvailable)
            VALUES
                (@projectionCommitId, @workType, @phaseBefore, @phaseAfter, @observedCount,
                 @lastHealthyNonZeroCount, @recoveryStreakBefore, @recoveryStreakAfter,
                 @protectionAllows, @effectiveAuthority);
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@workType", 128, workType);
        AddNVarChar(command, "@phaseBefore", 32, transition.Before.Phase.ToContractValue());
        AddNVarChar(command, "@phaseAfter", 32, transition.After.Phase.ToContractValue());
        command.Parameters.Add("@observedCount", SqlDbType.Int).Value = transition.After.LatestObservedCount;
        command.Parameters.Add("@lastHealthyNonZeroCount", SqlDbType.Int).Value = transition.After.LastHealthyNonZeroCount;
        command.Parameters.Add("@recoveryStreakBefore", SqlDbType.Int).Value = transition.Before.RecoveryStreak;
        command.Parameters.Add("@recoveryStreakAfter", SqlDbType.Int).Value = transition.After.RecoveryStreak;
        command.Parameters.Add("@protectionAllows", SqlDbType.Bit).Value = transition.ProtectionAllowsAbsenceAuthority;
        command.Parameters.Add("@effectiveAuthority", SqlDbType.Bit).Value = transition.EffectiveAbsenceAuthorityAvailable;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                p.DiagnosticStage,
                p.DiagnosticCode,
                p.DiagnosticSafeDetail,
                c.ProjectionCommitId,
                c.ProjectionSequence,
                c.CommittedAt,
                c.HostSessionId,
                c.RestartPhaseBefore,
                c.RestartPhaseAfter,
                c.AbsenceAuthority,
                p.RawObservationsExpiredAt
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
        && string.Equals(existing.ContentDigest, contentDigest, StringComparison.Ordinal)
        && Equals(existing.Diagnostic, round.Diagnostic);

    private static async Task<RoundCommitReceipt> ReadAcceptedReceiptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PollTraceRow existing,
        HistoryEpoch historyEpoch,
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
                SELECT e.SeriesId,
                       CASE WHEN e.EventType = N'DEMAND_GONE'
                            THEN e.SubjectId
                            ELSE JSON_VALUE(e.Payload, N'$.demandId') COLLATE Latin1_General_100_BIN2
                       END
                FROM mesingest.DemandSeriesEvents AS e
                WHERE e.PollTraceId = @pollTraceId
                  AND e.EventType IN (N'DEMAND_GONE', N'GONE_TIMEOUT_ARCHIVED')
                  AND CASE WHEN e.EventType = N'DEMAND_GONE'
                           THEN e.SubjectId
                           ELSE JSON_VALUE(e.Payload, N'$.demandId') COLLATE Latin1_General_100_BIN2
                      END IS NOT NULL
                ORDER BY e.SeriesId, e.SeriesSequence, e.EventId;
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
            IsReplay: true,
            existing.ProjectionSequence,
            historyEpoch);
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

    private static async Task<long> ReadProjectionSequenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ProjectionSequence
            FROM mesingest.ProjectionCommits
            WHERE ProjectionCommitId = @projectionCommitId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long sequence
            ? sequence
            : throw new InvalidOperationException("The accepted projection commit has no sequence.");
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
        HistoryEpoch historyEpoch,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesingest.PollTraces
                (PollTraceId, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest,
                 DiagnosticStage, DiagnosticCode, DiagnosticSafeDetail)
            VALUES
                (@pollTraceId, @queryVersion, N'SUCCESS', @startedAt, @completedAt, @rowCount, @contentDigest,
                 NULL, NULL, NULL);

            INSERT INTO mesingest.ProjectionCommits
                (ProjectionCommitId, PollTraceId, CommittedAt, HostSessionId,
                 RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority,
                 CatalogRevision, HistoryEpoch, OverviewActiveErrorSeriesCount,
                 OverviewPrior7DaysErrorSeriesCount)
            VALUES
                (@projectionCommitId, @pollTraceId, @completedAt, @hostSessionId,
                 @restartPhaseBefore, @restartPhaseAfter, @absenceAuthority,
                (SELECT CatalogRevision FROM mesingest.CatalogState WHERE Id = 1),
                 @historyEpoch, 0, 0);

            UPDATE mesingest.SchemaInfo
            SET EarliestAvailableHostUtc =
                CASE
                    WHEN EarliestAvailableHostUtc IS NULL
                         OR @completedAt < EarliestAvailableHostUtc
                    THEN @completedAt
                    ELSE EarliestAvailableHostUtc
                END
            WHERE Id = 1;
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
        command.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value =
            historyEpoch.Value;
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
                (PollTraceId, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest,
                 DiagnosticStage, DiagnosticCode, DiagnosticSafeDetail)
            VALUES
                (@pollTraceId, @queryVersion, @outcome, @startedAt, @completedAt, @rowCount, @contentDigest,
                 @diagnosticStage, @diagnosticCode, @diagnosticSafeDetail);

            UPDATE mesingest.SchemaInfo
            SET EarliestAvailableHostUtc =
                CASE
                    WHEN EarliestAvailableHostUtc IS NULL
                         OR @completedAt < EarliestAvailableHostUtc
                    THEN @completedAt
                    ELSE EarliestAvailableHostUtc
                END
            WHERE Id = 1;
            """;
        AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
        AddNVarChar(command, "@queryVersion", 128, round.QueryVersion);
        AddNVarChar(command, "@outcome", 16, GetOutcome(round.Outcome));
        AddDateTimeOffset(command, "@startedAt", round.StartedAt.ToUniversalTime());
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt.ToUniversalTime());
        command.Parameters.Add("@rowCount", SqlDbType.Int).Value = round.Observations.Count;
        AddChar(command, "@contentDigest", 64, contentDigest);
        AddNullableNVarChar(command, "@diagnosticStage", 64, round.Diagnostic?.Stage);
        AddNullableNVarChar(command, "@diagnosticCode", 128, round.Diagnostic?.Code);
        AddNullableNVarChar(command, "@diagnosticSafeDetail", 512, round.Diagnostic?.SafeDetail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReconcileUnassignedObservationAttentionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        UnassignedObservationFact after,
        CancellationToken cancellationToken)
    {
        PersistedUnassignedObservationState before;
        await using (var load = connection.CreateCommand())
        {
            load.Transaction = transaction;
            load.CommandText = """
                SELECT TOP (1)
                    fact.ObservationCount,
                    fact.ContentDigest,
                    fact.StateEventId,
                    fact.StateChangedAt
                FROM mesingest.ProjectionCommitUnassignedObservationFacts AS fact
                INNER JOIN mesingest.ProjectionCommits AS commitRow
                    ON commitRow.ProjectionCommitId = fact.ProjectionCommitId
                WHERE commitRow.ProjectionSequence <
                    (SELECT currentCommit.ProjectionSequence
                     FROM mesingest.ProjectionCommits AS currentCommit
                     WHERE currentCommit.ProjectionCommitId = @projectionCommitId)
                ORDER BY commitRow.ProjectionSequence DESC;
                """;
            AddNVarChar(load, "@projectionCommitId", 64, projectionCommitId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            before = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new PersistedUnassignedObservationState(
                    new UnassignedObservationFact(
                        reader.GetInt32(0),
                        GetNullableString(reader, 1)),
                    GetNullableString(reader, 2),
                    GetNullableDateTimeOffset(reader, 3))
                : PersistedUnassignedObservationState.Empty;
        }

        var eventType = GetUnassignedObservationEventType(before.Fact, after);
        var eventId = eventType is null ? before.StateEventId : NewId();
        var stateChangedAt = after.ObservationCount == 0
            ? null
            : eventType is null
                ? before.StateChangedAt
                : round.CompletedAt;
        await using (var insertFact = connection.CreateCommand())
        {
            insertFact.Transaction = transaction;
            insertFact.CommandText = """
                INSERT INTO mesingest.ProjectionCommitUnassignedObservationFacts
                    (ProjectionCommitId, ObservationCount, ContentDigest,
                     StateEventId, StateChangedAt)
                VALUES
                    (@projectionCommitId, @observationCount, @contentDigest,
                     @stateEventId, @stateChangedAt);
                """;
            AddNVarChar(insertFact, "@projectionCommitId", 64, projectionCommitId);
            insertFact.Parameters.Add("@observationCount", SqlDbType.Int).Value = after.ObservationCount;
            AddNullableChar(insertFact, "@contentDigest", 64, after.ContentDigest);
            AddNullableNVarChar(insertFact, "@stateEventId", 64,
                after.ObservationCount == 0 ? null : eventId);
            AddNullableDateTimeOffset(insertFact, "@stateChangedAt", stateChangedAt);
            await insertFact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (eventType is null)
        {
            return;
        }

        await using var insertEvent = connection.CreateCommand();
        insertEvent.Transaction = transaction;
        insertEvent.CommandText = """
            INSERT INTO mesingest.UnassignedMesObservationEvents
                (EventId, EventType, OccurredAt, ProjectionCommitId,
                 BeforeObservationCount, AfterObservationCount,
                 BeforeContentDigest, AfterContentDigest)
            VALUES
                (@eventId, @eventType, @occurredAt, @projectionCommitId,
                 @beforeObservationCount, @afterObservationCount,
                 @beforeContentDigest, @afterContentDigest);
            """;
        AddNVarChar(insertEvent, "@eventId", 64, eventId!);
        AddNVarChar(insertEvent, "@eventType", 128, eventType);
        AddDateTimeOffset(insertEvent, "@occurredAt", round.CompletedAt);
        AddNVarChar(insertEvent, "@projectionCommitId", 64, projectionCommitId);
        insertEvent.Parameters.Add("@beforeObservationCount", SqlDbType.Int).Value = before.Fact.ObservationCount;
        insertEvent.Parameters.Add("@afterObservationCount", SqlDbType.Int).Value = after.ObservationCount;
        AddNullableChar(insertEvent, "@beforeContentDigest", 64, before.Fact.ContentDigest);
        AddNullableChar(insertEvent, "@afterContentDigest", 64, after.ContentDigest);
        await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateOverviewErrorSummaryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectionCommitId,
        DateTimeOffset snapshotAsOf,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE commitRow
            SET OverviewActiveErrorSeriesCount =
                    (SELECT COUNT_BIG(*)
                     FROM (SELECT DISTINCT SeriesId
                           FROM mesingest.DemandSeriesCurrentConditions) AS activeSeries),
                OverviewPrior7DaysErrorSeriesCount =
                    (SELECT COUNT_BIG(*)
                     FROM (SELECT DISTINCT SeriesId
                           FROM mesingest.DemandSeriesErrorPeriods
                           WHERE StartedAt < @toUtc
                             AND (EndedAt IS NULL OR EndedAt > @fromUtc)) AS recentSeries)
            FROM mesingest.ProjectionCommits AS commitRow
            WHERE commitRow.ProjectionCommitId = @projectionCommitId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddDateTimeOffset(command, "@fromUtc", snapshotAsOf.ToUniversalTime().AddDays(-7));
        AddDateTimeOffset(command, "@toUtc", snapshotAsOf.ToUniversalTime());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The current overview error summary could not be bound to its projection commit.");
        }
    }

    private static string? GetUnassignedObservationEventType(
        UnassignedObservationFact before,
        UnassignedObservationFact after)
    {
        if (before.ObservationCount == 0 && after.ObservationCount > 0)
        {
            return "UNASSIGNED_MES_OBSERVATION_APPEARED";
        }

        if (before.ObservationCount > 0 && after.ObservationCount == 0)
        {
            return "UNASSIGNED_MES_OBSERVATION_CLEARED";
        }

        return before.ObservationCount > 0
            && after.ObservationCount > 0
            && !string.Equals(before.ContentDigest, after.ContentDigest, StringComparison.Ordinal)
                ? "UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED"
                : null;
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
                d.Package,
                d.CurrentRawObservationCount
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
            GetNullableString(reader, 14),
            reader.GetInt32(15));
    }

    private static async Task<ProjectedIdentity> InsertFirstGenerationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        PreparedObservation item,
        MesTaskUnionObservation? liveObservation,
        int currentRawObservationCount,
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
                     LatestObservationProjectionCommitId, CurrentRawObservationCount,
                     DemandRevision, ValueObservedAt,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, 1, NULL, N'VISIBLE',
                     @occurredAt, @occurredAt, NULL, @pollTraceId,
                     @projectionCommitId, @projectionCommitId, @projectionCommitId,
                     @currentRawObservationCount, 1, @occurredAt,
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
            command.Parameters.Add("@currentRawObservationCount", SqlDbType.Int).Value =
                currentRawObservationCount;
            AddNullableNVarChar(command, "@area", CurrentMesFieldMaximumLength, liveObservation?.Area);
            AddNullableNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, liveObservation?.Eqp);
            AddNullableNVarChar(command, "@step", CurrentMesFieldMaximumLength, liveObservation?.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", liveObservation?.MesSourceDate);
            AddNullableNVarChar(command, "@package", CurrentMesFieldMaximumLength, liveObservation?.Package);
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
        int currentRawObservationCount,
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
                     LatestObservationProjectionCommitId, CurrentRawObservationCount,
                     DemandRevision, ValueObservedAt,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, @generation, @predecessorDemandId, N'VISIBLE',
                     @occurredAt, @occurredAt, NULL, @pollTraceId,
                     @projectionCommitId, @projectionCommitId, @projectionCommitId,
                     @currentRawObservationCount, 1, @occurredAt,
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
            command.Parameters.Add("@currentRawObservationCount", SqlDbType.Int).Value =
                currentRawObservationCount;
            AddNullableNVarChar(command, "@area", CurrentMesFieldMaximumLength, liveObservation?.Area);
            AddNullableNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, liveObservation?.Eqp);
            AddNullableNVarChar(command, "@step", CurrentMesFieldMaximumLength, liveObservation?.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", liveObservation?.MesSourceDate);
            AddNullableNVarChar(command, "@package", CurrentMesFieldMaximumLength, liveObservation?.Package);
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

    private static async Task<ProjectedIdentity> InsertPostarchiveSuccessorGenerationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionRound round,
        string projectionCommitId,
        MesTaskUnionObservation? liveObservation,
        int currentRawObservationCount,
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
                     LatestObservationProjectionCommitId, CurrentRawObservationCount,
                     DemandRevision, ValueObservedAt,
                     Area, Eqp, Step, MesSourceDate, Package)
                VALUES
                    (@demandId, @seriesId, @generation, @predecessorDemandId, N'LONG_GONE_BUT_VISIBLE',
                     @occurredAt, @occurredAt, NULL, @pollTraceId,
                     @projectionCommitId, @projectionCommitId, @projectionCommitId,
                     @currentRawObservationCount, 1, @occurredAt,
                     @area, @eqp, @step, @mesSourceDate, @package);

                UPDATE mesingest.DemandSeries
                SET CurrentDemandId = @demandId,
                    CurrentPresence = N'LONG_GONE_BUT_VISIBLE',
                    LatestProjectionCommitId = @projectionCommitId
                WHERE SeriesId = @seriesId
                  AND CurrentDemandId = @predecessorDemandId
                  AND Lifecycle = N'ARCHIVED'
                  AND CurrentPresence = N'GONE';
                """;
            AddNVarChar(command, "@demandId", 64, demandId);
            AddNVarChar(command, "@seriesId", 64, current.SeriesId);
            command.Parameters.Add("@generation", SqlDbType.Int).Value = generation;
            AddNVarChar(command, "@predecessorDemandId", 64, current.DemandId);
            AddDateTimeOffset(command, "@occurredAt", round.CompletedAt);
            AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            command.Parameters.Add("@currentRawObservationCount", SqlDbType.Int).Value =
                currentRawObservationCount;
            AddNullableNVarChar(command, "@area", CurrentMesFieldMaximumLength, liveObservation?.Area);
            AddNullableNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, liveObservation?.Eqp);
            AddNullableNVarChar(command, "@step", CurrentMesFieldMaximumLength, liveObservation?.Step);
            AddNullableDateTimeOffset(command, "@mesSourceDate", liveObservation?.MesSourceDate);
            AddNullableNVarChar(command, "@package", CurrentMesFieldMaximumLength, liveObservation?.Package);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
            {
                throw new InvalidOperationException(
                    "The archived GONE Demand changed while creating its visible successor generation.");
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
                reason = PostarchiveReappearanceReason,
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

    private static void EnsurePostarchiveReappearanceCanAdvance(
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        DateTimeOffset completedAt)
    {
        if (!string.Equals(current.WorkType, observation.WorkType, StringComparison.Ordinal)
            || !string.Equals(current.Sublot, observation.Sublot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TransportDemandKey token collision detected while applying a postarchive reappearance.");
        }
        if (!string.Equals(current.Lifecycle, ArchivedLifecycle, StringComparison.Ordinal)
            || !string.Equals(current.CurrentPresence, GonePresence, StringComparison.Ordinal)
            || !string.Equals(current.DemandStatus, GoneDemandStatus, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Only an archived GONE Demand can reappear after archive.");
        }
        if (completedAt < current.DemandLastSeenAt
            || current.GoneConfirmedAt is null
            || completedAt < current.GoneConfirmedAt.Value)
        {
            throw new InvalidOperationException(
                "A postarchive reappearance cannot predate its predecessor evidence.");
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

        var isTrackingVisible = string.Equals(current.Lifecycle, TrackingLifecycle, StringComparison.Ordinal)
            && string.Equals(current.CurrentPresence, VisiblePresence, StringComparison.Ordinal)
            && string.Equals(current.DemandStatus, VisibleDemandStatus, StringComparison.Ordinal);
        var isArchivedVisible = string.Equals(current.Lifecycle, ArchivedLifecycle, StringComparison.Ordinal)
            && string.Equals(current.CurrentPresence, LongGoneButVisiblePresence, StringComparison.Ordinal)
            && string.Equals(current.DemandStatus, LongGoneButVisibleDemandStatus, StringComparison.Ordinal);
        if (!isTrackingVisible && !isArchivedVisible)
        {
            throw new NotSupportedException(
                "Only a currently visible tracking or archived Demand can accept an observation.");
        }

        if (completedAt < current.DemandLastSeenAt)
        {
            throw new InvalidOperationException(
                "A successful round cannot move DemandLastSeenAt backwards.");
        }

    }

    private static async Task<bool> AdvanceLiveObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionObservation observation,
        MesTaskUnionRound round,
        string projectionCommitId,
        int currentRawObservationCount,
        CancellationToken cancellationToken)
    {
        var changes = GetLiveFieldChanges(current, observation);
        var businessValueChanged = changes.Count != 0 || current.LatestObservationCount != 1;
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
                CurrentRawObservationCount = @currentRawObservationCount,
                DemandLastSeenAt = @completedAt,
                DemandRevision = DemandRevision +
                    CASE WHEN @businessValueChanged = 1 THEN 1 ELSE 0 END,
                ValueObservedAt = CASE WHEN @businessValueChanged = 1
                    THEN @completedAt ELSE ValueObservedAt END,
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
        command.Parameters.Add("@currentRawObservationCount", SqlDbType.Int).Value =
            currentRawObservationCount;
        command.Parameters.Add("@lastSeriesSequence", SqlDbType.BigInt).Value = nextSequence - 1;
        command.Parameters.Add("@businessValueChanged", SqlDbType.Bit).Value = businessValueChanged;
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt);
        AddNullableNVarChar(command, "@area", CurrentMesFieldMaximumLength, observation.Area);
        AddNullableNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, observation.Eqp);
        AddNullableNVarChar(command, "@step", CurrentMesFieldMaximumLength, observation.Step);
        AddNullableDateTimeOffset(
            command,
            "@mesSourceDate",
            MesTaskUnionValueSemantics.SourceDatesEqual(current.MesSourceDate, observation.MesSourceDate)
                ? current.MesSourceDate
                : observation.MesSourceDate);
        AddNullableNVarChar(command, "@package", CurrentMesFieldMaximumLength, observation.Package);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 2)
        {
            throw new InvalidOperationException(
                "The current DemandSeries projection changed while applying an equivalent observation.");
        }
        return businessValueChanged;
    }

    private static async Task<bool> AdvanceConflictingObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentProjectionRow current,
        MesTaskUnionRound round,
        string projectionCommitId,
        int currentRawObservationCount,
        CancellationToken cancellationToken)
    {
        var businessValueChanged = current.LatestObservationCount == 1;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.DemandSeries
            SET LatestProjectionCommitId = @projectionCommitId
            WHERE SeriesId = @seriesId;

            UPDATE mesingest.TransportDemands
            SET LatestProjectionCommitId = @projectionCommitId,
                LatestObservationProjectionCommitId = @projectionCommitId,
                CurrentRawObservationCount = @currentRawObservationCount,
                DemandLastSeenAt = @completedAt,
                DemandRevision = DemandRevision +
                    CASE WHEN @businessValueChanged = 1 THEN 1 ELSE 0 END,
                ValueObservedAt = CASE WHEN @businessValueChanged = 1
                    THEN @completedAt ELSE ValueObservedAt END
            WHERE DemandId = @demandId;
            """;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNVarChar(command, "@seriesId", 64, current.SeriesId);
        AddNVarChar(command, "@demandId", 64, current.DemandId);
        command.Parameters.Add("@currentRawObservationCount", SqlDbType.Int).Value =
            currentRawObservationCount;
        AddDateTimeOffset(command, "@completedAt", round.CompletedAt);
        command.Parameters.Add("@businessValueChanged", SqlDbType.Bit).Value = businessValueChanged;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
        {
            throw new InvalidOperationException(
                "The current DemandSeries projection changed while applying conflicting observations.");
        }
        return businessValueChanged;
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
        bool demandRevisionAdvancedThisRound,
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

            if (!demandRevisionAdvancedThisRound)
            {
                await AdvanceDemandRevisionForConditionChangeAsync(
                    connection,
                    transaction,
                    identity.DemandId,
                    round.CompletedAt,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AdvanceDemandRevisionForConditionChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string demandId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE mesingest.TransportDemands
            SET DemandRevision = DemandRevision + 1,
                ValueObservedAt = @observedAt
            WHERE DemandId = @demandId;
            """;
        AddNVarChar(command, "@demandId", 64, demandId);
        AddDateTimeOffset(command, "@observedAt", observedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The current TransportDemand disappeared while advancing its condition revision.");
        }
    }

    private static async Task EnsureLongGoneButVisibleConditionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectedIdentity identity,
        MesTaskUnionRound round,
        string projectionCommitId,
        CancellationToken cancellationToken)
    {
        var target = $"SERIES:{identity.SeriesId}";
        var key = new ConditionKey(LongGoneButVisibleError, ArchivedSeriesVisibilitySubject);
        var current = await LoadCurrentConditionsForTargetForUpdateAsync(
            connection,
            transaction,
            identity.SeriesId,
            target,
            cancellationToken).ConfigureAwait(false);
        if (current.ContainsKey(key))
        {
            return;
        }

        var definition = SeriesErrorCatalog.GetRequired(LongGoneButVisibleError);
        var periodId = NewId();
        var evidenceId = NewId();
        var sequence = await GetNextSeriesSequenceAsync(
            connection,
            transaction,
            identity.SeriesId,
            cancellationToken).ConfigureAwait(false);
        const string expectedRule = "AN_ARCHIVED_SERIES_IS_NEVER_EXTERNALLY_READABLE";
        var eventId = await InsertEventAsync(
            connection,
            transaction,
            identity.SeriesId,
            sequence,
            "SERIES_ERROR_PERIOD_STARTED",
            ArchivedSeriesVisibilitySubject,
            identity.DemandId,
            round,
            projectionCommitId,
            JsonSerializer.Serialize(new
            {
                periodId,
                code = LongGoneButVisibleError,
                category = definition.Category,
                target,
                subjectKind = ArchivedSeriesVisibilitySubject,
                startReason = PostarchiveReappearanceReason,
                observedValue = identity.DemandId,
                expectedRule,
            }),
            cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
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
                    (@evidenceId, @periodId, @eventId, @startReason, @observedAt,
                     @pollTraceId, @projectionCommitId, @demandId, @demandId, @expectedRule);

                INSERT INTO mesingest.DemandSeriesCurrentConditions
                    (SeriesId, ErrorCode, Target, SubjectKind, PeriodId, LatestEvidenceId)
                VALUES
                    (@seriesId, @errorCode, @target, @subjectKind, @periodId, @evidenceId);
                """;
            AddNVarChar(command, "@periodId", 64, periodId);
            AddNVarChar(command, "@seriesId", 64, identity.SeriesId);
            AddNVarChar(command, "@errorCode", 128, LongGoneButVisibleError);
            AddNVarChar(command, "@category", 64, definition.Category);
            AddNVarChar(command, "@severity", 32, definition.Severity);
            AddNVarChar(command, "@target", 128, target);
            AddNVarChar(command, "@subjectKind", 64, ArchivedSeriesVisibilitySubject);
            AddNVarChar(command, "@startReason", 64, PostarchiveReappearanceReason);
            AddDateTimeOffset(command, "@observedAt", round.CompletedAt);
            AddNVarChar(command, "@eventId", 64, eventId);
            AddNVarChar(command, "@evidenceId", 64, evidenceId);
            AddNVarChar(command, "@pollTraceId", 128, round.PollTraceId);
            AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
            AddNVarChar(command, "@demandId", 64, identity.DemandId);
            AddNVarChar(command, "@expectedRule", 256, expectedRule);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetLastSeriesSequenceAsync(
            connection,
            transaction,
            identity.SeriesId,
            sequence,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkAbsentVisibleDemandsGoneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlySet<string> presentKeyTokens,
        IReadOnlyDictionary<string, bool> effectiveAuthorityByWorkType,
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
                SELECT s.KeyToken, s.WorkType, s.SeriesId, d.DemandId, d.Generation, d.DemandLastSeenAt
                FROM mesingest.DemandSeries AS s WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN mesingest.TransportDemands AS d WITH (UPDLOCK, HOLDLOCK)
                    ON d.DemandId = s.CurrentDemandId
                WHERE (s.Lifecycle = N'TRACKING'
                       AND s.CurrentPresence = N'VISIBLE'
                       AND d.Status = N'VISIBLE')
                   OR (s.Lifecycle = N'ARCHIVED'
                       AND s.CurrentPresence = N'LONG_GONE_BUT_VISIBLE'
                       AND d.Status = N'LONG_GONE_BUT_VISIBLE')
                ORDER BY s.SeriesId;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new AbsentDemandRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetFieldValue<DateTimeOffset>(5));
                if (!presentKeyTokens.Contains(row.KeyToken)
                    && effectiveAuthorityByWorkType.GetValueOrDefault(row.WorkType))
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
            nextSequence = await CloseLongGoneButVisibleConditionAsGoneAsync(
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
                    WHERE DemandId = @demandId
                      AND Status IN (N'VISIBLE', N'LONG_GONE_BUT_VISIBLE');

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

    private static async Task ArchiveOverdueGoneSeriesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MesTaskUnionRound round,
        string projectionCommitId,
        IReadOnlyDictionary<string, bool> effectiveAuthorityByWorkType,
        ICollection<string> affectedSeriesIds,
        ICollection<string> affectedDemandIds,
        CancellationToken cancellationToken)
    {
        if (!effectiveAuthorityByWorkType.Values.Any(value => value))
        {
            return;
        }

        var candidates = new List<ArchiveCandidateRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.WorkType, s.SeriesId, d.DemandId, d.Generation, d.GoneConfirmedAt
                FROM mesingest.DemandSeries AS s WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN mesingest.TransportDemands AS d WITH (UPDLOCK, HOLDLOCK)
                    ON d.DemandId = s.CurrentDemandId
                WHERE s.Lifecycle = N'TRACKING'
                  AND s.CurrentPresence = N'GONE'
                  AND d.Status = N'GONE'
                  AND d.GoneConfirmedAt IS NOT NULL
                ORDER BY s.SeriesId;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new ArchiveCandidateRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        foreach (var candidate in candidates)
        {
            var absenceAuthority = effectiveAuthorityByWorkType.GetValueOrDefault(candidate.WorkType);
            if (!DemandSeriesArchivePolicy.IsDue(
                    candidate.GoneConfirmedAt,
                    round.CompletedAt,
                    absenceAuthority))
            {
                continue;
            }

            var sequence = await GetNextSeriesSequenceAsync(
                connection,
                transaction,
                candidate.SeriesId,
                cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(
                connection,
                transaction,
                candidate.SeriesId,
                sequence,
                SeriesArchivedEvent,
                "SERIES",
                candidate.SeriesId,
                round,
                projectionCommitId,
                JsonSerializer.Serialize(new
                {
                    seriesId = candidate.SeriesId,
                    demandId = candidate.DemandId,
                    generation = candidate.Generation,
                    goneConfirmedAt = candidate.GoneConfirmedAt,
                    archivedAt = round.CompletedAt,
                    minimumGoneDurationHours = DemandSeriesArchivePolicy.MinimumGoneDuration.TotalHours,
                }),
                cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE mesingest.TransportDemands
                    SET LatestProjectionCommitId = @projectionCommitId
                    WHERE DemandId = @demandId
                      AND Status = N'GONE'
                      AND GoneConfirmedAt = @goneConfirmedAt;

                    UPDATE mesingest.DemandSeries
                    SET Lifecycle = N'ARCHIVED',
                        ArchivedAt = @archivedAt,
                        LatestProjectionCommitId = @projectionCommitId,
                        LastSeriesSequence = @lastSeriesSequence
                    WHERE SeriesId = @seriesId
                      AND CurrentDemandId = @demandId
                      AND Lifecycle = N'TRACKING'
                      AND CurrentPresence = N'GONE';
                    """;
                AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
                AddNVarChar(command, "@demandId", 64, candidate.DemandId);
                AddDateTimeOffset(command, "@goneConfirmedAt", candidate.GoneConfirmedAt);
                AddDateTimeOffset(command, "@archivedAt", round.CompletedAt);
                command.Parameters.Add("@lastSeriesSequence", SqlDbType.BigInt).Value = sequence;
                AddNVarChar(command, "@seriesId", 64, candidate.SeriesId);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
                {
                    throw new InvalidOperationException(
                        "The overdue GONE series changed while publishing its archive fact.");
                }
            }

            affectedSeriesIds.Add(candidate.SeriesId);
            affectedDemandIds.Add(candidate.DemandId);
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

    private static async Task<long> CloseLongGoneButVisibleConditionAsGoneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AbsentDemandRow absent,
        MesTaskUnionRound round,
        string projectionCommitId,
        long nextSequence,
        CancellationToken cancellationToken)
    {
        var conditions = await LoadCurrentConditionsForTargetForUpdateAsync(
            connection,
            transaction,
            absent.SeriesId,
            $"SERIES:{absent.SeriesId}",
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
                observation.MesSourceDateRaw,
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
        CancellationToken cancellationToken) =>
        await LoadCurrentConditionsForTargetForUpdateAsync(
            connection,
            transaction,
            seriesId,
            $"DEMAND:{demandId}",
            cancellationToken).ConfigureAwait(false);

    private static async Task<Dictionary<ConditionKey, CurrentConditionRow>>
        LoadCurrentConditionsForTargetForUpdateAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string seriesId,
            string target,
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
        AddNVarChar(command, "@target", 128, target);
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
            "DATES" => observation.MesSourceDateRaw
                ?? observation.MesSourceDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
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
                 WorkType, Sublot, Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
            VALUES
                (@pollTraceId, @ordinal, @projectionCommitId, @seriesId, @demandId,
                 @workType, @sublot, @area, @eqp, @step, @mesSourceDate, @package, @mesSourceDateRaw);
            """;
        AddNVarChar(command, "@pollTraceId", 128, pollTraceId);
        command.Parameters.Add("@ordinal", SqlDbType.Int).Value = item.Ordinal;
        AddNVarChar(command, "@projectionCommitId", 64, projectionCommitId);
        AddNullableNVarChar(command, "@seriesId", 64, identity?.SeriesId);
        AddNullableNVarChar(command, "@demandId", 64, identity?.DemandId);
        AddNullableNVarChar(command, "@workType", 128, observation.WorkType);
        AddNullableNVarChar(command, "@sublot", 256, observation.Sublot);
        AddNullableNVarChar(command, "@area", CurrentMesFieldMaximumLength, observation.Area);
        AddNullableNVarChar(command, "@eqp", CurrentMesFieldMaximumLength, observation.Eqp);
        AddNullableNVarChar(command, "@step", CurrentMesFieldMaximumLength, observation.Step);
        AddNullableDateTimeOffset(command, "@mesSourceDate", observation.MesSourceDate);
        AddNullableNVarChar(command, "@package", CurrentMesFieldMaximumLength, observation.Package);
        AddNullableNVarChar(
            command,
            "@mesSourceDateRaw",
            RawMesSourceDateMaximumLength,
            observation.MesSourceDateRaw);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TransportDemandSnapshot>> ReadDemandGenerationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string seriesId,
        string currentDemandId,
        string lifecycle,
        string currentPresence,
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
            if (string.Equals(lifecycle, ArchivedLifecycle, StringComparison.Ordinal))
            {
                blockers.Add(SeriesArchivedBlocker);
            }
            if (isCurrent
                && string.Equals(currentPresence, LongGoneButVisiblePresence, StringComparison.Ordinal))
            {
                blockers.Add(LongGoneButVisibleError);
            }

            blockers = blockers.Distinct(StringComparer.Ordinal).ToList();

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
            reader.GetString(9),
            GetNullableDateTimeOffset(reader, 10));

    private static PollTraceRow ReadPollTraceRow(SqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.IsDBNull(7)
                ? null
                : new MesTaskUnionRoundDiagnostic(
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9)),
            GetNullableString(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            GetNullableDateTimeOffset(reader, 12),
            GetNullableString(reader, 13),
            GetNullableString(reader, 14),
            GetNullableString(reader, 15),
            reader.IsDBNull(16) ? null : reader.GetBoolean(16),
            GetNullableDateTimeOffset(reader, 17));

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

    private static void AddNullableChar(
        SqlCommand command,
        string name,
        int size,
        string? value) =>
        command.Parameters.Add(name, SqlDbType.Char, size).Value =
            value is null ? DBNull.Value : value;

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

    private static string SerializeBoundedSqlFilter(
        IReadOnlyList<string> values,
        Func<string, Exception> exceptionFactory)
    {
        var json = JsonSerializer.Serialize(values);
        if (json.Length > SqlFilterJsonMaximumLength)
        {
            throw exceptionFactory(
                $"A serialized filter dimension must not exceed {SqlFilterJsonMaximumLength} characters.");
        }
        return json;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed record PreparedRound(
        IReadOnlyList<PreparedObservation> Observations,
        string ContentDigest,
        UnassignedObservationFact UnassignedObservations);

    private sealed record UnassignedObservationFact(
        int ObservationCount,
        string? ContentDigest)
    {
        public static UnassignedObservationFact Empty { get; } = new(0, null);
    }

    private sealed record PersistedUnassignedObservationState(
        UnassignedObservationFact Fact,
        string? StateEventId,
        DateTimeOffset? StateChangedAt)
    {
        public static PersistedUnassignedObservationState Empty { get; } =
            new(UnassignedObservationFact.Empty, null, null);
    }

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
        [property: JsonPropertyName("mesSourceDateRaw")] string? MesSourceDateRaw,
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
        string? Package,
        int LatestObservationCount);

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
        string CurrentDemandId,
        DateTimeOffset? ArchivedAt);

    private sealed record PollTraceRow(
        string PollTraceId,
        string QueryVersion,
        string Outcome,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        int RowCount,
        string ContentDigest,
        MesTaskUnionRoundDiagnostic? Diagnostic,
        string? ProjectionCommitId,
        long? ProjectionSequence,
        DateTimeOffset? CommittedAt,
        string? HostSessionId,
        string? RestartPhaseBefore,
        string? RestartPhaseAfter,
        bool? AbsenceAuthority,
        DateTimeOffset? RawObservationsExpiredAt);

    private sealed record HostSessionRow(
        string HostSessionId,
        DateTimeOffset StartedAt,
        string RestartPhase,
        bool IsCurrent);

    private sealed record TaskTypeProtectionStateRow(
        string WorkType,
        string Phase,
        int LastHealthyNonZeroCount,
        int LatestObservedCount,
        int RecoveryStreak,
        string? EpisodeId,
        DateTimeOffset? EnteredAt,
        long LastSequence);

    private sealed record AbsentDemandRow(
        string KeyToken,
        string WorkType,
        string SeriesId,
        string DemandId,
        int Generation,
        DateTimeOffset DemandLastSeenAt);

    private sealed record ArchiveCandidateRow(
        string WorkType,
        string SeriesId,
        string DemandId,
        int Generation,
        DateTimeOffset GoneConfirmedAt);
}
