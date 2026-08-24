using System.Data;
using MesIngest.Core.SeriesProjection;
using Microsoft.Data.SqlClient;

namespace MesIngest.Infrastructure.SqlServer;

internal static class SqlServerMesIngestSchema
{
    public static async Task<HistoryEpoch> EnsureAsync(
        SqlConnection connection,
        HistoryEpochBootstrapIntent? historyEpochBootstrapIntent,
        CancellationToken cancellationToken)
    {
        var sessionLockHeld = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = AcquireSessionSchemaLockAndCountSql;
            var userObjectCount = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            sessionLockHeld = true;

            if (userObjectCount != 0 && historyEpochBootstrapIntent is not null)
            {
                throw new InvalidOperationException(
                    "A new HistoryEpoch can only be requested for an empty database; "
                    + "an existing database is never rotated in place.");
            }

            if (userObjectCount == 0)
            {
                command.CommandText = SetSimpleRecoverySql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            command.CommandText = EnsureSnapshotIsolationSql;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                command.Transaction = transaction;
                command.CommandText = CountUserObjectsSql;
                userObjectCount = Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);

                command.CommandText = userObjectCount == 0
                    ? BootstrapSchemaSql
                    : ValidateExistingSchemaSql;
                command.Parameters.Add("@schemaVersion", SqlDbType.Int).Value =
                    NewMesIngestContract.SchemaVersion;
                command.Parameters.Add("@contractVersion", SqlDbType.NVarChar, 128).Value =
                    NewMesIngestContract.Version;
                command.Parameters.Add("@keyComparison", SqlDbType.NVarChar, 128).Value =
                    NewMesIngestContract.KeyComparison;
                command.Parameters.Add("@historyEpoch", SqlDbType.UniqueIdentifier).Value =
                    HistoryEpoch.CreateNew().Value;
                await command.ExecuteNonQueryAsync(cancellationToken);

                command.Parameters.Clear();
                command.CommandText = """
                    SELECT
                        SchemaVersion,
                        ContractVersion,
                        TransportDemandKeyComparison,
                        DATALENGTH(SnapshotTokenSigningKey),
                        HistoryEpoch
                    FROM mesingest.SchemaInfo
                    WHERE Id = 1;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)
                    || reader.GetInt32(0) != NewMesIngestContract.SchemaVersion
                    || !string.Equals(reader.GetString(1), NewMesIngestContract.Version, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2), NewMesIngestContract.KeyComparison, StringComparison.Ordinal)
                    || reader.GetInt32(3) != 32
                    || reader.GetGuid(4) == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "The configured database does not contain the expected new-MesIngest schema contract.");
                }

                var historyEpoch = HistoryEpoch.FromGuid(reader.GetGuid(4));
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return historyEpoch;
            }
            catch (Exception exception)
            {
                await transaction.RollbackBestEffortAsync(exception);
                throw;
            }
        }
        finally
        {
            if (sessionLockHeld)
            {
                await using var releaseCommand = connection.CreateCommand();
                releaseCommand.CommandText = ReleaseSessionSchemaLockSql;
                await releaseCommand.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private const string AcquireSessionSchemaLockAndCountSql = """
        SET XACT_ABORT ON;

        -- Keep the lock identity stable so old and new contract binaries cannot
        -- validate or bootstrap the same database concurrently.
        DECLARE @lockResult INT;
        EXEC @lockResult = sys.sp_getapplock
            @Resource = N'mesingest.schema.contract.v1',
            @LockMode = N'Exclusive',
            @LockOwner = N'Session',
            @LockTimeout = 15000;
        IF @lockResult < 0
            THROW 51000, 'Could not acquire the new MesIngest schema contract lock.', 1;

        SELECT COUNT(*)
        FROM sys.objects
        WHERE is_ms_shipped = 0 AND parent_object_id = 0;
        """;

    private const string ReleaseSessionSchemaLockSql = """
        DECLARE @lockResult INT;
        EXEC @lockResult = sys.sp_releaseapplock
            @Resource = N'mesingest.schema.contract.v1',
            @LockOwner = N'Session';
        IF @lockResult < 0
            THROW 51000, 'Could not release the new MesIngest schema contract lock.', 1;
        """;

    private const string CountUserObjectsSql = """
        SELECT COUNT(*)
        FROM sys.objects
        WHERE is_ms_shipped = 0 AND parent_object_id = 0;
        """;

    private const string SetSimpleRecoverySql = """
        DECLARE @sql NVARCHAR(512) =
            N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET RECOVERY SIMPLE;';
        EXEC sys.sp_executesql @sql;
        """;

    private const string EnsureSnapshotIsolationSql = """
        IF (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()) <> 1
        BEGIN
            DECLARE @sql NVARCHAR(512) = N'ALTER DATABASE '
                + QUOTENAME(DB_NAME()) + N' SET ALLOW_SNAPSHOT_ISOLATION ON;';
            EXEC sys.sp_executesql @sql;
        END;
        """;

    private const string BootstrapSchemaSql = """
        SET XACT_ABORT ON;

        EXEC(N'CREATE SCHEMA mesingest AUTHORIZATION dbo;');

        CREATE TABLE mesingest.SchemaInfo
        (
            Id INT NOT NULL CONSTRAINT PK_MesIngest_SchemaInfo PRIMARY KEY,
            SchemaVersion INT NOT NULL,
            ContractVersion NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            TransportDemandKeyComparison NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SnapshotTokenSigningKey VARBINARY(32) NOT NULL,
            HistoryEpoch UNIQUEIDENTIFIER NOT NULL
                CONSTRAINT UQ_MesIngest_SchemaInfo_HistoryEpoch UNIQUE,
            EarliestAvailableHostUtc DATETIMEOFFSET(7) NULL,
            CONSTRAINT CK_MesIngest_SchemaInfo_SingleRow CHECK (Id = 1),
            CONSTRAINT CK_MesIngest_SchemaInfo_SnapshotTokenSigningKeyLength
                CHECK (DATALENGTH(SnapshotTokenSigningKey) = 32)
        );

        CREATE TABLE mesingest.HistoryCleanupState
        (
            Id TINYINT NOT NULL CONSTRAINT PK_MesIngest_HistoryCleanupState PRIMARY KEY,
            HistoryCleanupStatus NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            HistoryCleanupRunId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            HistoryCleanupLastStartedAt DATETIMEOFFSET(7) NULL,
            HistoryCleanupLastCompletedAt DATETIMEOFFSET(7) NULL,
            HistoryCleanupLastSuccessfulAt DATETIMEOFFSET(7) NULL,
            HistoryCleanupNextCheckAt DATETIMEOFFSET(7) NULL,
            HistoryCleanupLastExpiredPollTraceCount INT NOT NULL,
            HistoryCleanupLastDeletedRawObservationCount INT NOT NULL,
            HistoryCleanupLastDeletedSeriesCount INT NOT NULL,
            HistoryCleanupTotalExpiredPollTraceCount BIGINT NOT NULL,
            HistoryCleanupTotalDeletedRawObservationCount BIGINT NOT NULL,
            HistoryCleanupTotalDeletedSeriesCount BIGINT NOT NULL,
            HistoryCleanupLastFailureCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            HistoryCleanupLastFailureReason NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NULL,
            HistoryCleanupLastFailureAt DATETIMEOFFSET(7) NULL,
            HistoryCleanupLastFailureRunId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT CK_MesIngest_HistoryCleanupState_SingleRow CHECK (Id = 1)
        );

        CREATE TABLE mesingest.StoragePressureState
        (
            Id TINYINT NOT NULL CONSTRAINT PK_MesIngest_StoragePressureState PRIMARY KEY,
            StoragePressureStatus NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            HistoryEpoch UNIQUEIDENTIFIER NOT NULL,
            DatabaseName NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DatabaseFilePath NVARCHAR(1024) NOT NULL,
            VolumeRoot NVARCHAR(512) NOT NULL,
            TotalBytes BIGINT NOT NULL,
            AvailableBytes BIGINT NOT NULL,
            ObservedAt DATETIMEOFFSET(7) NULL,
            PausedAt DATETIMEOFFSET(7) NULL,
            PauseId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            PauseReason NVARCHAR(256) NULL,
            RecoveryAuditId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT CK_MesIngest_StoragePressureState_SingleRow CHECK (Id = 1),
            CONSTRAINT FK_MesIngest_StoragePressureState_HistoryEpoch
                FOREIGN KEY (HistoryEpoch) REFERENCES mesingest.SchemaInfo (HistoryEpoch)
        );

        CREATE TABLE mesingest.StoragePressureRecoveryAudits
        (
            RecoveryAuditId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_StoragePressureRecoveryAudits PRIMARY KEY,
            PauseId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT UQ_MesIngest_StoragePressureRecoveryAudits_PauseId UNIQUE,
            HistoryEpoch UNIQUEIDENTIFIER NOT NULL,
            DatabaseName NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ExecutionIdentity NVARCHAR(256) NOT NULL,
            DatabaseHost NVARCHAR(256) NOT NULL,
            Reason NVARCHAR(512) NOT NULL,
            StatusBefore NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            StatusAfter NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RecoveredAt DATETIMEOFFSET(7) NOT NULL,
            VolumeRoot NVARCHAR(512) NOT NULL,
            TotalBytes BIGINT NOT NULL,
            AvailableBytes BIGINT NOT NULL,
            CONSTRAINT FK_MesIngest_StoragePressureRecoveryAudits_HistoryEpoch
                FOREIGN KEY (HistoryEpoch) REFERENCES mesingest.SchemaInfo (HistoryEpoch)
        );

        CREATE TABLE mesingest.PollTraces
        (
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_PollTraces PRIMARY KEY,
            PollTraceSequence BIGINT IDENTITY(1,1) NOT NULL,
            QueryVersion NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Outcome NVARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
            StartedAt DATETIMEOFFSET(7) NOT NULL,
            CompletedAt DATETIMEOFFSET(7) NOT NULL,
            [RowCount] INT NOT NULL,
            ContentDigest CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DiagnosticStage NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            DiagnosticCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            DiagnosticSafeDetail NVARCHAR(512) NULL,
            RawObservationsExpiredAt DATETIMEOFFSET(7) NULL,
            CONSTRAINT UQ_MesIngest_PollTraces_Sequence UNIQUE (PollTraceSequence),
            CONSTRAINT CK_MesIngest_PollTraces_Outcome
                CHECK (Outcome IN (N'SUCCESS', N'FAILURE', N'INCOMPLETE')),
            CONSTRAINT CK_MesIngest_PollTraces_RowCount
                CHECK ([RowCount] BETWEEN 0 AND 25000),
            CONSTRAINT CK_MesIngest_PollTraces_Diagnostic CHECK
            (
                (DiagnosticStage IS NULL AND DiagnosticCode IS NULL AND DiagnosticSafeDetail IS NULL)
                OR
                (Outcome <> N'SUCCESS' AND DiagnosticStage IS NOT NULL
                    AND DiagnosticCode IS NOT NULL AND DiagnosticSafeDetail IS NOT NULL)
            )
        );
        CREATE INDEX IX_MesIngest_PollTraces_CompletedAt
            ON mesingest.PollTraces (CompletedAt, PollTraceId)
            INCLUDE ([RowCount]);
        CREATE INDEX IX_MesIngest_PollTraces_RawRetentionDue
            ON mesingest.PollTraces (CompletedAt, PollTraceId)
            INCLUDE ([RowCount])
            WHERE RawObservationsExpiredAt IS NULL;

        CREATE TABLE mesingest.ProjectionCommits
        (
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_ProjectionCommits PRIMARY KEY,
            ProjectionSequence BIGINT IDENTITY(1,1) NOT NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CommittedAt DATETIMEOFFSET(7) NOT NULL,
            HostSessionId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RestartPhaseBefore NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            RestartPhaseAfter NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            AbsenceAuthority BIT NOT NULL,
            CatalogRevision BIGINT NOT NULL,
            HistoryEpoch UNIQUEIDENTIFIER NOT NULL,
            OverviewActiveErrorSeriesCount BIGINT NOT NULL,
            OverviewPrior7DaysErrorSeriesCount BIGINT NOT NULL,
            CONSTRAINT UQ_MesIngest_ProjectionCommits_Sequence UNIQUE (ProjectionSequence),
            CONSTRAINT UQ_MesIngest_ProjectionCommits_PollTrace UNIQUE (PollTraceId),
            CONSTRAINT FK_MesIngest_ProjectionCommits_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_ProjectionCommits_HistoryEpoch
                FOREIGN KEY (HistoryEpoch) REFERENCES mesingest.SchemaInfo (HistoryEpoch)
        );

        CREATE TABLE mesingest.ProjectionCommitUnassignedObservationFacts
        (
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_ProjectionCommitUnassignedObservationFacts PRIMARY KEY,
            ObservationCount INT NOT NULL,
            ContentDigest CHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            StateEventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            StateChangedAt DATETIMEOFFSET(7) NULL,
            CONSTRAINT FK_MesIngest_ProjectionCommitUnassignedObservationFacts_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_ProjectionCommitUnassignedObservationFacts_State
                CHECK ((ObservationCount = 0 AND ContentDigest IS NULL)
                    OR (ObservationCount > 0 AND ContentDigest IS NOT NULL))
        );

        CREATE TABLE mesingest.UnassignedMesObservationEvents
        (
            EventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_UnassignedMesObservationEvents PRIMARY KEY,
            EventType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OccurredAt DATETIMEOFFSET(7) NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            BeforeObservationCount INT NOT NULL,
            AfterObservationCount INT NOT NULL,
            BeforeContentDigest CHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            AfterContentDigest CHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT UQ_MesIngest_UnassignedMesObservationEvents_Commit
                UNIQUE (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_UnassignedMesObservationEvents_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_UnassignedMesObservationEvents_EventType
                CHECK (EventType IN
                    (N'UNASSIGNED_MES_OBSERVATION_APPEARED',
                     N'UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED',
                     N'UNASSIGNED_MES_OBSERVATION_CLEARED')),
            CONSTRAINT CK_MesIngest_UnassignedMesObservationEvents_BeforeState
                CHECK ((BeforeObservationCount = 0 AND BeforeContentDigest IS NULL)
                    OR (BeforeObservationCount > 0 AND BeforeContentDigest IS NOT NULL)),
            CONSTRAINT CK_MesIngest_UnassignedMesObservationEvents_AfterState
                CHECK ((AfterObservationCount = 0 AND AfterContentDigest IS NULL)
                    OR (AfterObservationCount > 0 AND AfterContentDigest IS NOT NULL)),
            CONSTRAINT CK_MesIngest_UnassignedMesObservationEvents_Transition
                CHECK ((EventType = N'UNASSIGNED_MES_OBSERVATION_APPEARED'
                            AND BeforeObservationCount = 0 AND AfterObservationCount > 0)
                    OR (EventType = N'UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED'
                            AND BeforeObservationCount > 0 AND AfterObservationCount > 0
                            AND BeforeContentDigest <> AfterContentDigest)
                    OR (EventType = N'UNASSIGNED_MES_OBSERVATION_CLEARED'
                            AND BeforeObservationCount > 0 AND AfterObservationCount = 0))
        );
        CREATE INDEX IX_MesIngest_UnassignedMesObservationEvents_Overview
            ON mesingest.UnassignedMesObservationEvents (OccurredAt DESC, EventId)
            INCLUDE (EventType, ProjectionCommitId, BeforeObservationCount,
                     AfterObservationCount, BeforeContentDigest, AfterContentDigest);

        CREATE TABLE mesingest.CurrentOverviewAreaFacts
        (
            AreaKey CHAR(65) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_CurrentOverviewAreaFacts PRIMARY KEY,
            IsGlobal BIT NOT NULL,
            Area NVARCHAR(512) COLLATE Latin1_General_100_BIN2 NULL,
            ExactTotalDemandCount BIGINT NOT NULL,
            ReadableCount BIGINT NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
        );

        CREATE TABLE mesingest.CurrentOverviewActivities
        (
            ActivityRank TINYINT NOT NULL
                CONSTRAINT PK_MesIngest_CurrentOverviewActivities PRIMARY KEY,
            SnapshotProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            EventId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Kind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EventType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Severity NVARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OccurredAt DATETIMEOFFSET(7) NOT NULL,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SourceProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            NavigationTarget NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
        );

        CREATE TABLE mesingest.HostSessions
        (
            HostSessionId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_HostSessions PRIMARY KEY,
            StartedAt DATETIMEOFFSET(7) NOT NULL,
            RestartPhase NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            IsCurrent BIT NOT NULL,
            CONSTRAINT CK_MesIngest_HostSessions_RestartPhase
                CHECK (RestartPhase IN (N'BARRIER', N'POST_BARRIER', N'NORMAL'))
        );
        ALTER TABLE mesingest.ProjectionCommits
        ADD CONSTRAINT FK_MesIngest_ProjectionCommits_HostSession
            FOREIGN KEY (HostSessionId) REFERENCES mesingest.HostSessions (HostSessionId);

        CREATE TABLE mesingest.AbsenceAuthorityEvents
        (
            EventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_AbsenceAuthorityEvents PRIMARY KEY,
            HostSessionId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EventType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OccurredAt DATETIMEOFFSET(7) NOT NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            PhaseBefore NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PhaseAfter NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT FK_MesIngest_AbsenceAuthorityEvents_HostSession
                FOREIGN KEY (HostSessionId) REFERENCES mesingest.HostSessions (HostSessionId),
            CONSTRAINT FK_MesIngest_AbsenceAuthorityEvents_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_AbsenceAuthorityEvents_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId)
        );

        CREATE TABLE mesingest.TaskTypeProtectionStates
        (
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_TaskTypeProtectionStates PRIMARY KEY,
            Phase NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LastHealthyNonZeroCount INT NOT NULL,
            LatestObservedCount INT NOT NULL,
            RecoveryStreak INT NOT NULL,
            EpisodeId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            EnteredAt DATETIMEOFFSET(7) NULL,
            LastSequence BIGINT NOT NULL,
            EnterThreshold INT NOT NULL,
            ProtectionAllowsAbsenceAuthority BIT NOT NULL,
            EffectiveAbsenceAuthorityAvailable BIT NOT NULL,
            LatestPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LatestProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT FK_MesIngest_TaskTypeProtectionStates_PollTrace
                FOREIGN KEY (LatestPollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_TaskTypeProtectionStates_Commit
                FOREIGN KEY (LatestProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_Phase
                CHECK (Phase IN (N'MONITORING', N'PAUSED_ZERO_DROP', N'RECOVERING', N'AUTHORITY_PENDING')),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_LastHealthyCount
                CHECK (LastHealthyNonZeroCount >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_LatestCount
                CHECK (LatestObservedCount >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_RecoveryStreak
                CHECK (RecoveryStreak >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_LastSequence
                CHECK (LastSequence >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_EnterThreshold
                CHECK (EnterThreshold >= 1),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_EffectiveAuthority
                CHECK (EffectiveAbsenceAuthorityAvailable <= ProtectionAllowsAbsenceAuthority),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionStates_Episode
                CHECK ((EpisodeId IS NULL AND EnteredAt IS NULL)
                    OR (EpisodeId IS NOT NULL AND EnteredAt IS NOT NULL))
        );

        CREATE TABLE mesingest.TaskTypeProtectionEvents
        (
            EventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_TaskTypeProtectionEvents PRIMARY KEY,
            EpisodeId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            WorkTypeSequence BIGINT NOT NULL,
            EventType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OccurredAt DATETIMEOFFSET(7) NOT NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PhaseBefore NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PhaseAfter NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ObservedCount INT NOT NULL,
            LastHealthyNonZeroCount INT NOT NULL,
            RecoveryStreak INT NOT NULL,
            RequiredRecoveryStreak INT NOT NULL,
            EnterThreshold INT NOT NULL,
            CONSTRAINT UQ_MesIngest_TaskTypeProtectionEvents_WorkTypeSequence
                UNIQUE (WorkType, WorkTypeSequence),
            CONSTRAINT FK_MesIngest_TaskTypeProtectionEvents_State
                FOREIGN KEY (WorkType) REFERENCES mesingest.TaskTypeProtectionStates (WorkType),
            CONSTRAINT FK_MesIngest_TaskTypeProtectionEvents_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_TaskTypeProtectionEvents_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_Sequence
                CHECK (WorkTypeSequence >= 1),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_ObservedCount
                CHECK (ObservedCount >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_LastHealthyCount
                CHECK (LastHealthyNonZeroCount >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_RecoveryStreak
                CHECK (RecoveryStreak >= 0),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_RequiredRecoveryStreak
                CHECK (RequiredRecoveryStreak >= 1),
            CONSTRAINT CK_MesIngest_TaskTypeProtectionEvents_EnterThreshold
                CHECK (EnterThreshold >= 1)
        );
        CREATE INDEX IX_MesIngest_TaskTypeProtectionEvents_Commit
            ON mesingest.TaskTypeProtectionEvents
                (ProjectionCommitId, WorkType, WorkTypeSequence);

        CREATE TABLE mesingest.ProjectionCommitTaskTypeProtectionDecisions
        (
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PhaseBefore NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PhaseAfter NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ObservedCount INT NOT NULL,
            LastHealthyNonZeroCount INT NOT NULL,
            RecoveryStreakBefore INT NOT NULL,
            RecoveryStreakAfter INT NOT NULL,
            ProtectionAllowsAbsenceAuthority BIT NOT NULL,
            EffectiveAbsenceAuthorityAvailable BIT NOT NULL,
            CONSTRAINT PK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions
                PRIMARY KEY (ProjectionCommitId, WorkType),
            CONSTRAINT FK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_State
                FOREIGN KEY (WorkType) REFERENCES mesingest.TaskTypeProtectionStates (WorkType),
            CONSTRAINT CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_ObservedCount
                CHECK (ObservedCount >= 0),
            CONSTRAINT CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_LastHealthyCount
                CHECK (LastHealthyNonZeroCount >= 0),
            CONSTRAINT CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_StreakBefore
                CHECK (RecoveryStreakBefore >= 0),
            CONSTRAINT CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_StreakAfter
                CHECK (RecoveryStreakAfter >= 0),
            CONSTRAINT CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_EffectiveAuthority
                CHECK (EffectiveAbsenceAuthorityAvailable <= ProtectionAllowsAbsenceAuthority)
        );

        CREATE TABLE mesingest.DemandSeries
        (
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_DemandSeries PRIMARY KEY,
            KeyToken CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Lifecycle NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CurrentPresence NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            StartedAt DATETIMEOFFSET(7) NULL,
            ArchivedAt DATETIMEOFFSET(7) NULL,
            CreatedPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CreatedProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LatestProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CurrentDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            LastSeriesSequence BIGINT NOT NULL,
            RetentionEligibilityAt DATETIMEOFFSET(7) NULL,
            CONSTRAINT UQ_MesIngest_DemandSeries_KeyToken UNIQUE (KeyToken),
            CONSTRAINT FK_MesIngest_DemandSeries_CreatedPollTrace
                FOREIGN KEY (CreatedPollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_DemandSeries_CreatedCommit
                FOREIGN KEY (CreatedProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_DemandSeries_LatestCommit
                FOREIGN KEY (LatestProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_DemandSeries_LastSequence CHECK (LastSeriesSequence >= 0)
        );
        CREATE INDEX IX_MesIngest_DemandSeries_RetentionEligibilityAt
            ON mesingest.DemandSeries (RetentionEligibilityAt, SeriesId)
            WHERE RetentionEligibilityAt IS NOT NULL;

        CREATE TABLE mesingest.ArchivedDemandKeyTombstones
        (
            KeyToken CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_ArchivedDemandKeyTombstones PRIMARY KEY,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OriginalSeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT UQ_MesIngest_ArchivedDemandKeyTombstones_OriginalSeries UNIQUE,
            ArchivedAt DATETIMEOFFSET(7) NOT NULL,
            ArchiveConclusion NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            TombstoneVersion INT NOT NULL,
            CONSTRAINT CK_MesIngest_ArchivedDemandKeyTombstones_ArchiveConclusion
                CHECK (ArchiveConclusion = N'ARCHIVED'),
            CONSTRAINT CK_MesIngest_ArchivedDemandKeyTombstones_Version
                CHECK (TombstoneVersion = 1)
        );

        CREATE TABLE mesingest.CurrentOverviewErrorSeriesFacts
        (
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_CurrentOverviewErrorSeriesFacts PRIMARY KEY,
            EarliestActiveStartedAt DATETIMEOFFSET(7) NULL,
            LatestEndedPeriodStartedAt DATETIMEOFFSET(7) NULL,
            LatestEndedAt DATETIMEOFFSET(7) NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT FK_MesIngest_CurrentOverviewErrorSeriesFacts_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_CurrentOverviewErrorSeriesFacts_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_CurrentOverviewErrorSeriesFacts_EndedPair
                CHECK ((LatestEndedPeriodStartedAt IS NULL AND LatestEndedAt IS NULL)
                    OR (LatestEndedPeriodStartedAt IS NOT NULL AND LatestEndedAt IS NOT NULL))
        );

        CREATE TABLE mesingest.TransportDemands
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_TransportDemands PRIMARY KEY,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Generation INT NOT NULL,
            PredecessorDemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            Status NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL,
            DemandLastSeenAt DATETIMEOFFSET(7) NOT NULL,
            GoneConfirmedAt DATETIMEOFFSET(7) NULL,
            CreatedPollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CreatedProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LatestProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LatestObservationProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CurrentRawObservationCount INT NOT NULL,
            DemandRevision BIGINT NOT NULL,
            ValueObservedAt DATETIMEOFFSET(7) NOT NULL,
            Area NVARCHAR(512) NULL,
            Eqp NVARCHAR(512) NULL,
            Step NVARCHAR(512) NULL,
            MesSourceDate DATETIMEOFFSET(7) NULL,
            Package NVARCHAR(512) NULL,
            CONSTRAINT UQ_MesIngest_TransportDemands_SeriesGeneration
                UNIQUE (SeriesId, Generation),
            CONSTRAINT FK_MesIngest_TransportDemands_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_TransportDemands_Predecessor
                FOREIGN KEY (PredecessorDemandId) REFERENCES mesingest.TransportDemands (DemandId),
            CONSTRAINT FK_MesIngest_TransportDemands_CreatedPollTrace
                FOREIGN KEY (CreatedPollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_TransportDemands_CreatedCommit
                FOREIGN KEY (CreatedProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_TransportDemands_LatestCommit
                FOREIGN KEY (LatestProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_TransportDemands_LatestObservationCommit
                FOREIGN KEY (LatestObservationProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_TransportDemands_Generation CHECK (Generation >= 1)
            ,CONSTRAINT CK_MesIngest_TransportDemands_CurrentRawObservationCount
                CHECK (CurrentRawObservationCount >= 1)
            ,CONSTRAINT CK_MesIngest_TransportDemands_DemandRevision CHECK (DemandRevision >= 1)
        );

        ALTER TABLE mesingest.DemandSeries
        ADD CONSTRAINT FK_MesIngest_DemandSeries_CurrentDemand
            FOREIGN KEY (CurrentDemandId) REFERENCES mesingest.TransportDemands (DemandId);

        CREATE TABLE mesingest.DemandRawObservations
        (
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Ordinal INT NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NULL,
            Area NVARCHAR(512) NULL,
            Eqp NVARCHAR(512) NULL,
            Step NVARCHAR(512) NULL,
            MesSourceDate DATETIMEOFFSET(7) NULL,
            Package NVARCHAR(512) NULL,
            MesSourceDateRaw NVARCHAR(128) NULL,
            CONSTRAINT PK_MesIngest_DemandRawObservations PRIMARY KEY (PollTraceId, Ordinal)
                WITH (DATA_COMPRESSION = PAGE),
            CONSTRAINT FK_MesIngest_DemandRawObservations_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_DemandRawObservations_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_DemandRawObservations_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_DemandRawObservations_Demand
                FOREIGN KEY (DemandId) REFERENCES mesingest.TransportDemands (DemandId),
            CONSTRAINT CK_MesIngest_DemandRawObservations_Ordinal CHECK (Ordinal >= 0)
        );
        CREATE INDEX IX_MesIngest_DemandRawObservations_Series
            ON mesingest.DemandRawObservations (SeriesId, PollTraceId, Ordinal)
            WITH (DATA_COMPRESSION = PAGE);
        CREATE INDEX IX_MesIngest_DemandRawObservations_Demand
            ON mesingest.DemandRawObservations (DemandId, ProjectionCommitId)
            INCLUDE (Area)
            WITH (DATA_COMPRESSION = PAGE);

        CREATE TABLE mesingest.DemandSeriesEvents
        (
            EventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_DemandSeriesEvents PRIMARY KEY,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SeriesSequence BIGINT NOT NULL,
            EventType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OccurredAt DATETIMEOFFSET(7) NOT NULL,
            SubjectKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SubjectId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PayloadVersion INT NOT NULL,
            Payload NVARCHAR(MAX) NOT NULL,
            CONSTRAINT UQ_MesIngest_DemandSeriesEvents_SeriesSequence
                UNIQUE (SeriesId, SeriesSequence),
            CONSTRAINT FK_MesIngest_DemandSeriesEvents_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_DemandSeriesEvents_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_DemandSeriesEvents_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_DemandSeriesEvents_Sequence CHECK (SeriesSequence >= 1),
            CONSTRAINT CK_MesIngest_DemandSeriesEvents_PayloadVersion CHECK (PayloadVersion >= 1),
            CONSTRAINT CK_MesIngest_DemandSeriesEvents_PayloadJson CHECK (ISJSON(Payload) = 1)
        );

        CREATE TABLE mesingest.DemandSeriesErrorPeriods
        (
            PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_DemandSeriesErrorPeriods PRIMARY KEY,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ErrorCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Category NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Severity NVARCHAR(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Target NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SubjectKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            StartReason NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            StartedAt DATETIMEOFFSET(7) NOT NULL,
            EndedAt DATETIMEOFFSET(7) NULL,
            EndReason NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            OpenedEventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ClosedEventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT FK_MesIngest_DemandSeriesErrorPeriods_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_DemandSeriesErrorPeriods_OpenedEvent
                FOREIGN KEY (OpenedEventId) REFERENCES mesingest.DemandSeriesEvents (EventId),
            CONSTRAINT FK_MesIngest_DemandSeriesErrorPeriods_ClosedEvent
                FOREIGN KEY (ClosedEventId) REFERENCES mesingest.DemandSeriesEvents (EventId),
            CONSTRAINT CK_MesIngest_DemandSeriesErrorPeriods_EndPair
                CHECK ((EndedAt IS NULL AND EndReason IS NULL AND ClosedEventId IS NULL)
                    OR (EndedAt IS NOT NULL AND EndReason IS NOT NULL AND ClosedEventId IS NOT NULL))
        );

        CREATE TABLE mesingest.SeriesErrorPeriodEvidence
        (
            EvidenceId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_SeriesErrorPeriodEvidence PRIMARY KEY,
            PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EventId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EvidenceKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ObservedAt DATETIMEOFFSET(7) NOT NULL,
            PollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ObservedValue NVARCHAR(MAX) NULL,
            ExpectedRule NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT FK_MesIngest_SeriesErrorPeriodEvidence_Period
                FOREIGN KEY (PeriodId) REFERENCES mesingest.DemandSeriesErrorPeriods (PeriodId),
            CONSTRAINT FK_MesIngest_SeriesErrorPeriodEvidence_Event
                FOREIGN KEY (EventId) REFERENCES mesingest.DemandSeriesEvents (EventId),
            CONSTRAINT FK_MesIngest_SeriesErrorPeriodEvidence_PollTrace
                FOREIGN KEY (PollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_SeriesErrorPeriodEvidence_Commit
                FOREIGN KEY (ProjectionCommitId) REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT FK_MesIngest_SeriesErrorPeriodEvidence_Demand
                FOREIGN KEY (DemandId) REFERENCES mesingest.TransportDemands (DemandId)
        );
        CREATE INDEX IX_MesIngest_SeriesErrorPeriodEvidence_Period
            ON mesingest.SeriesErrorPeriodEvidence (PeriodId, ObservedAt, EvidenceId);
        CREATE INDEX IX_MesIngest_DemandSeriesErrorPeriods_Search
            ON mesingest.DemandSeriesErrorPeriods
                (Category, ErrorCode, StartedAt, SeriesId)
            INCLUDE (PeriodId, EndedAt, OpenedEventId, ClosedEventId, Severity);
        CREATE INDEX IX_MesIngest_SeriesErrorPeriodEvidence_Demand
            ON mesingest.SeriesErrorPeriodEvidence
                (DemandId, PeriodId, ObservedAt, EvidenceId)
            INCLUDE (ProjectionCommitId);

        CREATE TABLE mesingest.DemandSeriesCurrentConditions
        (
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ErrorCode NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Target NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SubjectKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PeriodId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            LatestEvidenceId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT PK_MesIngest_DemandSeriesCurrentConditions
                PRIMARY KEY (SeriesId, ErrorCode, Target, SubjectKind),
            CONSTRAINT UQ_MesIngest_DemandSeriesCurrentConditions_Period UNIQUE (PeriodId),
            CONSTRAINT FK_MesIngest_DemandSeriesCurrentConditions_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_DemandSeriesCurrentConditions_Period
                FOREIGN KEY (PeriodId) REFERENCES mesingest.DemandSeriesErrorPeriods (PeriodId),
            CONSTRAINT FK_MesIngest_DemandSeriesCurrentConditions_Evidence
                FOREIGN KEY (LatestEvidenceId) REFERENCES mesingest.SeriesErrorPeriodEvidence (EvidenceId)
        );

        CREATE TABLE mesingest.CatalogState
        (
            Id INT NOT NULL CONSTRAINT PK_MesIngest_CatalogState PRIMARY KEY,
            CatalogRevision BIGINT NOT NULL,
            ProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL,
            CONSTRAINT CK_MesIngest_CatalogState_SingleRow CHECK (Id = 1),
            CONSTRAINT CK_MesIngest_CatalogState_Revision CHECK (CatalogRevision >= 0),
            CONSTRAINT FK_MesIngest_CatalogState_Commit
                FOREIGN KEY (ProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId)
        );

        CREATE TABLE mesingest.CatalogItems
        (
            DemandId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL
                CONSTRAINT PK_MesIngest_CatalogItems PRIMARY KEY,
            SeriesId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            WorkType NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Sublot NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Generation INT NOT NULL,
            DemandRevision BIGINT NOT NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL,
            ValueObservedAt DATETIMEOFFSET(7) NOT NULL,
            ValuePollTraceId NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ValueProjectionCommitId NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Area NVARCHAR(512) NOT NULL,
            Eqp NVARCHAR(512) NOT NULL,
            Step NVARCHAR(512) NOT NULL,
            MesSourceDate DATETIMEOFFSET(7) NOT NULL,
            Package NVARCHAR(512) NOT NULL,
            CONSTRAINT FK_MesIngest_CatalogItems_Demand
                FOREIGN KEY (DemandId) REFERENCES mesingest.TransportDemands (DemandId),
            CONSTRAINT FK_MesIngest_CatalogItems_Series
                FOREIGN KEY (SeriesId) REFERENCES mesingest.DemandSeries (SeriesId),
            CONSTRAINT FK_MesIngest_CatalogItems_ValuePollTrace
                FOREIGN KEY (ValuePollTraceId) REFERENCES mesingest.PollTraces (PollTraceId),
            CONSTRAINT FK_MesIngest_CatalogItems_ValueCommit
                FOREIGN KEY (ValueProjectionCommitId)
                REFERENCES mesingest.ProjectionCommits (ProjectionCommitId),
            CONSTRAINT CK_MesIngest_CatalogItems_Generation CHECK (Generation >= 1),
            CONSTRAINT CK_MesIngest_CatalogItems_DemandRevision CHECK (DemandRevision >= 1)
        );

        INSERT INTO mesingest.CatalogState
            (Id, CatalogRevision, ProjectionCommitId)
        VALUES
            (1, 0, NULL);

        INSERT INTO mesingest.SchemaInfo
            (Id, SchemaVersion, ContractVersion, TransportDemandKeyComparison,
             SnapshotTokenSigningKey, HistoryEpoch, EarliestAvailableHostUtc)
        VALUES
            (1, @schemaVersion, @contractVersion, @keyComparison,
             CRYPT_GEN_RANDOM(32), @historyEpoch, NULL);

        INSERT INTO mesingest.HistoryCleanupState
            (Id, HistoryCleanupStatus, HistoryCleanupRunId,
             HistoryCleanupLastStartedAt, HistoryCleanupLastCompletedAt,
             HistoryCleanupLastSuccessfulAt, HistoryCleanupNextCheckAt,
             HistoryCleanupLastExpiredPollTraceCount,
             HistoryCleanupLastDeletedRawObservationCount,
             HistoryCleanupLastDeletedSeriesCount,
             HistoryCleanupTotalExpiredPollTraceCount,
             HistoryCleanupTotalDeletedRawObservationCount,
             HistoryCleanupTotalDeletedSeriesCount,
             HistoryCleanupLastFailureCode, HistoryCleanupLastFailureReason,
             HistoryCleanupLastFailureAt, HistoryCleanupLastFailureRunId)
        VALUES
            (1, N'NOT_RUN', NULL, NULL, NULL, NULL, NULL,
             0, 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL);

        INSERT INTO mesingest.StoragePressureState
            (Id, StoragePressureStatus, HistoryEpoch, DatabaseName,
             DatabaseFilePath, VolumeRoot, TotalBytes, AvailableBytes,
             ObservedAt, PausedAt, PauseId, PauseReason, RecoveryAuditId)
        VALUES
            (1, N'HEALTHY', @historyEpoch, DB_NAME(), N'', N'', 1, 1,
             NULL, NULL, NULL, NULL, NULL);
        """;

    private const string ValidateExistingSchemaSql = """
        SET XACT_ABORT ON;

        IF (SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME()) <> N'SIMPLE'
            THROW 51001, 'The configured new-MesIngest database must use SIMPLE recovery.', 1;

        IF (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()) <> 1
            THROW 51001, 'The configured new-MesIngest database must allow snapshot isolation.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.objects
            WHERE is_ms_shipped = 0
              AND parent_object_id = 0
              AND [type] <> N'U'
        )
            THROW 51001, 'The configured database contains an unexpected top-level user object.', 1;

        IF
        (
            SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0
        ) <> 26
        OR EXISTS
        (
            SELECT SCHEMA_NAME(t.schema_id), t.name
            FROM sys.tables AS t
            WHERE t.is_ms_shipped = 0
            EXCEPT
            SELECT N'mesingest', v.TableName
            FROM (VALUES
                (N'SchemaInfo'),
                (N'HistoryCleanupState'),
                (N'StoragePressureState'),
                (N'StoragePressureRecoveryAudits'),
                (N'PollTraces'),
                (N'ProjectionCommits'),
                (N'ProjectionCommitUnassignedObservationFacts'),
                (N'UnassignedMesObservationEvents'),
                (N'CurrentOverviewAreaFacts'),
                (N'CurrentOverviewErrorSeriesFacts'),
                (N'CurrentOverviewActivities'),
                (N'HostSessions'),
                (N'AbsenceAuthorityEvents'),
                (N'TaskTypeProtectionStates'),
                (N'TaskTypeProtectionEvents'),
                (N'ProjectionCommitTaskTypeProtectionDecisions'),
                (N'DemandSeries'),
                (N'ArchivedDemandKeyTombstones'),
                (N'TransportDemands'),
                (N'DemandRawObservations'),
                (N'DemandSeriesEvents'),
                (N'DemandSeriesErrorPeriods'),
                (N'SeriesErrorPeriodEvidence'),
                (N'DemandSeriesCurrentConditions'),
                (N'CatalogState'),
                (N'CatalogItems')
            ) AS v(TableName)
        )
        OR EXISTS
        (
            SELECT N'mesingest', v.TableName
            FROM (VALUES
                (N'SchemaInfo'),
                (N'HistoryCleanupState'),
                (N'StoragePressureState'),
                (N'StoragePressureRecoveryAudits'),
                (N'PollTraces'),
                (N'ProjectionCommits'),
                (N'ProjectionCommitUnassignedObservationFacts'),
                (N'UnassignedMesObservationEvents'),
                (N'CurrentOverviewAreaFacts'),
                (N'CurrentOverviewErrorSeriesFacts'),
                (N'CurrentOverviewActivities'),
                (N'HostSessions'),
                (N'AbsenceAuthorityEvents'),
                (N'TaskTypeProtectionStates'),
                (N'TaskTypeProtectionEvents'),
                (N'ProjectionCommitTaskTypeProtectionDecisions'),
                (N'DemandSeries'),
                (N'ArchivedDemandKeyTombstones'),
                (N'TransportDemands'),
                (N'DemandRawObservations'),
                (N'DemandSeriesEvents'),
                (N'DemandSeriesErrorPeriods'),
                (N'SeriesErrorPeriodEvidence'),
                (N'DemandSeriesCurrentConditions'),
                (N'CatalogState'),
                (N'CatalogItems')
            ) AS v(TableName)
            EXCEPT
            SELECT SCHEMA_NAME(t.schema_id), t.name
            FROM sys.tables AS t
            WHERE t.is_ms_shipped = 0
        )
            THROW 51001, 'The configured database contains an unexpected new-MesIngest table set.', 1;

        DECLARE @ExpectedColumns TABLE
        (
            TableName SYSNAME NOT NULL,
            ColumnId INT NOT NULL,
            ColumnName SYSNAME NOT NULL,
            TypeName SYSNAME NOT NULL,
            MaxLength SMALLINT NOT NULL,
            [Precision] TINYINT NOT NULL,
            Scale TINYINT NOT NULL,
            IsNullable BIT NOT NULL,
            CollationName SYSNAME NULL
        );

        INSERT INTO @ExpectedColumns
            (TableName, ColumnId, ColumnName, TypeName, MaxLength, [Precision], Scale, IsNullable, CollationName)
        VALUES
            (N'SchemaInfo', 1, N'Id', N'int', 4, 10, 0, 0, NULL),
            (N'SchemaInfo', 2, N'SchemaVersion', N'int', 4, 10, 0, 0, NULL),
            (N'SchemaInfo', 3, N'ContractVersion', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SchemaInfo', 4, N'TransportDemandKeyComparison', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SchemaInfo', 5, N'SnapshotTokenSigningKey', N'varbinary', 32, 0, 0, 0, NULL),
            (N'SchemaInfo', 6, N'HistoryEpoch', N'uniqueidentifier', 16, 0, 0, 0, NULL),
            (N'SchemaInfo', 7, N'EarliestAvailableHostUtc', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 1, N'Id', N'tinyint', 1, 3, 0, 0, NULL),
            (N'HistoryCleanupState', 2, N'HistoryCleanupStatus', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'HistoryCleanupState', 3, N'HistoryCleanupRunId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'HistoryCleanupState', 4, N'HistoryCleanupLastStartedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 5, N'HistoryCleanupLastCompletedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 6, N'HistoryCleanupLastSuccessfulAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 7, N'HistoryCleanupNextCheckAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 8, N'HistoryCleanupLastExpiredPollTraceCount', N'int', 4, 10, 0, 0, NULL),
            (N'HistoryCleanupState', 9, N'HistoryCleanupLastDeletedRawObservationCount', N'int', 4, 10, 0, 0, NULL),
            (N'HistoryCleanupState', 10, N'HistoryCleanupLastDeletedSeriesCount', N'int', 4, 10, 0, 0, NULL),
            (N'HistoryCleanupState', 11, N'HistoryCleanupTotalExpiredPollTraceCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'HistoryCleanupState', 12, N'HistoryCleanupTotalDeletedRawObservationCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'HistoryCleanupState', 13, N'HistoryCleanupTotalDeletedSeriesCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'HistoryCleanupState', 14, N'HistoryCleanupLastFailureCode', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'HistoryCleanupState', 15, N'HistoryCleanupLastFailureReason', N'nvarchar', 512, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'HistoryCleanupState', 16, N'HistoryCleanupLastFailureAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'HistoryCleanupState', 17, N'HistoryCleanupLastFailureRunId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),

            (N'StoragePressureState', 1, N'Id', N'tinyint', 1, 3, 0, 0, NULL),
            (N'StoragePressureState', 2, N'StoragePressureStatus', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureState', 3, N'HistoryEpoch', N'uniqueidentifier', 16, 0, 0, 0, NULL),
            (N'StoragePressureState', 4, N'DatabaseName', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureState', 5, N'DatabaseFilePath', N'nvarchar', 2048, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureState', 6, N'VolumeRoot', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureState', 7, N'TotalBytes', N'bigint', 8, 19, 0, 0, NULL),
            (N'StoragePressureState', 8, N'AvailableBytes', N'bigint', 8, 19, 0, 0, NULL),
            (N'StoragePressureState', 9, N'ObservedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'StoragePressureState', 10, N'PausedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'StoragePressureState', 11, N'PauseId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'StoragePressureState', 12, N'PauseReason', N'nvarchar', 512, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureState', 13, N'RecoveryAuditId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),

            (N'StoragePressureRecoveryAudits', 1, N'RecoveryAuditId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureRecoveryAudits', 2, N'PauseId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureRecoveryAudits', 3, N'HistoryEpoch', N'uniqueidentifier', 16, 0, 0, 0, NULL),
            (N'StoragePressureRecoveryAudits', 4, N'DatabaseName', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureRecoveryAudits', 5, N'ExecutionIdentity', N'nvarchar', 512, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureRecoveryAudits', 6, N'DatabaseHost', N'nvarchar', 512, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureRecoveryAudits', 7, N'Reason', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureRecoveryAudits', 8, N'StatusBefore', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureRecoveryAudits', 9, N'StatusAfter', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'StoragePressureRecoveryAudits', 10, N'RecoveredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'StoragePressureRecoveryAudits', 11, N'VolumeRoot', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'StoragePressureRecoveryAudits', 12, N'TotalBytes', N'bigint', 8, 19, 0, 0, NULL),
            (N'StoragePressureRecoveryAudits', 13, N'AvailableBytes', N'bigint', 8, 19, 0, 0, NULL),

            (N'PollTraces', 1, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 2, N'PollTraceSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'PollTraces', 3, N'QueryVersion', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 4, N'Outcome', N'nvarchar', 32, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 5, N'StartedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'PollTraces', 6, N'CompletedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'PollTraces', 7, N'RowCount', N'int', 4, 10, 0, 0, NULL),
            (N'PollTraces', 8, N'ContentDigest', N'char', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 9, N'DiagnosticStage', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 10, N'DiagnosticCode', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'PollTraces', 11, N'DiagnosticSafeDetail', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'PollTraces', 12, N'RawObservationsExpiredAt', N'datetimeoffset', 10, 34, 7, 1, NULL),

            (N'ProjectionCommits', 1, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommits', 2, N'ProjectionSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'ProjectionCommits', 3, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommits', 4, N'CommittedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'ProjectionCommits', 5, N'HostSessionId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommits', 6, N'RestartPhaseBefore', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommits', 7, N'RestartPhaseAfter', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommits', 8, N'AbsenceAuthority', N'bit', 1, 1, 0, 0, NULL),
            (N'ProjectionCommits', 9, N'CatalogRevision', N'bigint', 8, 19, 0, 0, NULL),
            (N'ProjectionCommits', 10, N'HistoryEpoch', N'uniqueidentifier', 16, 0, 0, 0, NULL),
            (N'ProjectionCommits', 11, N'OverviewActiveErrorSeriesCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'ProjectionCommits', 12, N'OverviewPrior7DaysErrorSeriesCount', N'bigint', 8, 19, 0, 0, NULL),

            (N'ProjectionCommitUnassignedObservationFacts', 1, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitUnassignedObservationFacts', 2, N'ObservationCount', N'int', 4, 10, 0, 0, NULL),
            (N'ProjectionCommitUnassignedObservationFacts', 3, N'ContentDigest', N'char', 64, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitUnassignedObservationFacts', 4, N'StateEventId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitUnassignedObservationFacts', 5, N'StateChangedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),

            (N'UnassignedMesObservationEvents', 1, N'EventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'UnassignedMesObservationEvents', 2, N'EventType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'UnassignedMesObservationEvents', 3, N'OccurredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'UnassignedMesObservationEvents', 4, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'UnassignedMesObservationEvents', 5, N'BeforeObservationCount', N'int', 4, 10, 0, 0, NULL),
            (N'UnassignedMesObservationEvents', 6, N'AfterObservationCount', N'int', 4, 10, 0, 0, NULL),
            (N'UnassignedMesObservationEvents', 7, N'BeforeContentDigest', N'char', 64, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'UnassignedMesObservationEvents', 8, N'AfterContentDigest', N'char', 64, 0, 0, 1, N'Latin1_General_100_BIN2'),

            (N'CurrentOverviewAreaFacts', 1, N'AreaKey', N'char', 65, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewAreaFacts', 2, N'IsGlobal', N'bit', 1, 1, 0, 0, NULL),
            (N'CurrentOverviewAreaFacts', 3, N'Area', N'nvarchar', 1024, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewAreaFacts', 4, N'ExactTotalDemandCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'CurrentOverviewAreaFacts', 5, N'ReadableCount', N'bigint', 8, 19, 0, 0, NULL),
            (N'CurrentOverviewAreaFacts', 6, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'CurrentOverviewErrorSeriesFacts', 1, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewErrorSeriesFacts', 2, N'EarliestActiveStartedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'CurrentOverviewErrorSeriesFacts', 3, N'LatestEndedPeriodStartedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'CurrentOverviewErrorSeriesFacts', 4, N'LatestEndedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'CurrentOverviewErrorSeriesFacts', 5, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'CurrentOverviewActivities', 1, N'ActivityRank', N'tinyint', 1, 3, 0, 0, NULL),
            (N'CurrentOverviewActivities', 2, N'SnapshotProjectionCommitId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 3, N'EventId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 4, N'Kind', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 5, N'EventType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 6, N'Severity', N'nvarchar', 32, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 7, N'OccurredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'CurrentOverviewActivities', 8, N'SeriesId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 9, N'WorkType', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 10, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 11, N'SourceProjectionCommitId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'CurrentOverviewActivities', 12, N'NavigationTarget', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'HostSessions', 1, N'HostSessionId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'HostSessions', 2, N'StartedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'HostSessions', 3, N'RestartPhase', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'HostSessions', 4, N'IsCurrent', N'bit', 1, 1, 0, 0, NULL),

            (N'AbsenceAuthorityEvents', 1, N'EventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 2, N'HostSessionId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 3, N'EventType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 4, N'OccurredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'AbsenceAuthorityEvents', 5, N'PollTraceId', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 6, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 7, N'PhaseBefore', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'AbsenceAuthorityEvents', 8, N'PhaseAfter', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'TaskTypeProtectionStates', 1, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionStates', 2, N'Phase', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionStates', 3, N'LastHealthyNonZeroCount', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 4, N'LatestObservedCount', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 5, N'RecoveryStreak', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 6, N'EpisodeId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionStates', 7, N'EnteredAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'TaskTypeProtectionStates', 8, N'LastSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 9, N'EnterThreshold', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 10, N'ProtectionAllowsAbsenceAuthority', N'bit', 1, 1, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 11, N'EffectiveAbsenceAuthorityAvailable', N'bit', 1, 1, 0, 0, NULL),
            (N'TaskTypeProtectionStates', 12, N'LatestPollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionStates', 13, N'LatestProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'TaskTypeProtectionEvents', 1, N'EventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 2, N'EpisodeId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 3, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 4, N'WorkTypeSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'TaskTypeProtectionEvents', 5, N'EventType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 6, N'OccurredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'TaskTypeProtectionEvents', 7, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 8, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 9, N'PhaseBefore', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 10, N'PhaseAfter', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TaskTypeProtectionEvents', 11, N'ObservedCount', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionEvents', 12, N'LastHealthyNonZeroCount', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionEvents', 13, N'RecoveryStreak', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionEvents', 14, N'RequiredRecoveryStreak', N'int', 4, 10, 0, 0, NULL),
            (N'TaskTypeProtectionEvents', 15, N'EnterThreshold', N'int', 4, 10, 0, 0, NULL),

            (N'ProjectionCommitTaskTypeProtectionDecisions', 1, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 2, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 3, N'PhaseBefore', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 4, N'PhaseAfter', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 5, N'ObservedCount', N'int', 4, 10, 0, 0, NULL),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 6, N'LastHealthyNonZeroCount', N'int', 4, 10, 0, 0, NULL),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 7, N'RecoveryStreakBefore', N'int', 4, 10, 0, 0, NULL),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 8, N'RecoveryStreakAfter', N'int', 4, 10, 0, 0, NULL),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 9, N'ProtectionAllowsAbsenceAuthority', N'bit', 1, 1, 0, 0, NULL),
            (N'ProjectionCommitTaskTypeProtectionDecisions', 10, N'EffectiveAbsenceAuthorityAvailable', N'bit', 1, 1, 0, 0, NULL),

            (N'DemandSeries', 1, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 2, N'KeyToken', N'char', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 3, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 4, N'Sublot', N'nvarchar', 512, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 5, N'Lifecycle', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 6, N'CurrentPresence', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 7, N'StartedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'DemandSeries', 8, N'ArchivedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'DemandSeries', 9, N'CreatedPollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 10, N'CreatedProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 11, N'LatestProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 12, N'CurrentDemandId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandSeries', 13, N'LastSeriesSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'DemandSeries', 14, N'RetentionEligibilityAt', N'datetimeoffset', 10, 34, 7, 1, NULL),

            (N'ArchivedDemandKeyTombstones', 1, N'KeyToken', N'char', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ArchivedDemandKeyTombstones', 2, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ArchivedDemandKeyTombstones', 3, N'Sublot', N'nvarchar', 512, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ArchivedDemandKeyTombstones', 4, N'OriginalSeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ArchivedDemandKeyTombstones', 5, N'ArchivedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'ArchivedDemandKeyTombstones', 6, N'ArchiveConclusion', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'ArchivedDemandKeyTombstones', 7, N'TombstoneVersion', N'int', 4, 10, 0, 0, NULL),

            (N'TransportDemands', 1, N'DemandId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 2, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 3, N'Generation', N'int', 4, 10, 0, 0, NULL),
            (N'TransportDemands', 4, N'PredecessorDemandId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 5, N'Status', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 6, N'CreatedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'TransportDemands', 7, N'DemandLastSeenAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'TransportDemands', 8, N'GoneConfirmedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'TransportDemands', 9, N'CreatedPollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 10, N'CreatedProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 11, N'LatestProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 12, N'LatestObservationProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'TransportDemands', 13, N'CurrentRawObservationCount', N'int', 4, 10, 0, 0, NULL),
            (N'TransportDemands', 14, N'DemandRevision', N'bigint', 8, 19, 0, 0, NULL),
            (N'TransportDemands', 15, N'ValueObservedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'TransportDemands', 16, N'Area', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'TransportDemands', 17, N'Eqp', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'TransportDemands', 18, N'Step', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'TransportDemands', 19, N'MesSourceDate', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'TransportDemands', 20, N'Package', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),

            (N'DemandRawObservations', 1, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 2, N'Ordinal', N'int', 4, 10, 0, 0, NULL),
            (N'DemandRawObservations', 3, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 4, N'SeriesId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 5, N'DemandId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 6, N'WorkType', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 7, N'Sublot', N'nvarchar', 512, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandRawObservations', 8, N'Area', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'DemandRawObservations', 9, N'Eqp', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'DemandRawObservations', 10, N'Step', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'DemandRawObservations', 11, N'MesSourceDate', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'DemandRawObservations', 12, N'Package', N'nvarchar', 1024, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'DemandRawObservations', 13, N'MesSourceDateRaw', N'nvarchar', 256, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),

            (N'DemandSeriesEvents', 1, N'EventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 2, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 3, N'SeriesSequence', N'bigint', 8, 19, 0, 0, NULL),
            (N'DemandSeriesEvents', 4, N'EventType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 5, N'OccurredAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'DemandSeriesEvents', 6, N'SubjectKind', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 7, N'SubjectId', N'nvarchar', 256, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 8, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 9, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesEvents', 10, N'PayloadVersion', N'int', 4, 10, 0, 0, NULL),
            (N'DemandSeriesEvents', 11, N'Payload', N'nvarchar', -1, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),

            (N'DemandSeriesErrorPeriods', 1, N'PeriodId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 2, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 3, N'ErrorCode', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 4, N'Category', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 5, N'Severity', N'nvarchar', 64, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 6, N'Target', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 7, N'SubjectKind', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 8, N'StartReason', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 9, N'StartedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'DemandSeriesErrorPeriods', 10, N'EndedAt', N'datetimeoffset', 10, 34, 7, 1, NULL),
            (N'DemandSeriesErrorPeriods', 11, N'EndReason', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 12, N'OpenedEventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesErrorPeriods', 13, N'ClosedEventId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),

            (N'SeriesErrorPeriodEvidence', 1, N'EvidenceId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 2, N'PeriodId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 3, N'EventId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 4, N'EvidenceKind', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 5, N'ObservedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'SeriesErrorPeriodEvidence', 6, N'PollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 7, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 8, N'DemandId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'SeriesErrorPeriodEvidence', 9, N'ObservedValue', N'nvarchar', -1, 0, 0, 1, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'SeriesErrorPeriodEvidence', 10, N'ExpectedRule', N'nvarchar', 512, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'DemandSeriesCurrentConditions', 1, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesCurrentConditions', 2, N'ErrorCode', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesCurrentConditions', 3, N'Target', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesCurrentConditions', 4, N'SubjectKind', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesCurrentConditions', 5, N'PeriodId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'DemandSeriesCurrentConditions', 6, N'LatestEvidenceId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),

            (N'CatalogState', 1, N'Id', N'int', 4, 10, 0, 0, NULL),
            (N'CatalogState', 2, N'CatalogRevision', N'bigint', 8, 19, 0, 0, NULL),
            (N'CatalogState', 3, N'ProjectionCommitId', N'nvarchar', 128, 0, 0, 1, N'Latin1_General_100_BIN2'),

            (N'CatalogItems', 1, N'DemandId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 2, N'SeriesId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 3, N'WorkType', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 4, N'Sublot', N'nvarchar', 512, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 5, N'Generation', N'int', 4, 10, 0, 0, NULL),
            (N'CatalogItems', 6, N'DemandRevision', N'bigint', 8, 19, 0, 0, NULL),
            (N'CatalogItems', 7, N'CreatedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'CatalogItems', 8, N'ValueObservedAt', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'CatalogItems', 9, N'ValuePollTraceId', N'nvarchar', 256, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 10, N'ValueProjectionCommitId', N'nvarchar', 128, 0, 0, 0, N'Latin1_General_100_BIN2'),
            (N'CatalogItems', 11, N'Area', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'CatalogItems', 12, N'Eqp', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'CatalogItems', 13, N'Step', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))),
            (N'CatalogItems', 14, N'MesSourceDate', N'datetimeoffset', 10, 34, 7, 0, NULL),
            (N'CatalogItems', 15, N'Package', N'nvarchar', 1024, 0, 0, 0, CONVERT(SYSNAME, DATABASEPROPERTYEX(DB_NAME(), 'Collation')));

        IF EXISTS
        (
            SELECT e.* FROM @ExpectedColumns AS e
            EXCEPT
            SELECT
                t.name, c.column_id, c.name, ty.name, c.max_length,
                c.[precision], c.scale, c.is_nullable, c.collation_name
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            INNER JOIN sys.types AS ty
                ON ty.user_type_id = c.user_type_id AND ty.is_user_defined = 0
            WHERE s.name = N'mesingest'
        )
        OR EXISTS
        (
            SELECT
                t.name, c.column_id, c.name, ty.name, c.max_length,
                c.[precision], c.scale, c.is_nullable, c.collation_name
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            INNER JOIN sys.types AS ty
                ON ty.user_type_id = c.user_type_id AND ty.is_user_defined = 0
            WHERE s.name = N'mesingest'
            EXCEPT
            SELECT e.* FROM @ExpectedColumns AS e
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            WHERE s.name = N'mesingest'
              AND (c.is_computed = 1 OR c.default_object_id <> 0)
        )
        OR
        (
            SELECT COUNT(*)
            FROM sys.identity_columns AS ic
            INNER JOIN sys.tables AS t ON t.object_id = ic.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
        ) <> 2
        OR NOT EXISTS
        (
            SELECT 1
            FROM sys.identity_columns AS ic
            INNER JOIN sys.tables AS t ON t.object_id = ic.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'PollTraces'
              AND ic.name = N'PollTraceSequence'
              AND CONVERT(BIGINT, ic.seed_value) = 1
              AND CONVERT(BIGINT, ic.increment_value) = 1
              AND ic.is_not_for_replication = 0
        )
        OR NOT EXISTS
        (
            SELECT 1
            FROM sys.identity_columns AS ic
            INNER JOIN sys.tables AS t ON t.object_id = ic.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'ProjectionCommits'
              AND ic.name = N'ProjectionSequence'
              AND CONVERT(BIGINT, ic.seed_value) = 1
              AND CONVERT(BIGINT, ic.increment_value) = 1
              AND ic.is_not_for_replication = 0
        )
            THROW 51002, 'The configured database contains an incompatible new-MesIngest column contract.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND t.temporal_type <> 0
        )
            THROW 51002, 'The configured database contains an incompatible temporal new-MesIngest table.', 1;

        DECLARE @ExpectedKeys TABLE
        (
            ConstraintName SYSNAME NOT NULL,
            TableName SYSNAME NOT NULL,
            IsPrimaryKey BIT NOT NULL,
            IsUnique BIT NOT NULL,
            KeyOrdinal INT NOT NULL,
            ColumnName SYSNAME NOT NULL,
            IsDescending BIT NOT NULL
        );
        INSERT INTO @ExpectedKeys VALUES
            (N'PK_MesIngest_SchemaInfo', N'SchemaInfo', 1, 1, 1, N'Id', 0),
            (N'UQ_MesIngest_SchemaInfo_HistoryEpoch', N'SchemaInfo', 0, 1, 1, N'HistoryEpoch', 0),
            (N'PK_MesIngest_HistoryCleanupState', N'HistoryCleanupState', 1, 1, 1, N'Id', 0),
            (N'PK_MesIngest_StoragePressureState', N'StoragePressureState', 1, 1, 1, N'Id', 0),
            (N'PK_MesIngest_StoragePressureRecoveryAudits', N'StoragePressureRecoveryAudits', 1, 1, 1, N'RecoveryAuditId', 0),
            (N'UQ_MesIngest_StoragePressureRecoveryAudits_PauseId', N'StoragePressureRecoveryAudits', 0, 1, 1, N'PauseId', 0),
            (N'PK_MesIngest_PollTraces', N'PollTraces', 1, 1, 1, N'PollTraceId', 0),
            (N'UQ_MesIngest_PollTraces_Sequence', N'PollTraces', 0, 1, 1, N'PollTraceSequence', 0),
            (N'PK_MesIngest_ProjectionCommits', N'ProjectionCommits', 1, 1, 1, N'ProjectionCommitId', 0),
            (N'UQ_MesIngest_ProjectionCommits_Sequence', N'ProjectionCommits', 0, 1, 1, N'ProjectionSequence', 0),
            (N'UQ_MesIngest_ProjectionCommits_PollTrace', N'ProjectionCommits', 0, 1, 1, N'PollTraceId', 0),
            (N'PK_MesIngest_ProjectionCommitUnassignedObservationFacts', N'ProjectionCommitUnassignedObservationFacts', 1, 1, 1, N'ProjectionCommitId', 0),
            (N'PK_MesIngest_UnassignedMesObservationEvents', N'UnassignedMesObservationEvents', 1, 1, 1, N'EventId', 0),
            (N'UQ_MesIngest_UnassignedMesObservationEvents_Commit', N'UnassignedMesObservationEvents', 0, 1, 1, N'ProjectionCommitId', 0),
            (N'PK_MesIngest_CurrentOverviewAreaFacts', N'CurrentOverviewAreaFacts', 1, 1, 1, N'AreaKey', 0),
            (N'PK_MesIngest_CurrentOverviewErrorSeriesFacts', N'CurrentOverviewErrorSeriesFacts', 1, 1, 1, N'SeriesId', 0),
            (N'PK_MesIngest_CurrentOverviewActivities', N'CurrentOverviewActivities', 1, 1, 1, N'ActivityRank', 0),
            (N'PK_MesIngest_HostSessions', N'HostSessions', 1, 1, 1, N'HostSessionId', 0),
            (N'PK_MesIngest_AbsenceAuthorityEvents', N'AbsenceAuthorityEvents', 1, 1, 1, N'EventId', 0),
            (N'PK_MesIngest_TaskTypeProtectionStates', N'TaskTypeProtectionStates', 1, 1, 1, N'WorkType', 0),
            (N'PK_MesIngest_TaskTypeProtectionEvents', N'TaskTypeProtectionEvents', 1, 1, 1, N'EventId', 0),
            (N'UQ_MesIngest_TaskTypeProtectionEvents_WorkTypeSequence', N'TaskTypeProtectionEvents', 0, 1, 1, N'WorkType', 0),
            (N'UQ_MesIngest_TaskTypeProtectionEvents_WorkTypeSequence', N'TaskTypeProtectionEvents', 0, 1, 2, N'WorkTypeSequence', 0),
            (N'PK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions', N'ProjectionCommitTaskTypeProtectionDecisions', 1, 1, 1, N'ProjectionCommitId', 0),
            (N'PK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions', N'ProjectionCommitTaskTypeProtectionDecisions', 1, 1, 2, N'WorkType', 0),
            (N'PK_MesIngest_DemandSeries', N'DemandSeries', 1, 1, 1, N'SeriesId', 0),
            (N'UQ_MesIngest_DemandSeries_KeyToken', N'DemandSeries', 0, 1, 1, N'KeyToken', 0),
            (N'PK_MesIngest_ArchivedDemandKeyTombstones', N'ArchivedDemandKeyTombstones', 1, 1, 1, N'KeyToken', 0),
            (N'UQ_MesIngest_ArchivedDemandKeyTombstones_OriginalSeries', N'ArchivedDemandKeyTombstones', 0, 1, 1, N'OriginalSeriesId', 0),
            (N'PK_MesIngest_TransportDemands', N'TransportDemands', 1, 1, 1, N'DemandId', 0),
            (N'UQ_MesIngest_TransportDemands_SeriesGeneration', N'TransportDemands', 0, 1, 1, N'SeriesId', 0),
            (N'UQ_MesIngest_TransportDemands_SeriesGeneration', N'TransportDemands', 0, 1, 2, N'Generation', 0),
            (N'PK_MesIngest_DemandRawObservations', N'DemandRawObservations', 1, 1, 1, N'PollTraceId', 0),
            (N'PK_MesIngest_DemandRawObservations', N'DemandRawObservations', 1, 1, 2, N'Ordinal', 0),
            (N'PK_MesIngest_DemandSeriesEvents', N'DemandSeriesEvents', 1, 1, 1, N'EventId', 0),
            (N'UQ_MesIngest_DemandSeriesEvents_SeriesSequence', N'DemandSeriesEvents', 0, 1, 1, N'SeriesId', 0),
            (N'UQ_MesIngest_DemandSeriesEvents_SeriesSequence', N'DemandSeriesEvents', 0, 1, 2, N'SeriesSequence', 0),
            (N'PK_MesIngest_DemandSeriesErrorPeriods', N'DemandSeriesErrorPeriods', 1, 1, 1, N'PeriodId', 0),
            (N'PK_MesIngest_SeriesErrorPeriodEvidence', N'SeriesErrorPeriodEvidence', 1, 1, 1, N'EvidenceId', 0),
            (N'PK_MesIngest_DemandSeriesCurrentConditions', N'DemandSeriesCurrentConditions', 1, 1, 1, N'SeriesId', 0),
            (N'PK_MesIngest_DemandSeriesCurrentConditions', N'DemandSeriesCurrentConditions', 1, 1, 2, N'ErrorCode', 0),
            (N'PK_MesIngest_DemandSeriesCurrentConditions', N'DemandSeriesCurrentConditions', 1, 1, 3, N'Target', 0),
            (N'PK_MesIngest_DemandSeriesCurrentConditions', N'DemandSeriesCurrentConditions', 1, 1, 4, N'SubjectKind', 0),
            (N'UQ_MesIngest_DemandSeriesCurrentConditions_Period', N'DemandSeriesCurrentConditions', 0, 1, 1, N'PeriodId', 0),
            (N'PK_MesIngest_CatalogState', N'CatalogState', 1, 1, 1, N'Id', 0),
            (N'PK_MesIngest_CatalogItems', N'CatalogItems', 1, 1, 1, N'DemandId', 0);

        IF EXISTS
        (
            SELECT e.* FROM @ExpectedKeys AS e
            EXCEPT
            SELECT kc.name, t.name,
                CONVERT(BIT, CASE WHEN kc.[type] = N'PK' THEN 1 ELSE 0 END),
                i.is_unique,
                ic.key_ordinal, c.name, ic.is_descending_key
            FROM sys.key_constraints AS kc
            INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes AS i
                ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = N'mesingest'
        )
        OR EXISTS
        (
            SELECT kc.name, t.name,
                CONVERT(BIT, CASE WHEN kc.[type] = N'PK' THEN 1 ELSE 0 END),
                i.is_unique,
                ic.key_ordinal, c.name, ic.is_descending_key
            FROM sys.key_constraints AS kc
            INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes AS i
                ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = N'mesingest'
            EXCEPT SELECT e.* FROM @ExpectedKeys AS e
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.key_constraints AS kc
            INNER JOIN sys.indexes AS i
                ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND (i.is_disabled = 1 OR i.is_hypothetical = 1 OR i.has_filter = 1 OR i.[type] NOT IN (1, 2))
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND i.is_unique = 1
              AND NOT EXISTS
              (
                  SELECT 1 FROM sys.key_constraints AS kc
                  WHERE kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
              )
        )
            THROW 51003, 'The configured database contains an incompatible new-MesIngest key contract.', 1;

        DECLARE @ExpectedForeignKeys TABLE
        (
            ConstraintName SYSNAME NOT NULL,
            ParentTable SYSNAME NOT NULL,
            ParentColumn SYSNAME NOT NULL,
            ReferencedTable SYSNAME NOT NULL,
            ReferencedColumn SYSNAME NOT NULL
        );
        INSERT INTO @ExpectedForeignKeys VALUES
            (N'FK_MesIngest_StoragePressureState_HistoryEpoch', N'StoragePressureState', N'HistoryEpoch', N'SchemaInfo', N'HistoryEpoch'),
            (N'FK_MesIngest_StoragePressureRecoveryAudits_HistoryEpoch', N'StoragePressureRecoveryAudits', N'HistoryEpoch', N'SchemaInfo', N'HistoryEpoch'),
            (N'FK_MesIngest_ProjectionCommits_PollTrace', N'ProjectionCommits', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_ProjectionCommits_HistoryEpoch', N'ProjectionCommits', N'HistoryEpoch', N'SchemaInfo', N'HistoryEpoch'),
            (N'FK_MesIngest_ProjectionCommits_HostSession', N'ProjectionCommits', N'HostSessionId', N'HostSessions', N'HostSessionId'),
            (N'FK_MesIngest_ProjectionCommitUnassignedObservationFacts_Commit', N'ProjectionCommitUnassignedObservationFacts', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_UnassignedMesObservationEvents_Commit', N'UnassignedMesObservationEvents', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_CurrentOverviewErrorSeriesFacts_Series', N'CurrentOverviewErrorSeriesFacts', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_CurrentOverviewErrorSeriesFacts_Commit', N'CurrentOverviewErrorSeriesFacts', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_AbsenceAuthorityEvents_HostSession', N'AbsenceAuthorityEvents', N'HostSessionId', N'HostSessions', N'HostSessionId'),
            (N'FK_MesIngest_AbsenceAuthorityEvents_PollTrace', N'AbsenceAuthorityEvents', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_AbsenceAuthorityEvents_Commit', N'AbsenceAuthorityEvents', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_TaskTypeProtectionStates_PollTrace', N'TaskTypeProtectionStates', N'LatestPollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_TaskTypeProtectionStates_Commit', N'TaskTypeProtectionStates', N'LatestProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_TaskTypeProtectionEvents_State', N'TaskTypeProtectionEvents', N'WorkType', N'TaskTypeProtectionStates', N'WorkType'),
            (N'FK_MesIngest_TaskTypeProtectionEvents_PollTrace', N'TaskTypeProtectionEvents', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_TaskTypeProtectionEvents_Commit', N'TaskTypeProtectionEvents', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_Commit', N'ProjectionCommitTaskTypeProtectionDecisions', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_State', N'ProjectionCommitTaskTypeProtectionDecisions', N'WorkType', N'TaskTypeProtectionStates', N'WorkType'),
            (N'FK_MesIngest_DemandSeries_CreatedPollTrace', N'DemandSeries', N'CreatedPollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_DemandSeries_CreatedCommit', N'DemandSeries', N'CreatedProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_DemandSeries_LatestCommit', N'DemandSeries', N'LatestProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_DemandSeries_CurrentDemand', N'DemandSeries', N'CurrentDemandId', N'TransportDemands', N'DemandId'),
            (N'FK_MesIngest_TransportDemands_Series', N'TransportDemands', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_TransportDemands_Predecessor', N'TransportDemands', N'PredecessorDemandId', N'TransportDemands', N'DemandId'),
            (N'FK_MesIngest_TransportDemands_CreatedPollTrace', N'TransportDemands', N'CreatedPollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_TransportDemands_CreatedCommit', N'TransportDemands', N'CreatedProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_TransportDemands_LatestCommit', N'TransportDemands', N'LatestProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_TransportDemands_LatestObservationCommit', N'TransportDemands', N'LatestObservationProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_DemandRawObservations_PollTrace', N'DemandRawObservations', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_DemandRawObservations_Commit', N'DemandRawObservations', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_DemandRawObservations_Series', N'DemandRawObservations', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_DemandRawObservations_Demand', N'DemandRawObservations', N'DemandId', N'TransportDemands', N'DemandId'),
            (N'FK_MesIngest_DemandSeriesEvents_Series', N'DemandSeriesEvents', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_DemandSeriesEvents_PollTrace', N'DemandSeriesEvents', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_DemandSeriesEvents_Commit', N'DemandSeriesEvents', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_DemandSeriesErrorPeriods_Series', N'DemandSeriesErrorPeriods', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_DemandSeriesErrorPeriods_OpenedEvent', N'DemandSeriesErrorPeriods', N'OpenedEventId', N'DemandSeriesEvents', N'EventId'),
            (N'FK_MesIngest_DemandSeriesErrorPeriods_ClosedEvent', N'DemandSeriesErrorPeriods', N'ClosedEventId', N'DemandSeriesEvents', N'EventId'),
            (N'FK_MesIngest_SeriesErrorPeriodEvidence_Period', N'SeriesErrorPeriodEvidence', N'PeriodId', N'DemandSeriesErrorPeriods', N'PeriodId'),
            (N'FK_MesIngest_SeriesErrorPeriodEvidence_Event', N'SeriesErrorPeriodEvidence', N'EventId', N'DemandSeriesEvents', N'EventId'),
            (N'FK_MesIngest_SeriesErrorPeriodEvidence_PollTrace', N'SeriesErrorPeriodEvidence', N'PollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_SeriesErrorPeriodEvidence_Commit', N'SeriesErrorPeriodEvidence', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_SeriesErrorPeriodEvidence_Demand', N'SeriesErrorPeriodEvidence', N'DemandId', N'TransportDemands', N'DemandId'),
            (N'FK_MesIngest_DemandSeriesCurrentConditions_Series', N'DemandSeriesCurrentConditions', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_DemandSeriesCurrentConditions_Period', N'DemandSeriesCurrentConditions', N'PeriodId', N'DemandSeriesErrorPeriods', N'PeriodId'),
            (N'FK_MesIngest_DemandSeriesCurrentConditions_Evidence', N'DemandSeriesCurrentConditions', N'LatestEvidenceId', N'SeriesErrorPeriodEvidence', N'EvidenceId'),
            (N'FK_MesIngest_CatalogState_Commit', N'CatalogState', N'ProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId'),
            (N'FK_MesIngest_CatalogItems_Demand', N'CatalogItems', N'DemandId', N'TransportDemands', N'DemandId'),
            (N'FK_MesIngest_CatalogItems_Series', N'CatalogItems', N'SeriesId', N'DemandSeries', N'SeriesId'),
            (N'FK_MesIngest_CatalogItems_ValuePollTrace', N'CatalogItems', N'ValuePollTraceId', N'PollTraces', N'PollTraceId'),
            (N'FK_MesIngest_CatalogItems_ValueCommit', N'CatalogItems', N'ValueProjectionCommitId', N'ProjectionCommits', N'ProjectionCommitId');

        IF (SELECT COUNT(*) FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest') <> 52
        OR EXISTS
        (
            SELECT e.* FROM @ExpectedForeignKeys AS e
            EXCEPT
            SELECT fk.name, pt.name, pc.name, rt.name, rc.name
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS pt ON pt.object_id = fk.parent_object_id
            INNER JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
            INNER JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS pc ON pc.object_id = pt.object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
            WHERE ps.name = N'mesingest'
              AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
              AND fk.delete_referential_action = 0 AND fk.update_referential_action = 0
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND (fk.is_disabled = 1 OR fk.is_not_trusted = 1
                   OR fk.delete_referential_action <> 0 OR fk.update_referential_action <> 0)
        )
        OR EXISTS
        (
            SELECT fk.name, pt.name, pc.name, rt.name, rc.name
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS pt ON pt.object_id = fk.parent_object_id
            INNER JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
            INNER JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS pc ON pc.object_id = pt.object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
            WHERE ps.name = N'mesingest'
            EXCEPT SELECT e.* FROM @ExpectedForeignKeys AS e
        )
            THROW 51004, 'The configured database contains an incompatible new-MesIngest foreign-key contract.', 1;

        DECLARE @ExpectedChecks TABLE
        (
            ConstraintName SYSNAME NOT NULL,
            TableName SYSNAME NOT NULL,
            Definition NVARCHAR(4000) NOT NULL
        );
        INSERT INTO @ExpectedChecks VALUES
            (N'CK_MesIngest_SchemaInfo_SingleRow', N'SchemaInfo', N'([Id]=(1))'),
            (N'CK_MesIngest_SchemaInfo_SnapshotTokenSigningKeyLength', N'SchemaInfo', N'(datalength([SnapshotTokenSigningKey])=(32))'),
            (N'CK_MesIngest_HistoryCleanupState_SingleRow', N'HistoryCleanupState', N'([Id]=(1))'),
            (N'CK_MesIngest_StoragePressureState_SingleRow', N'StoragePressureState', N'([Id]=(1))'),
            (N'CK_MesIngest_PollTraces_Outcome', N'PollTraces', N'([Outcome]=N''INCOMPLETE'' OR [Outcome]=N''FAILURE'' OR [Outcome]=N''SUCCESS'')'),
            (N'CK_MesIngest_PollTraces_RowCount', N'PollTraces', N'([RowCount]>=(0) AND [RowCount]<=(25000))'),
            (N'CK_MesIngest_PollTraces_Diagnostic', N'PollTraces', N'([DiagnosticStage] IS NULL AND [DiagnosticCode] IS NULL AND [DiagnosticSafeDetail] IS NULL OR [Outcome]<>N''SUCCESS'' AND [DiagnosticStage] IS NOT NULL AND [DiagnosticCode] IS NOT NULL AND [DiagnosticSafeDetail] IS NOT NULL)'),
            (N'CK_MesIngest_ProjectionCommitUnassignedObservationFacts_State', N'ProjectionCommitUnassignedObservationFacts', N'([ObservationCount]=(0) AND [ContentDigest] IS NULL OR [ObservationCount]>(0) AND [ContentDigest] IS NOT NULL)'),
            (N'CK_MesIngest_UnassignedMesObservationEvents_EventType', N'UnassignedMesObservationEvents', N'([EventType]=N''UNASSIGNED_MES_OBSERVATION_CLEARED'' OR [EventType]=N''UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED'' OR [EventType]=N''UNASSIGNED_MES_OBSERVATION_APPEARED'')'),
            (N'CK_MesIngest_UnassignedMesObservationEvents_BeforeState', N'UnassignedMesObservationEvents', N'([BeforeObservationCount]=(0) AND [BeforeContentDigest] IS NULL OR [BeforeObservationCount]>(0) AND [BeforeContentDigest] IS NOT NULL)'),
            (N'CK_MesIngest_UnassignedMesObservationEvents_AfterState', N'UnassignedMesObservationEvents', N'([AfterObservationCount]=(0) AND [AfterContentDigest] IS NULL OR [AfterObservationCount]>(0) AND [AfterContentDigest] IS NOT NULL)'),
            (N'CK_MesIngest_UnassignedMesObservationEvents_Transition', N'UnassignedMesObservationEvents', N'([EventType]=N''UNASSIGNED_MES_OBSERVATION_APPEARED'' AND [BeforeObservationCount]=(0) AND [AfterObservationCount]>(0) OR [EventType]=N''UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED'' AND [BeforeObservationCount]>(0) AND [AfterObservationCount]>(0) AND [BeforeContentDigest]<>[AfterContentDigest] OR [EventType]=N''UNASSIGNED_MES_OBSERVATION_CLEARED'' AND [BeforeObservationCount]>(0) AND [AfterObservationCount]=(0))'),
            (N'CK_MesIngest_CurrentOverviewErrorSeriesFacts_EndedPair', N'CurrentOverviewErrorSeriesFacts', N'([LatestEndedPeriodStartedAt] IS NULL AND [LatestEndedAt] IS NULL OR [LatestEndedPeriodStartedAt] IS NOT NULL AND [LatestEndedAt] IS NOT NULL)'),
            (N'CK_MesIngest_HostSessions_RestartPhase', N'HostSessions', N'([RestartPhase]=N''NORMAL'' OR [RestartPhase]=N''POST_BARRIER'' OR [RestartPhase]=N''BARRIER'')'),
            (N'CK_MesIngest_TaskTypeProtectionStates_Phase', N'TaskTypeProtectionStates', N'([Phase]=N''AUTHORITY_PENDING'' OR [Phase]=N''RECOVERING'' OR [Phase]=N''PAUSED_ZERO_DROP'' OR [Phase]=N''MONITORING'')'),
            (N'CK_MesIngest_TaskTypeProtectionStates_LastHealthyCount', N'TaskTypeProtectionStates', N'([LastHealthyNonZeroCount]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionStates_LatestCount', N'TaskTypeProtectionStates', N'([LatestObservedCount]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionStates_RecoveryStreak', N'TaskTypeProtectionStates', N'([RecoveryStreak]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionStates_LastSequence', N'TaskTypeProtectionStates', N'([LastSequence]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionStates_EnterThreshold', N'TaskTypeProtectionStates', N'([EnterThreshold]>=(1))'),
            (N'CK_MesIngest_TaskTypeProtectionStates_EffectiveAuthority', N'TaskTypeProtectionStates', N'([EffectiveAbsenceAuthorityAvailable]<=[ProtectionAllowsAbsenceAuthority])'),
            (N'CK_MesIngest_TaskTypeProtectionStates_Episode', N'TaskTypeProtectionStates', N'([EpisodeId] IS NULL AND [EnteredAt] IS NULL OR [EpisodeId] IS NOT NULL AND [EnteredAt] IS NOT NULL)'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_Sequence', N'TaskTypeProtectionEvents', N'([WorkTypeSequence]>=(1))'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_ObservedCount', N'TaskTypeProtectionEvents', N'([ObservedCount]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_LastHealthyCount', N'TaskTypeProtectionEvents', N'([LastHealthyNonZeroCount]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_RecoveryStreak', N'TaskTypeProtectionEvents', N'([RecoveryStreak]>=(0))'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_RequiredRecoveryStreak', N'TaskTypeProtectionEvents', N'([RequiredRecoveryStreak]>=(1))'),
            (N'CK_MesIngest_TaskTypeProtectionEvents_EnterThreshold', N'TaskTypeProtectionEvents', N'([EnterThreshold]>=(1))'),
            (N'CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_ObservedCount', N'ProjectionCommitTaskTypeProtectionDecisions', N'([ObservedCount]>=(0))'),
            (N'CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_LastHealthyCount', N'ProjectionCommitTaskTypeProtectionDecisions', N'([LastHealthyNonZeroCount]>=(0))'),
            (N'CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_StreakBefore', N'ProjectionCommitTaskTypeProtectionDecisions', N'([RecoveryStreakBefore]>=(0))'),
            (N'CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_StreakAfter', N'ProjectionCommitTaskTypeProtectionDecisions', N'([RecoveryStreakAfter]>=(0))'),
            (N'CK_MesIngest_ProjectionCommitTaskTypeProtectionDecisions_EffectiveAuthority', N'ProjectionCommitTaskTypeProtectionDecisions', N'([EffectiveAbsenceAuthorityAvailable]<=[ProtectionAllowsAbsenceAuthority])'),
            (N'CK_MesIngest_DemandSeries_LastSequence', N'DemandSeries', N'([LastSeriesSequence]>=(0))'),
            (N'CK_MesIngest_ArchivedDemandKeyTombstones_ArchiveConclusion', N'ArchivedDemandKeyTombstones', N'([ArchiveConclusion]=N''ARCHIVED'')'),
            (N'CK_MesIngest_ArchivedDemandKeyTombstones_Version', N'ArchivedDemandKeyTombstones', N'([TombstoneVersion]=(1))'),
            (N'CK_MesIngest_TransportDemands_Generation', N'TransportDemands', N'([Generation]>=(1))'),
            (N'CK_MesIngest_TransportDemands_CurrentRawObservationCount', N'TransportDemands', N'([CurrentRawObservationCount]>=(1))'),
            (N'CK_MesIngest_TransportDemands_DemandRevision', N'TransportDemands', N'([DemandRevision]>=(1))'),
            (N'CK_MesIngest_DemandRawObservations_Ordinal', N'DemandRawObservations', N'([Ordinal]>=(0))'),
            (N'CK_MesIngest_DemandSeriesEvents_Sequence', N'DemandSeriesEvents', N'([SeriesSequence]>=(1))'),
            (N'CK_MesIngest_DemandSeriesEvents_PayloadVersion', N'DemandSeriesEvents', N'([PayloadVersion]>=(1))'),
            (N'CK_MesIngest_DemandSeriesEvents_PayloadJson', N'DemandSeriesEvents', N'(isjson([Payload])=(1))'),
            (N'CK_MesIngest_DemandSeriesErrorPeriods_EndPair', N'DemandSeriesErrorPeriods', N'([EndedAt] IS NULL AND [EndReason] IS NULL AND [ClosedEventId] IS NULL OR [EndedAt] IS NOT NULL AND [EndReason] IS NOT NULL AND [ClosedEventId] IS NOT NULL)'),
            (N'CK_MesIngest_CatalogState_SingleRow', N'CatalogState', N'([Id]=(1))'),
            (N'CK_MesIngest_CatalogState_Revision', N'CatalogState', N'([CatalogRevision]>=(0))'),
            (N'CK_MesIngest_CatalogItems_Generation', N'CatalogItems', N'([Generation]>=(1))'),
            (N'CK_MesIngest_CatalogItems_DemandRevision', N'CatalogItems', N'([DemandRevision]>=(1))');

        IF (SELECT COUNT(*) FROM sys.check_constraints AS cc
            INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest') <> 48
        OR EXISTS
        (
            SELECT e.* FROM @ExpectedChecks AS e
            EXCEPT
            SELECT cc.name, t.name, cc.definition
            FROM sys.check_constraints AS cc
            INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND cc.is_disabled = 0 AND cc.is_not_trusted = 0
        )
        OR EXISTS
        (
            SELECT 1
            FROM sys.check_constraints AS cc
            INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND (cc.is_disabled = 1 OR cc.is_not_trusted = 1)
        )
        OR EXISTS
        (
            SELECT cc.name, t.name, cc.definition
            FROM sys.check_constraints AS cc
            INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
            EXCEPT SELECT e.* FROM @ExpectedChecks AS e
        )
            THROW 51005, 'The configured database contains an incompatible new-MesIngest check-constraint contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'PollTraces'
              AND i.name = N'IX_MesIngest_PollTraces_CompletedAt'
              AND i.[type] = 2
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND N'CompletedAt,PollTraceId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal > 0)
              AND N'RowCount' =
                  (SELECT STRING_AGG(c.name, N',')
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.is_included_column = 1)
        )
            THROW 51006, 'The configured database is missing the PollTrace historical-boundary index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'DemandRawObservations'
              AND i.name = N'IX_MesIngest_DemandRawObservations_Series'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND 3 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1)
              AND N'SeriesId,PollTraceId,Ordinal' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT SUM(CONVERT(INT, ic.is_descending_key))
                       FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
        )
            THROW 51006, 'The configured database is missing the new-MesIngest raw-observation index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'mesingest.DemandRawObservations')
              AND i.name = N'PK_MesIngest_DemandRawObservations'
              AND i.[type] = 1
              AND i.is_primary_key = 1
        )
            THROW 51006, 'The new-MesIngest raw-observation primary key must remain clustered.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'DemandRawObservations'
              AND i.name = N'IX_MesIngest_DemandRawObservations_Demand'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND 2 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND N'DemandId,ProjectionCommitId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT SUM(CONVERT(INT, ic.is_descending_key))
                       FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND N'Area' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.index_column_id)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.is_included_column = 1)
        )
            THROW 51006, 'The configured database is missing the new-MesIngest demand raw-observation index contract.', 1;

        IF EXISTS
        (
            SELECT expected.IndexName
            FROM (VALUES
                (N'PK_MesIngest_DemandRawObservations'),
                (N'IX_MesIngest_DemandRawObservations_Series'),
                (N'IX_MesIngest_DemandRawObservations_Demand')
            ) AS expected(IndexName)
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes AS i
                INNER JOIN sys.tables AS t ON t.object_id = i.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE s.name = N'mesingest'
                  AND t.name = N'DemandRawObservations'
                  AND i.name = expected.IndexName
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM sys.partitions AS p
                      WHERE p.object_id = i.object_id
                        AND p.index_id = i.index_id
                        AND p.data_compression_desc <> N'PAGE'
                  )
            )
        )
            THROW 51006, 'The configured database is missing PAGE compression on a required raw-observation index.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'SeriesErrorPeriodEvidence'
              AND i.name = N'IX_MesIngest_SeriesErrorPeriodEvidence_Period'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND 3 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1)
              AND N'PeriodId,ObservedAt,EvidenceId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT SUM(CONVERT(INT, ic.is_descending_key))
                       FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
        )
            THROW 51006, 'The configured database is missing the new-MesIngest error-evidence index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'DemandSeriesErrorPeriods'
              AND i.name = N'IX_MesIngest_DemandSeriesErrorPeriods_Search'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND N'Category,ErrorCode,StartedAt,SeriesId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal > 0)
              AND N'PeriodId,EndedAt,OpenedEventId,ClosedEventId,Severity' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.index_column_id)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.is_included_column = 1)
        )
            THROW 51006, 'The configured database is missing the new-MesIngest Error Search period index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'SeriesErrorPeriodEvidence'
              AND i.name = N'IX_MesIngest_SeriesErrorPeriodEvidence_Demand'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND N'DemandId,PeriodId,ObservedAt,EvidenceId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal > 0)
              AND N'ProjectionCommitId' =
                  (SELECT STRING_AGG(c.name, N',')
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.is_included_column = 1)
        )
            THROW 51006, 'The configured database is missing the new-MesIngest Error Search Demand evidence index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'TaskTypeProtectionEvents'
              AND i.name = N'IX_MesIngest_TaskTypeProtectionEvents_Commit'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND 3 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT COUNT(*) FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1)
              AND N'ProjectionCommitId,WorkType,WorkTypeSequence' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
              AND 0 = (SELECT SUM(CONVERT(INT, ic.is_descending_key))
                       FROM sys.index_columns AS ic
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
        )
            THROW 51006, 'The configured database is missing the task-type protection event commit index contract.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
              AND t.name = N'UnassignedMesObservationEvents'
              AND i.name = N'IX_MesIngest_UnassignedMesObservationEvents_Overview'
              AND i.[type] IN (1, 2)
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
              AND N'OccurredAt,EventId' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal > 0)
              AND N'1,0' =
                  (SELECT STRING_AGG(CONVERT(NVARCHAR(1), CONVERT(INT, ic.is_descending_key)), N',')
                          WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.key_ordinal > 0)
              AND N'EventType,ProjectionCommitId,BeforeObservationCount,AfterObservationCount,BeforeContentDigest,AfterContentDigest' =
                  (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.index_column_id)
                   FROM sys.index_columns AS ic
                   INNER JOIN sys.columns AS c
                       ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     AND ic.is_included_column = 1)
        )
            THROW 51006, 'The configured database is missing the unassigned-observation overview event index contract.', 1;

        DECLARE @ExpectedIndexes TABLE
        (
            TableName SYSNAME NOT NULL,
            IndexName SYSNAME NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        );
        INSERT INTO @ExpectedIndexes (TableName, IndexName)
        SELECT DISTINCT TableName, ConstraintName
        FROM @ExpectedKeys;
        INSERT INTO @ExpectedIndexes (TableName, IndexName) VALUES
            (N'PollTraces', N'IX_MesIngest_PollTraces_CompletedAt'),
            (N'PollTraces', N'IX_MesIngest_PollTraces_RawRetentionDue'),
            (N'UnassignedMesObservationEvents', N'IX_MesIngest_UnassignedMesObservationEvents_Overview'),
            (N'TaskTypeProtectionEvents', N'IX_MesIngest_TaskTypeProtectionEvents_Commit'),
            (N'DemandSeries', N'IX_MesIngest_DemandSeries_RetentionEligibilityAt'),
            (N'DemandRawObservations', N'IX_MesIngest_DemandRawObservations_Series'),
            (N'DemandRawObservations', N'IX_MesIngest_DemandRawObservations_Demand'),
            (N'SeriesErrorPeriodEvidence', N'IX_MesIngest_SeriesErrorPeriodEvidence_Period'),
            (N'DemandSeriesErrorPeriods', N'IX_MesIngest_DemandSeriesErrorPeriods_Search'),
            (N'SeriesErrorPeriodEvidence', N'IX_MesIngest_SeriesErrorPeriodEvidence_Demand');

        IF EXISTS
        (
            SELECT TableName, IndexName FROM @ExpectedIndexes
            EXCEPT
            SELECT t.name, i.name
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND i.index_id > 0
        )
        OR EXISTS
        (
            SELECT t.name, i.name
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest' AND i.index_id > 0
            EXCEPT
            SELECT TableName, IndexName FROM @ExpectedIndexes
        )
            THROW 51006, 'The configured database contains an unexpected new-MesIngest index.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.triggers AS tr
            INNER JOIN sys.tables AS t ON t.object_id = tr.parent_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'mesingest'
        )
            THROW 51007, 'The configured database contains unexpected new-MesIngest triggers.', 1;

        IF (SELECT COUNT(*) FROM mesingest.SchemaInfo) <> 1
        OR (SELECT COUNT(*) FROM mesingest.HistoryCleanupState) <> 1
        OR (SELECT COUNT(*) FROM mesingest.StoragePressureState) <> 1
        OR NOT EXISTS
        (
            SELECT 1 FROM mesingest.SchemaInfo
            WHERE Id = 1
              AND SchemaVersion = @schemaVersion
              AND ContractVersion = @contractVersion COLLATE Latin1_General_100_BIN2
              AND TransportDemandKeyComparison = @keyComparison COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(SnapshotTokenSigningKey) = 32
              AND HistoryEpoch <> '00000000-0000-0000-0000-000000000000'
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM mesingest.HistoryCleanupState
            WHERE Id = 1
              AND HistoryCleanupStatus IN
                  (N'NOT_RUN', N'RUNNING', N'SUCCEEDED', N'BUDGET_EXHAUSTED',
                   N'YIELDED_TO_POLL', N'INTERRUPTED', N'FAILED')
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM mesingest.StoragePressureState
            WHERE Id = 1
              AND StoragePressureStatus IN
                  (N'HEALTHY', N'CRITICAL_WARNING', N'STORAGE_PRESSURE_PAUSE')
              AND HistoryEpoch = (SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1)
        )
            THROW 51008, 'The configured database has a mismatched new-MesIngest schema contract identity.', 1;
        """;
}
