using MesIngest.Core;

namespace MesIngest.Tests;

public class DemandChangeFeedStoreTests
{
    [Fact]
    public void ReplaceState_appends_created_and_gone_only_not_last_seen_updates()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);

        var visible = Demand("d1", DemandStatus.Visible, now, "DIE_TO_OVEN", "Q1");
        store.ReplaceState(new ProjectionState([visible]));

        var createdPage = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now });
        var created = Assert.Single(createdPage.Items);
        Assert.Equal(1, created.Sequence);
        Assert.Equal(DemandChangeType.Created, created.ChangeType);
        Assert.Equal("d1", created.DemandId);
        Assert.Equal(DemandStatus.Visible, created.Payload.Status);
        Assert.Equal(1, createdPage.HighWatermark);
        Assert.Equal(1, createdPage.EarliestAvailableSequence);

        var refreshed = visible with { MesLastSeenAt = now.AddMinutes(1), DisappearCount = 1 };
        store.ReplaceState(new ProjectionState([refreshed]));
        Assert.Equal(1, store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now }).Items.Count);

        var gone = refreshed with
        {
            Status = DemandStatus.Gone,
            GoneAt = now.AddMinutes(2),
            DisappearCount = 2,
            MesLastSeenAt = now.AddMinutes(2),
        };
        store.ReplaceState(new ProjectionState([gone]));

        var all = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now });
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(DemandChangeType.Created, all.Items[0].ChangeType);
        Assert.Equal(DemandChangeType.Gone, all.Items[1].ChangeType);
        Assert.Equal(DemandStatus.Gone, all.Items[1].Payload.Status);
        Assert.Equal(now.AddMinutes(2), all.Items[1].Payload.GoneAt);
        Assert.Equal(2, all.HighWatermark);
    }

    [Fact]
    public void QueryChangeFeed_pages_by_afterSequence_and_is_idempotent_for_repeat_reads()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
        ]));

        var first = store.QueryChangeFeed(new DemandChangeFeedQuery { Limit = 2, AsOf = now });
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextAfterSequence);
        Assert.Equal(3, first.HighWatermark);

        var second = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = first.NextAfterSequence!.Value,
            Limit = 2,
            AsOf = now,
        });
        Assert.Equal(new[] { "c" }, second.Items.Select(i => i.DemandId).ToArray());
        Assert.False(second.HasMore);
        Assert.Null(second.NextAfterSequence);

        var repeat = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = first.NextAfterSequence!.Value,
            Limit = 2,
            AsOf = now,
        });
        Assert.Equal(second.Items.Select(i => i.Sequence).ToArray(), repeat.Items.Select(i => i.Sequence).ToArray());
    }

    [Fact]
    public void QueryChangeFeed_expired_cursor_throws_sync_cursor_expired()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        store.ReplaceState(new ProjectionState([Demand("a", DemandStatus.Visible, now, "T", "S1")]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
        ]));

        clock = now.AddHours(49);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
            Demand("c", DemandStatus.Visible, now, "T", "S3"),
            Demand("d", DemandStatus.Visible, clock, "T", "S4"),
        ]));

        // afterSequence == 0 is not a free pass once earliest > 1 — same 410 as any gap.
        var fromStart = Assert.Throws<SyncCursorExpiredException>(() =>
            store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 0, AsOf = clock }));
        Assert.Equal(0, fromStart.AfterSequence);
        Assert.Equal(4, fromStart.EarliestAvailableSequence);
        Assert.Equal(4, fromStart.HighWatermark);

        // Consumer checkpoint 2 missed purged sequence 3 → must Bootstrap, not silently skip.
        var ex = Assert.Throws<SyncCursorExpiredException>(() =>
            store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 2, AsOf = clock }));
        Assert.Equal(2, ex.AfterSequence);
        Assert.Equal(4, ex.EarliestAvailableSequence);
        Assert.Equal(4, ex.HighWatermark);

        // Checkpoint exactly at earliest-1 is still contiguous.
        var catchUp = store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 3, AsOf = clock });
        Assert.Equal(new[] { "d" }, catchUp.Items.Select(i => i.DemandId).ToArray());
        Assert.Equal(4, catchUp.HighWatermark);
    }

    [Fact]
    public void QueryChangeFeed_full_purge_keeps_monotonic_watermark_and_expires_stale_cursors()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var clock = now;
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => clock);

        store.ReplaceState(new ProjectionState([Demand("a", DemandStatus.Visible, now, "T", "S1")]));
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));

        var beforePurge = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = clock });
        Assert.Equal(2, beforePurge.HighWatermark);

        // Advance past retention with no new feed rows → ledger empty, watermark must not fall to 0.
        clock = now.AddHours(49);
        var empty = store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 2, AsOf = clock });
        Assert.Empty(empty.Items);
        Assert.Null(empty.EarliestAvailableSequence);
        Assert.Equal(2, empty.HighWatermark);

        var expired = Assert.Throws<SyncCursorExpiredException>(() =>
            store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 0, AsOf = clock }));
        Assert.Equal(0, expired.AfterSequence);
        Assert.Null(expired.EarliestAvailableSequence);
        Assert.Equal(2, expired.HighWatermark);

        var midGap = Assert.Throws<SyncCursorExpiredException>(() =>
            store.QueryChangeFeed(new DemandChangeFeedQuery { AfterSequence = 1, AsOf = clock }));
        Assert.Equal(2, midGap.HighWatermark);
    }

    [Fact]
    public void Bootstrap_high_watermark_then_catch_up_does_not_lose_concurrent_changes()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);

        var visibleA = Demand("a", DemandStatus.Visible, now, "T", "SA");
        var visibleB = Demand("b", DemandStatus.Visible, now, "T", "SB");
        var recentGone = Demand("g", DemandStatus.Gone, now.AddHours(-2), "T", "SG", goneAt: now.AddHours(-1));
        store.ReplaceState(new ProjectionState([visibleA, visibleB, recentGone]));

        var watermark = store.QueryChangeFeed(new DemandChangeFeedQuery { AsOf = now }).HighWatermark;
        Assert.Equal(3, watermark);

        // Authoritative bootstrap: all VISIBLE + GoneAt >= now-24h GONE (replace, do not merge).
        var bootstrapVisible = store.QueryPage(new DemandListQuery
        {
            Status = DemandStatus.Visible,
            Limit = 200,
            AsOf = now,
        });
        var bootstrapGone = store.QueryPage(new DemandListQuery
        {
            Status = DemandStatus.Gone,
            Limit = 200,
            AsOf = now,
        });
        var mirror = bootstrapVisible.Items
            .Concat(bootstrapGone.Items)
            .ToDictionary(d => d.DemandId, StringComparer.Ordinal);
        Assert.Equal(3, mirror.Count);
        Assert.Equal(DemandStatus.Gone, mirror["g"].Status);

        // Concurrent CREATED + GONE after watermark was taken.
        var goneA = visibleA with { Status = DemandStatus.Gone, GoneAt = now.AddMinutes(1), DisappearCount = 2 };
        var visibleC = Demand("c", DemandStatus.Visible, now.AddMinutes(1), "T", "SC");
        store.ReplaceState(new ProjectionState([goneA, visibleB, recentGone, visibleC]));

        var catchUp = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = watermark,
            AsOf = now,
        });
        Assert.Contains(catchUp.Items, i => i.DemandId == "a" && i.ChangeType == DemandChangeType.Gone);
        Assert.Contains(catchUp.Items, i => i.DemandId == "c" && i.ChangeType == DemandChangeType.Created);

        foreach (var change in catchUp.Items)
        {
            mirror[change.DemandId] = DemandFromPayload(change.Payload);
        }

        Assert.Equal(DemandStatus.Gone, mirror["a"].Status);
        Assert.Equal(DemandStatus.Visible, mirror["b"].Status);
        Assert.Equal(DemandStatus.Visible, mirror["c"].Status);
        Assert.Equal(DemandStatus.Gone, mirror["g"].Status);
    }

    [Fact]
    public void Two_independent_consumers_can_page_the_same_feed()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        store.ReplaceState(new ProjectionState(
        [
            Demand("a", DemandStatus.Visible, now, "T", "S1"),
            Demand("b", DemandStatus.Visible, now, "T", "S2"),
        ]));

        var consumerOne = store.QueryChangeFeed(new DemandChangeFeedQuery { Limit = 1, AsOf = now });
        var consumerTwo = store.QueryChangeFeed(new DemandChangeFeedQuery { Limit = 1, AsOf = now });
        Assert.Equal(consumerOne.Items[0].Sequence, consumerTwo.Items[0].Sequence);

        var oneNext = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = consumerOne.NextAfterSequence!.Value,
            AsOf = now,
        });
        var twoNext = store.QueryChangeFeed(new DemandChangeFeedQuery
        {
            AfterSequence = consumerTwo.NextAfterSequence!.Value,
            AsOf = now,
        });
        Assert.Equal(oneNext.Items.Select(i => i.Sequence), twoNext.Items.Select(i => i.Sequence));
    }

    private static TransportDemand Demand(
        string id,
        DemandStatus status,
        DateTimeOffset dates,
        string taskType,
        string sublot,
        DateTimeOffset? goneAt = null) =>
        new()
        {
            DemandId = id,
            TaskType = taskType,
            Sublot = sublot,
            Dates = dates,
            Status = status,
            MesLastSeenAt = dates,
            GoneAt = goneAt,
            CreatedAt = dates,
        };

    private static TransportDemand DemandFromPayload(DemandChangePayload p) =>
        new()
        {
            DemandId = p.DemandId,
            TaskType = p.TaskType,
            Sublot = p.Sublot,
            Area = p.Area,
            Eqp = p.Eqp,
            Step = p.Step,
            Dates = p.Dates,
            Package = p.Package,
            Status = p.Status,
            MesLastSeenAt = p.CreatedAt,
            LocationRisk = p.LocationRisk,
            LocationRiskCode = p.LocationRiskCode,
            CreatedAt = p.CreatedAt,
            GoneAt = p.GoneAt,
        };
}
