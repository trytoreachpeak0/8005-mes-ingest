using MesIngest.Core;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

[CollectionDefinition("SqlServer", DisableParallelization = true)]
public sealed class SqlServerCollectionDefinition;

[Collection("SqlServer")]
public class SqlServerTransportDemandStoreTests
{
    [SqlServerAvailabilityFact]
    public void Persisted_demands_and_pauses_are_readable_after_new_store_instance()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var state = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d-visible",
                    TaskType = "DIE_TO_WIRE_STAGING",
                    Sublot = "Q-VIS",
                    Area = "N09-01",
                    Eqp = "EQ1",
                    Step = "焊线",
                    Dates = now,
                    Package = "PKG-V",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = now,
                    DisappearCount = 0,
                    LocationRisk = false,
                    CreatedAt = now.AddHours(-2),
                },
                new TransportDemand
                {
                    DemandId = "d-gone",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-GONE",
                    Area = null,
                    Eqp = "EQ2",
                    Step = "烘箱",
                    Dates = now.AddHours(-1),
                    Package = "PKG-G",
                    Status = DemandStatus.Gone,
                    MesLastSeenAt = now.AddMinutes(-20),
                    DisappearCount = 2,
                    LocationRisk = true,
                    LocationRiskCode = "AREA_EMPTY",
                    CreatedAt = now.AddHours(-3),
                    GoneAt = now.AddMinutes(-15),
                },
            ],
            [
                new TaskTypePauseState(
                    "DIE_TO_OVEN",
                    PausedZeroDrop: true,
                    LastHealthyNonZeroCount: 12,
                    RecoveryStreak: 0),
            ]);

        var writer = new SqlServerTransportDemandStore(cs);
        writer.ReplaceState(state);

        var reader = new SqlServerTransportDemandStore(cs);
        var reloaded = reader.GetState();

        // Hot path: VISIBLE only. Permanent GONE stays queryable via List/GetById.
        var visible = Assert.Single(reloaded.Demands);
        Assert.Equal("d-visible", visible.DemandId);
        Assert.Equal(DemandStatus.Visible, visible.Status);
        Assert.Equal("PKG-V", visible.Package);
        Assert.Equal(0, visible.DisappearCount);
        Assert.False(visible.LocationRisk);
        Assert.Equal(now.AddHours(-2), visible.CreatedAt);
        Assert.Null(visible.GoneAt);

        var gone = Assert.Single(reader.List(DemandStatus.Gone));
        Assert.Equal("d-gone", gone.DemandId);
        Assert.Equal(DemandStatus.Gone, gone.Status);
        Assert.Equal(2, gone.DisappearCount);
        Assert.True(gone.LocationRisk);
        Assert.Equal("AREA_EMPTY", gone.LocationRiskCode);
        Assert.Null(gone.Area);
        Assert.Equal(now.AddHours(-3), gone.CreatedAt);
        Assert.Equal(now.AddMinutes(-15), gone.GoneAt);
        Assert.True(reader.HasGoneTransportDemandKey(new TransportDemandKey("DIE_TO_OVEN", "Q-GONE")));
        Assert.Equal(
            Assert.Single(reader.List(DemandStatus.Gone, taskType: "DIE_TO_OVEN", sublot: "Q-GONE")).DemandId,
            reader.GetLatestGoneDemandId(new TransportDemandKey("DIE_TO_OVEN", "Q-GONE")));
        Assert.False(reader.HasGoneTransportDemandKey(new TransportDemandKey("DIE_TO_WIRE_STAGING", "Q-VIS")));
        Assert.Null(reader.GetLatestGoneDemandId(new TransportDemandKey("DIE_TO_WIRE_STAGING", "Q-VIS")));

        var pause = Assert.Single(reloaded.TaskTypePauses);
        Assert.Equal("DIE_TO_OVEN", pause.TaskType);
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(12, pause.LastHealthyNonZeroCount);
        Assert.Equal(0, pause.RecoveryStreak);

        Assert.Equal("d-visible", reader.GetById("d-visible")?.DemandId);
        Assert.Single(reader.List(DemandStatus.Visible));
        Assert.Single(reader.List(DemandStatus.Gone));
    }

    [SqlServerAvailabilityFact]
    public void Gone_history_lookup_uses_ordinal_transport_demand_key_equality()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "gone-key-equality",
                TaskType = "WIRE_TO_GATE",
                Sublot = "Q-1",
                Dates = now,
                Status = DemandStatus.Gone,
                MesLastSeenAt = now,
                CreatedAt = now,
                GoneAt = now,
            },
        ]));

        Assert.Equal(
            "gone-key-equality",
            store.GetLatestGoneDemandId(new TransportDemandKey("WIRE_TO_GATE", "Q-1")));
        Assert.Null(store.GetLatestGoneDemandId(new TransportDemandKey("wire_to_gate", "Q-1")));
        Assert.Null(store.GetLatestGoneDemandId(new TransportDemandKey("WIRE_TO_GATE", "q-1")));
        Assert.Null(store.GetLatestGoneDemandId(new TransportDemandKey("WIRE_TO_GATE", "Q-1 ")));
    }

    [SqlServerAvailabilityFact]
    public void ReplaceState_appends_change_feed_in_same_transaction_for_created_and_gone()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs, clock: () => now);
        var visible = new TransportDemand
        {
            DemandId = "feed-1",
            TaskType = "DIE_TO_OVEN",
            Sublot = "Q-FEED",
            Dates = now,
            Status = DemandStatus.Visible,
            MesLastSeenAt = now,
            CreatedAt = now,
        };
        store.ReplaceState(new ProjectionState([visible]));

        var created = Assert.Single(store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now }).Items);
        Assert.Equal(DemandChangeType.Created, created.ChangeType);
        Assert.Equal("feed-1", created.DemandId);

        store.ReplaceState(new ProjectionState(
        [
            visible with { MesLastSeenAt = now.AddMinutes(1), DisappearCount = 1 },
        ]));
        Assert.Equal(1, store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now }).Items.Count);

        store.ReplaceState(new ProjectionState(
        [
            visible with
            {
                Status = DemandStatus.Gone,
                GoneAt = now.AddMinutes(2),
                DisappearCount = 2,
                MesLastSeenAt = now.AddMinutes(2),
            },
        ]));

        var page = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now });
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(DemandChangeType.Gone, page.Items[1].ChangeType);
        Assert.Equal(DemandStatus.Gone, page.Items[1].Payload.Status);
        Assert.Equal(now.AddMinutes(2), page.Items[1].Payload.GoneAt);

        // Reappear uses a new DemandId → CREATED again.
        store.ReplaceState(new ProjectionState(
        [
            visible with
            {
                Status = DemandStatus.Gone,
                GoneAt = now.AddMinutes(2),
                DisappearCount = 2,
            },
            visible with
            {
                DemandId = "feed-1-reappear",
                Status = DemandStatus.Visible,
                MesLastSeenAt = now.AddMinutes(3),
                CreatedAt = now.AddMinutes(3),
                GoneAt = null,
                DisappearCount = 0,
            },
        ]));
        var afterReappear = store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 2, AsOf = now });
        Assert.Contains(
            afterReappear.Items,
            i => i.DemandId == "feed-1-reappear" && i.ChangeType == DemandChangeType.Created);
    }

    [SqlServerAvailabilityFact]
    public void Change_feed_survives_host_store_restart_and_purges_expired_entries()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var writer = new SqlServerTransportDemandStore(
            cs,
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        writer.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));
        writer.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "b",
                TaskType = "T",
                Sublot = "S2",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));
        writer.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "b",
                TaskType = "T",
                Sublot = "S2",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "c",
                TaskType = "T",
                Sublot = "S3",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));

        clock = now.AddHours(49);
        writer.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "b",
                TaskType = "T",
                Sublot = "S2",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "c",
                TaskType = "T",
                Sublot = "S3",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "d",
                TaskType = "T",
                Sublot = "S4",
                Dates = clock,
                Status = DemandStatus.Visible,
                MesLastSeenAt = clock,
                CreatedAt = clock,
            },
        ]));

        var reader = new SqlServerTransportDemandStore(
            cs,
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        // Default afterSequence=0 must not silently resume mid-ledger after purge.
        var fromStart = Assert.Throws<SyncCursorExpiredException>(() =>
            reader.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 0, AsOf = clock }));
        Assert.True(fromStart.EarliestAvailableSequence >= 4);
        Assert.Equal(fromStart.EarliestAvailableSequence, fromStart.HighWatermark);

        var gap = Assert.Throws<SyncCursorExpiredException>(() =>
            reader.QueryChangeFeed(new DemandChangeFeedQuery
            {
                AfterSequence = fromStart.EarliestAvailableSequence!.Value - 2,
                AsOf = clock,
            }));
        Assert.Equal(fromStart.EarliestAvailableSequence, gap.EarliestAvailableSequence);

        var catchUp = reader.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = fromStart.EarliestAvailableSequence!.Value - 1,
            AsOf = clock,
        });
        Assert.Equal(1, catchUp.Items.Count);
        Assert.Equal("d", catchUp.Items[0].DemandId);
        Assert.Equal(catchUp.Items[0].Sequence, catchUp.EarliestAvailableSequence);
        Assert.Equal(catchUp.Items[0].Sequence, catchUp.HighWatermark);
    }

    [SqlServerAvailabilityFact]
    public void Change_feed_full_purge_keeps_monotonic_watermark_and_expires_stale_cursors()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var store = new SqlServerTransportDemandStore(
            cs,
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "a",
                TaskType = "T",
                Sublot = "S1",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "b",
                TaskType = "T",
                Sublot = "S2",
                Dates = now,
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));

        var beforePurge = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = clock });
        var highWatermark = beforePurge.HighWatermark;
        Assert.True(highWatermark >= 2);

        clock = now.AddHours(49);
        var empty = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = highWatermark,
            AsOf = clock,
        });
        Assert.Empty(empty.Items);
        Assert.Null(empty.EarliestAvailableSequence);
        Assert.Equal(highWatermark, empty.HighWatermark);

        var expired = Assert.Throws<SyncCursorExpiredException>(() =>
            store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 0, AsOf = clock }));
        Assert.Null(expired.EarliestAvailableSequence);
        Assert.Equal(highWatermark, expired.HighWatermark);
    }

    [SqlServerAvailabilityFact]
    public void ReplaceState_writes_only_changed_rows_and_leaves_gone_immutable()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var t0 = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var stableVisible = new TransportDemand
        {
            DemandId = "keep-visible",
            TaskType = "DIE_TO_WIRE_STAGING",
            Sublot = "Q-KEEP",
            Area = "N09-01",
            Eqp = "EQ1",
            Step = "焊线",
            Dates = t0,
            Package = "PKG-K",
            Status = DemandStatus.Visible,
            MesLastSeenAt = t0,
            DisappearCount = 0,
            LocationRisk = false,
            CreatedAt = t0.AddHours(-2),
        };
        var changingVisible = new TransportDemand
        {
            DemandId = "change-visible",
            TaskType = "DIE_TO_OVEN",
            Sublot = "Q-CHG",
            Area = "N09-02",
            Eqp = "EQ2",
            Step = "烘箱",
            Dates = t0.AddMinutes(-10),
            Package = "PKG-C",
            Status = DemandStatus.Visible,
            MesLastSeenAt = t0,
            DisappearCount = 0,
            LocationRisk = false,
            CreatedAt = t0.AddHours(-1),
        };
        var gone = new TransportDemand
        {
            DemandId = "keep-gone",
            TaskType = "WIRE_TO_GATE",
            Sublot = "Q-GONE",
            Area = "N01",
            Eqp = "EQ3",
            Step = "关卡",
            Dates = t0.AddHours(-3),
            Package = "PKG-G",
            Status = DemandStatus.Gone,
            MesLastSeenAt = t0.AddMinutes(-30),
            DisappearCount = 2,
            LocationRisk = true,
            LocationRiskCode = "AREA_EMPTY",
            CreatedAt = t0.AddHours(-4),
            GoneAt = t0.AddMinutes(-20),
        };

        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
            [stableVisible, changingVisible, gone],
            [new TaskTypePauseState("DIE_TO_OVEN", false, 5, 0)]));

        SqlServerTestEnv.InstallDemandWriteAudit(cs);

        var t1 = t0.AddMinutes(1);
        store.ReplaceState(new ProjectionState(
            [
                stableVisible, // identical → zero write
                changingVisible with { MesLastSeenAt = t1 },
                // historical GONE omitted from hot ReplaceState payload
            ],
            [new TaskTypePauseState("DIE_TO_OVEN", false, 5, 0)]));

        var writes = SqlServerTestEnv.ReadDemandWriteAudit(cs);
        Assert.DoesNotContain(writes, w => w.DemandId == "keep-visible");
        Assert.DoesNotContain(writes, w => w.DemandId == "keep-gone");
        Assert.Contains(writes, w => w.DemandId == "change-visible" && w.Op == "UPDATE");
        Assert.DoesNotContain(writes, w => w.Op == "DELETE");

        var reloadedGone = store.GetById("keep-gone");
        Assert.NotNull(reloadedGone);
        Assert.Equal(gone, reloadedGone);
        Assert.Equal(t1, store.GetById("change-visible")!.MesLastSeenAt);
        Assert.Equal(stableVisible, Assert.Single(store.GetState().Demands, d => d.DemandId == "keep-visible"));
    }

    [SqlServerAvailabilityFact]
    public void ReplaceState_commits_projection_and_alerts_atomically()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var t0 = new DateTimeOffset(2026, 8, 2, 14, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d-a",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-A",
                    Area = "N1",
                    Eqp = "E1",
                    Step = "烘箱",
                    Dates = t0,
                    Package = "P",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = t0,
                    CreatedAt = t0,
                },
            ]));

        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'dbo.CK_TransportDemands_Status_Guard', N'C') IS NOT NULL
                    ALTER TABLE dbo.TransportDemands DROP CONSTRAINT CK_TransportDemands_Status_Guard;
                ALTER TABLE dbo.TransportDemands WITH NOCHECK
                    ADD CONSTRAINT CK_TransportDemands_Status_Guard
                    CHECK (Status IN (N'VISIBLE', N'GONE') AND DemandId <> N'boom');
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            Assert.ThrowsAny<Exception>(() =>
                store.ReplaceState(
                    new ProjectionState(
                    [
                        new TransportDemand
                        {
                            DemandId = "d-a",
                            TaskType = "DIE_TO_OVEN",
                            Sublot = "Q-A",
                            Area = "N1",
                            Eqp = "E1",
                            Step = "烘箱",
                            Dates = t0,
                            Package = "P",
                            Status = DemandStatus.Visible,
                            MesLastSeenAt = t0.AddMinutes(1),
                            CreatedAt = t0,
                        },
                        new TransportDemand
                        {
                            DemandId = "boom",
                            TaskType = "DIE_TO_OVEN",
                            Sublot = "Q-B",
                            Area = "N2",
                            Eqp = "E2",
                            Step = "烘箱",
                            Dates = t0,
                            Package = "P2",
                            Status = DemandStatus.Visible,
                            MesLastSeenAt = t0.AddMinutes(1),
                            CreatedAt = t0,
                        },
                    ]),
                    [
                        new IngestAlert(Code: "REAPPEAR_AFTER_GONE", DemandId: "boom", Message: "should roll back"),
                    ]));

            Assert.Equal(t0, store.GetById("d-a")!.MesLastSeenAt);
            Assert.Empty(store.ListAlerts());
        }
        finally
        {
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'dbo.CK_TransportDemands_Status_Guard', N'C') IS NOT NULL
                    ALTER TABLE dbo.TransportDemands DROP CONSTRAINT CK_TransportDemands_Status_Guard;
                """;
            cmd.ExecuteNonQuery();
        }
    }

    [SqlServerAvailabilityFact]
    public void ReplaceState_is_atomic_across_demand_and_pause_writes()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var t0 = new DateTimeOffset(2026, 8, 2, 13, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d-a",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-A",
                    Area = "N1",
                    Eqp = "E1",
                    Step = "烘箱",
                    Dates = t0,
                    Package = "P",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = t0,
                    CreatedAt = t0,
                },
            ],
            [new TaskTypePauseState("DIE_TO_OVEN", false, 3, 0)]));

        // Force mid-transaction failure after first demand write via CHECK that rejects Status.
        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'dbo.CK_TransportDemands_Status_Guard', N'C') IS NOT NULL
                    ALTER TABLE dbo.TransportDemands DROP CONSTRAINT CK_TransportDemands_Status_Guard;
                ALTER TABLE dbo.TransportDemands WITH NOCHECK
                    ADD CONSTRAINT CK_TransportDemands_Status_Guard
                    CHECK (Status IN (N'VISIBLE', N'GONE') AND DemandId <> N'boom');
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            var boom = Assert.ThrowsAny<Exception>(() =>
                store.ReplaceState(new ProjectionState(
                    [
                        new TransportDemand
                        {
                            DemandId = "d-a",
                            TaskType = "DIE_TO_OVEN",
                            Sublot = "Q-A",
                            Area = "N1",
                            Eqp = "E1",
                            Step = "烘箱",
                            Dates = t0,
                            Package = "P",
                            Status = DemandStatus.Visible,
                            MesLastSeenAt = t0.AddMinutes(1),
                            CreatedAt = t0,
                        },
                        new TransportDemand
                        {
                            DemandId = "boom",
                            TaskType = "DIE_TO_OVEN",
                            Sublot = "Q-B",
                            Area = "N2",
                            Eqp = "E2",
                            Step = "烘箱",
                            Dates = t0,
                            Package = "P2",
                            Status = DemandStatus.Visible,
                            MesLastSeenAt = t0.AddMinutes(1),
                            CreatedAt = t0,
                        },
                    ],
                    [new TaskTypePauseState("DIE_TO_OVEN", true, 3, 0)])));
            Assert.Contains("CHECK", boom.Message + (boom.InnerException?.Message ?? ""), StringComparison.OrdinalIgnoreCase);

            // Neither demand MesLastSeenAt bump nor pause flip may be visible.
            Assert.Equal(t0, store.GetById("d-a")!.MesLastSeenAt);
            Assert.Null(store.GetById("boom"));
            var pause = Assert.Single(store.GetState().TaskTypePauses);
            Assert.False(pause.PausedZeroDrop);
        }
        finally
        {
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'dbo.CK_TransportDemands_Status_Guard', N'C') IS NOT NULL
                    ALTER TABLE dbo.TransportDemands DROP CONSTRAINT CK_TransportDemands_Status_Guard;
                """;
            cmd.ExecuteNonQuery();
        }
    }

    [SqlServerAvailabilityFact]
    public void Persisted_alerts_and_poll_health_are_readable_after_new_store_instance()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var started = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var ended = started.AddSeconds(3);

        var writer = new SqlServerTransportDemandStore(cs);
        writer.ReplaceState(
            ProjectionState.Empty,
            [
                new IngestAlert(
                    Code: "FIELD_DRIFT",
                    TaskType: "DIE_TO_WIRE_STAGING",
                    Sublot: "Q1",
                    DemandId: "d1",
                    Message: "drift",
                    Details: """{"fields":[{"field":"Area","frozen":"A","observed":"B"}]}"""),
                new IngestAlert(
                    Code: "PAUSED_ZERO_DROP",
                    TaskType: "DIE_TO_OVEN",
                    Message: "paused",
                    Details: """{"lastHealthyNonZeroCount":12,"recoveryStreak":0,"enterThreshold":10,"clearStreakRequired":3}"""),
            ]);
        writer.SetLatestPollHealth(new PollHealth(
            StartedAt: started,
            EndedAt: ended,
            DurationMs: 3000,
            RowCount: 42,
            Success: true,
            Outcome: "SUCCESS"));

        var reader = new SqlServerTransportDemandStore(cs);
        var alerts = reader.ListAlerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Code == "FIELD_DRIFT" && a.DemandId == "d1");
        Assert.Contains(alerts, a => a.Code == "PAUSED_ZERO_DROP" && a.TaskType == "DIE_TO_OVEN");
        Assert.All(alerts, a => Assert.NotNull(a.CreatedAt));
        Assert.All(alerts, a => Assert.False(string.IsNullOrWhiteSpace(a.AlertId)));
        Assert.Contains(alerts, a => a.Code == "PAUSED_ZERO_DROP");

        var health = reader.GetLatestPollHealth();
        Assert.NotNull(health);
        Assert.Equal(started, health.StartedAt);
        Assert.Equal(ended, health.EndedAt);
        Assert.Equal(3000, health.DurationMs);
        Assert.Equal(42, health.RowCount);
        Assert.True(health.Success);
        Assert.Equal("SUCCESS", health.Outcome);
    }

    [SqlServerAvailabilityFact]
    public void ReplaceState_does_not_rewrite_unchanged_resolved_alert_history()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var store = new SqlServerTransportDemandStore(cs, clock: () => now);
        var alert = new IngestAlert(
            AlertCodes.FieldDrift,
            TaskType: "TYPE",
            Sublot: "Q-1",
            DemandId: "d-1",
            Message: "drift");

        store.ReplaceState(ProjectionState.Empty, [alert]);
        store.ReplaceState(ProjectionState.Empty, []);
        SqlServerTestEnv.InstallAlertWriteAudit(cs);

        store.ReplaceState(ProjectionState.Empty, []);

        Assert.Empty(SqlServerTestEnv.ReadAlertWriteAudit(cs));
    }

    [SqlServerAvailabilityFact]
    public void Alert_query_pages_new_sort_columns_on_the_shared_contract()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var store = new SqlServerTransportDemandStore(cs, clock: () => now);
        store.ReplaceState(ProjectionState.Empty,
        [
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-B", Sublot: "S-B"),
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-A", Sublot: "S-A"),
            new IngestAlert(AlertCodes.FieldDrift, TaskType: "T-C", Sublot: "S-C"),
        ]);

        var first = store.QueryAlerts(new AlertListQuery
        {
            SortBy = AlertSortColumn.TaskType,
            Direction = SortDirection.Asc,
            Limit = 2,
            UseDefaultPrioritySort = false,
        });
        var second = store.QueryAlerts(new AlertListQuery
        {
            SortBy = AlertSortColumn.TaskType,
            Direction = SortDirection.Asc,
            Limit = 2,
            Cursor = first.NextCursor,
            UseDefaultPrioritySort = false,
        });

        Assert.Equal(new[] { "T-A", "T-B" }, first.Items.Select(alert => alert.TaskType));
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "T-C" }, second.Items.Select(alert => alert.TaskType));
        Assert.False(second.HasMore);
    }

    [SqlServerAvailabilityFact]
    public void Query_page_uses_read_path_indexes_and_keyset_pagination()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);
        _ = new SqlServerTransportDemandStore(cs);

        using (var conn = new SqlConnection(cs))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                """
                SELECT name FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.TransportDemands')
                  AND name IN (
                    N'IX_TransportDemands_Status_Dates_DemandId',
                    N'IX_TransportDemands_TaskType_Sublot',
                    N'IX_TransportDemands_GoneAt_DemandId');
                """,
                conn);
            var names = new HashSet<string>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            Assert.Contains("IX_TransportDemands_Status_Dates_DemandId", names);
            Assert.Contains("IX_TransportDemands_TaskType_Sublot", names);
            Assert.Contains("IX_TransportDemands_GoneAt_DemandId", names);
        }

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
        [
            new TransportDemand
            {
                DemandId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q1",
                Dates = now.AddMinutes(1),
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                TaskType = "DIE_TO_OVEN",
                Sublot = "Q2",
                Dates = now.AddMinutes(2),
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
            new TransportDemand
            {
                DemandId = "cccccccccccccccccccccccccccccccc",
                TaskType = "WIRE_TO_GATE",
                Sublot = "Q3",
                Dates = now.AddMinutes(3),
                Status = DemandStatus.Visible,
                MesLastSeenAt = now,
                CreatedAt = now,
            },
        ]));

        var first = store.QueryPage(new DemandListQuery
        {
            Status = DemandStatus.Visible,
            Limit = 2,
            AsOf = now,
        });
        Assert.True(first.HasMore);
        Assert.Equal(
            new[] { "cccccccccccccccccccccccccccccccc", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            first.Items.Select(d => d.DemandId).ToArray());

        var second = store.QueryPage(new DemandListQuery
        {
            Status = DemandStatus.Visible,
            Limit = 2,
            Cursor = first.NextCursor,
            AsOf = now,
        });
        Assert.False(second.HasMore);
        Assert.Equal(
            new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            second.Items.Select(d => d.DemandId).ToArray());
    }

    [SqlServerAvailabilityFact]
    public void Query_page_desc_with_tied_primary_values_has_no_gap_or_dup()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
        [
            SqlDemand("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", now),
            SqlDemand("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", now),
            SqlDemand("cccccccccccccccccccccccccccccccc", now),
            SqlDemand("dddddddddddddddddddddddddddddddd", now),
            SqlDemand("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", now),
        ]));

        var collected = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = store.QueryPage(new DemandListQuery
            {
                Status = DemandStatus.Visible,
                SortBy = DemandSortColumn.Dates,
                Direction = SortDirection.Desc,
                Limit = 2,
                Cursor = cursor,
                AsOf = now,
            });
            collected.AddRange(page.Items.Select(d => d.DemandId));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(
            new[]
            {
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "cccccccccccccccccccccccccccccccc",
                "dddddddddddddddddddddddddddddddd",
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            },
            collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [SqlServerAvailabilityFact]
    public void Query_page_desc_page_boundary_inside_tie_group_has_no_gap_or_dup()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var earlier = now.AddHours(-1);
        var later = now.AddHours(1);
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
        [
            SqlDemand("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", earlier),
            SqlDemand("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", now),
            SqlDemand("cccccccccccccccccccccccccccccccc", now),
            SqlDemand("dddddddddddddddddddddddddddddddd", now),
            SqlDemand("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", later),
        ]));

        var collected = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = store.QueryPage(new DemandListQuery
            {
                Status = DemandStatus.Visible,
                SortBy = DemandSortColumn.Dates,
                Direction = SortDirection.Desc,
                Limit = 2,
                Cursor = cursor,
                AsOf = now,
            });
            collected.AddRange(page.Items.Select(d => d.DemandId));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(
            new[]
            {
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "cccccccccccccccccccccccccccccccc",
                "dddddddddddddddddddddddddddddddd",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [SqlServerAvailabilityFact]
    public void Query_page_new_visible_columns_cover_nulls_ties_and_both_directions()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new SqlServerTransportDemandStore(cs);
        store.ReplaceState(new ProjectionState(
        [
            SortableSqlDemand("a1", now, "B", null, "2", "P1", locationRisk: true, disappearCount: 2),
            SortableSqlDemand("a2", now, null, "E2", "1", null, locationRisk: false, disappearCount: 1),
            SortableSqlDemand("a3", now, "A", "E1", null, "P2", locationRisk: true, disappearCount: 1),
            SortableSqlDemand("a4", now, "A", "E1", "3", "P1", locationRisk: false, disappearCount: 0),
            SortableSqlDemand("a5", now, null, "E3", null, null, locationRisk: false, disappearCount: 2),
        ]));

        var cases = new (DemandSortColumn SortBy, SortDirection Direction, string[] ExpectedIds)[]
        {
            (DemandSortColumn.Status, SortDirection.Asc, ["a1", "a2", "a3", "a4", "a5"]),
            (DemandSortColumn.Status, SortDirection.Desc, ["a1", "a2", "a3", "a4", "a5"]),
            (DemandSortColumn.Area, SortDirection.Asc, ["a2", "a5", "a3", "a4", "a1"]),
            (DemandSortColumn.Area, SortDirection.Desc, ["a1", "a3", "a4", "a2", "a5"]),
            (DemandSortColumn.Eqp, SortDirection.Asc, ["a1", "a3", "a4", "a2", "a5"]),
            (DemandSortColumn.Eqp, SortDirection.Desc, ["a5", "a2", "a3", "a4", "a1"]),
            (DemandSortColumn.Step, SortDirection.Asc, ["a3", "a5", "a2", "a1", "a4"]),
            (DemandSortColumn.Step, SortDirection.Desc, ["a4", "a1", "a2", "a3", "a5"]),
            (DemandSortColumn.Package, SortDirection.Asc, ["a2", "a5", "a1", "a4", "a3"]),
            (DemandSortColumn.Package, SortDirection.Desc, ["a3", "a1", "a4", "a2", "a5"]),
            (DemandSortColumn.LocationRisk, SortDirection.Asc, ["a2", "a4", "a5", "a1", "a3"]),
            (DemandSortColumn.LocationRisk, SortDirection.Desc, ["a1", "a3", "a2", "a4", "a5"]),
            (DemandSortColumn.DisappearCount, SortDirection.Asc, ["a4", "a2", "a3", "a1", "a5"]),
            (DemandSortColumn.DisappearCount, SortDirection.Desc, ["a1", "a5", "a2", "a3", "a4"]),
        };

        foreach (var sortCase in cases)
        {
            var actualIds = CollectSqlIds(store, sortCase.SortBy, sortCase.Direction, now);
            Assert.Equal(sortCase.ExpectedIds, actualIds);
            Assert.Equal(actualIds.Count, actualIds.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private static List<string> CollectSqlIds(
        SqlServerTransportDemandStore store,
        DemandSortColumn sortBy,
        SortDirection direction,
        DateTimeOffset asOf)
    {
        var collected = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = store.QueryPage(new DemandListQuery
            {
                Status = DemandStatus.Visible,
                SortBy = sortBy,
                Direction = direction,
                Limit = 2,
                Cursor = cursor,
                AsOf = asOf,
            });
            collected.AddRange(page.Items.Select(demand => demand.DemandId));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return collected;
    }

    private static TransportDemand SortableSqlDemand(
        string demandId,
        DateTimeOffset now,
        string? area,
        string? eqp,
        string? step,
        string? package,
        bool locationRisk,
        int disappearCount) =>
        new()
        {
            DemandId = demandId,
            TaskType = "DIE_TO_OVEN",
            Sublot = "SAME_SUBLOT",
            Area = area,
            Eqp = eqp,
            Step = step,
            Dates = now,
            Package = package,
            Status = DemandStatus.Visible,
            MesLastSeenAt = now,
            DisappearCount = disappearCount,
            LocationRisk = locationRisk,
            CreatedAt = now,
        };

    private static TransportDemand SqlDemand(string id, DateTimeOffset dates) =>
        new()
        {
            DemandId = id,
            TaskType = "DIE_TO_OVEN",
            Sublot = "Q1",
            Dates = dates,
            Status = DemandStatus.Visible,
            MesLastSeenAt = dates,
            CreatedAt = dates,
        };
}

internal static class SqlServerTestEnv
{
    public static string? ConnectionString { get; } = Resolve();

    public static bool IsAvailable => ConnectionString is not null;

    private static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MES_INGEST_SQLSERVER");
        var candidates = new[]
        {
            fromEnv,
            @"Server=(localdb)\MSSQLLocalDB;Database=MesIngest_Ticket05_Smoke;Trusted_Connection=True;TrustServerCertificate=True",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(candidate);
                var database = builder.InitialCatalog;
                builder.InitialCatalog = "master";
                using var conn = new SqlConnection(builder.ConnectionString);
                conn.Open();
                if (!string.IsNullOrWhiteSpace(database))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"""
                        IF DB_ID(N'{database.Replace("'", "''")}') IS NULL
                            CREATE DATABASE [{database.Replace("]", "]]")}];
                        """;
                    cmd.ExecuteNonQuery();
                }

                return candidate;
            }
            catch (SqlException)
            {
                // try next candidate
            }
            catch (InvalidOperationException)
            {
                // try next candidate
            }
        }

        return null;
    }

    public static void WipeProjection(string connectionString)
    {
        // Hard wipe: incremental ReplaceState(Empty) must not erase permanent GONE history.
        _ = new SqlServerTransportDemandStore(connectionString);
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.TR_TransportDemands_WriteAudit', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_TransportDemands_WriteAudit;
            IF OBJECT_ID(N'dbo.DemandWriteAudit', N'U') IS NOT NULL
                DROP TABLE dbo.DemandWriteAudit;
            IF OBJECT_ID(N'dbo.TR_IngestAlerts_WriteAudit', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_IngestAlerts_WriteAudit;
            IF OBJECT_ID(N'dbo.AlertWriteAudit', N'U') IS NOT NULL
                DROP TABLE dbo.AlertWriteAudit;
            IF OBJECT_ID(N'dbo.CK_TransportDemands_Status_Guard', N'C') IS NOT NULL
                ALTER TABLE dbo.TransportDemands DROP CONSTRAINT CK_TransportDemands_Status_Guard;
            DELETE FROM dbo.TransportDemands;
            DELETE FROM dbo.TaskTypePauses;
            DELETE FROM dbo.IngestAlerts;
            DELETE FROM dbo.PollHealth;
            IF OBJECT_ID(N'dbo.DemandChangeFeed', N'U') IS NOT NULL
                TRUNCATE TABLE dbo.DemandChangeFeed;
            """;
        cmd.ExecuteNonQuery();
    }

    public static void InstallDemandWriteAudit(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.TR_TransportDemands_WriteAudit', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_TransportDemands_WriteAudit;
            IF OBJECT_ID(N'dbo.DemandWriteAudit', N'U') IS NOT NULL
                DROP TABLE dbo.DemandWriteAudit;
            CREATE TABLE dbo.DemandWriteAudit
            (
                Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                DemandId NVARCHAR(64) NOT NULL,
                Op NVARCHAR(16) NOT NULL
            );
            EXEC(N'
                CREATE TRIGGER dbo.TR_TransportDemands_WriteAudit
                ON dbo.TransportDemands
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.DemandWriteAudit (DemandId, Op)
                    SELECT DemandId, N''INSERT'' FROM inserted
                    WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.DemandId = inserted.DemandId);
                    INSERT INTO dbo.DemandWriteAudit (DemandId, Op)
                    SELECT DemandId, N''UPDATE'' FROM inserted
                    WHERE EXISTS (SELECT 1 FROM deleted d WHERE d.DemandId = inserted.DemandId);
                    INSERT INTO dbo.DemandWriteAudit (DemandId, Op)
                    SELECT DemandId, N''DELETE'' FROM deleted
                    WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.DemandId = deleted.DemandId);
                END
            ');
            """;
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyList<(string DemandId, string Op)> ReadDemandWriteAudit(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DemandId, Op FROM dbo.DemandWriteAudit ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var list = new List<(string DemandId, string Op)>();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    public static void InstallAlertWriteAudit(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'dbo.TR_IngestAlerts_WriteAudit', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_IngestAlerts_WriteAudit;
            IF OBJECT_ID(N'dbo.AlertWriteAudit', N'U') IS NOT NULL
                DROP TABLE dbo.AlertWriteAudit;
            CREATE TABLE dbo.AlertWriteAudit
            (
                Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                AlertId NVARCHAR(64) NOT NULL,
                Op NVARCHAR(16) NOT NULL
            );
            EXEC(N'
                CREATE TRIGGER dbo.TR_IngestAlerts_WriteAudit
                ON dbo.IngestAlerts
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.AlertWriteAudit (AlertId, Op)
                    SELECT AlertId, N''INSERT'' FROM inserted
                    WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.AlertId = inserted.AlertId);
                    INSERT INTO dbo.AlertWriteAudit (AlertId, Op)
                    SELECT AlertId, N''UPDATE'' FROM inserted
                    WHERE EXISTS (SELECT 1 FROM deleted d WHERE d.AlertId = inserted.AlertId);
                    INSERT INTO dbo.AlertWriteAudit (AlertId, Op)
                    SELECT AlertId, N''DELETE'' FROM deleted
                    WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.AlertId = deleted.AlertId);
                END
            ');
            """;
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyList<(string AlertId, string Op)> ReadAlertWriteAudit(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AlertId, Op FROM dbo.AlertWriteAudit ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var list = new List<(string AlertId, string Op)>();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerAvailabilityFactAttribute : FactAttribute
{
    public SqlServerAvailabilityFactAttribute()
    {
        if (!SqlServerTestEnv.IsAvailable)
        {
            Skip = "SQL Server unavailable (set MES_INGEST_SQLSERVER or enable LocalDB)";
        }
    }
}
