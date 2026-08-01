using System.Collections.Concurrent;
using MesIngest.Core;

namespace MesIngest.Tests;

/// <summary>
/// Ticket 06: default InMemory store must tolerate concurrent API readers + single poll writer.
/// Regression nails for unsynchronized _alerts / _changeFeed mutation and write-boundary publish.
/// </summary>
public class InMemoryTransportDemandStoreConcurrencyTests
{
    [Fact]
    public void Concurrent_alert_readers_and_replace_writer_do_not_throw()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        Seed(store, now, demandCount: 8);

        var failures = new ConcurrentBag<Exception>();
        var violations = new ConcurrentBag<string>();
        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            ready.Wait();
            var i = 0;
            while (!stop.IsSet)
            {
                i++;
                var demands = Enumerable.Range(0, 8)
                    .Select(n => Demand($"d{n}", now.AddMinutes(n + (i % 3)), "T", $"S{n}"))
                    .ToList();
                var alerts = Enumerable.Range(0, 6)
                    .Select(n => new IngestAlert(
                        Code: "POLL_FAILURE",
                        TaskType: "T",
                        Sublot: $"S{n}",
                        DemandId: $"d{n}",
                        Message: $"round-{i}-{n}",
                        CreatedAt: now.AddSeconds(i)))
                    .ToList();
                try
                {
                    store.ReplaceState(new ProjectionState(demands), alerts);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        });

        var readers = Enumerable.Range(0, 8).Select(__ => Task.Run(() =>
        {
            ready.Wait();
            while (!stop.IsSet)
            {
                try
                {
                    var page = store.QueryAlerts(new AlertListQuery { Limit = 50, UseDefaultPrioritySort = true });
                    CheckAlertEnvelope(page, limit: 50, violations);
                    var listed = store.ListAlerts();
                    if (listed is null)
                    {
                        violations.Add("ListAlerts returned null");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        })).ToArray();

        RunStress(ready, stop, readers, writer);

        Assert.True(
            failures.IsEmpty,
            "Expected no concurrent enumeration/mutation failures, got: "
            + string.Join(" | ", failures.Select(Describe)));
        Assert.True(
            violations.IsEmpty,
            "Envelope violations: " + string.Join(" | ", violations.Take(8)));
    }

    [Fact]
    public void Concurrent_change_feed_readers_and_replace_writer_do_not_throw()
    {
        var clock = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromMinutes(5),
            clock: () => clock);
        Seed(store, clock, demandCount: 4);

        var failures = new ConcurrentBag<Exception>();
        var violations = new ConcurrentBag<string>();
        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            ready.Wait();
            var i = 0;
            while (!stop.IsSet)
            {
                i++;
                // Advance clock so PurgeChangeFeed RemoveAll races with readers.
                clock = clock.AddSeconds(30);
                var demands = Enumerable.Range(0, 4)
                    .Select(n => Demand($"d{n}-r{i}", clock, "T", $"S{n}"))
                    .ToList();
                try
                {
                    store.ReplaceState(new ProjectionState(demands));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        });

        var readers = Enumerable.Range(0, 8).Select(__ => Task.Run(() =>
        {
            ready.Wait();
            while (!stop.IsSet)
            {
                try
                {
                    var page = store.QueryChangeFeed(new DemandChangeFeedQuery
                    {
                        AfterSequence = 0,
                        Limit = 50,
                        AsOf = clock,
                    });
                    CheckChangeFeedEnvelope(page, limit: 50, violations);
                }
                catch (SyncCursorExpiredException)
                {
                    // Retention can expire the bootstrap cursor; that is a contract response, not a crash.
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        })).ToArray();

        RunStress(ready, stop, readers, writer);

        Assert.True(
            failures.IsEmpty,
            "Expected no concurrent enumeration/mutation failures, got: "
            + string.Join(" | ", failures.Select(Describe)));
        Assert.True(
            violations.IsEmpty,
            "Envelope violations: " + string.Join(" | ", violations.Take(8)));
    }

    [Fact]
    public void Concurrent_demand_page_readers_and_replace_writer_do_not_throw()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(clock: () => now);
        Seed(store, now, demandCount: 8);

        var failures = new ConcurrentBag<Exception>();
        var violations = new ConcurrentBag<string>();
        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            ready.Wait();
            var i = 0;
            while (!stop.IsSet)
            {
                i++;
                var demands = Enumerable.Range(0, 8)
                    .Select(n => Demand($"d{n}-r{i}", now.AddMinutes(n + (i % 3)), "T", $"S{n}"))
                    .ToList();
                try
                {
                    store.ReplaceState(new ProjectionState(demands));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        });

        var readers = Enumerable.Range(0, 8).Select(__ => Task.Run(() =>
        {
            ready.Wait();
            while (!stop.IsSet)
            {
                try
                {
                    var page = store.QueryPage(new DemandListQuery { Limit = 5 });
                    CheckDemandEnvelope(page, limit: 5, violations);
                    GC.KeepAlive(store.List());
                    GC.KeepAlive(store.GetState());
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        })).ToArray();

        RunStress(ready, stop, readers, writer);

        Assert.True(
            failures.IsEmpty,
            "Expected no concurrent demand-page failures, got: "
            + string.Join(" | ", failures.Select(Describe)));
        Assert.True(
            violations.IsEmpty,
            "Envelope violations: " + string.Join(" | ", violations.Take(8)));
    }

    [Fact]
    public void Concurrent_readers_see_demand_created_feed_and_alert_write_boundary()
    {
        // Per-round demand ids force fresh CREATED feed entries on every ReplaceState.
        // Alert invariant is checked inside a single ListAlerts call (cross-call samples can tear
        // between committed rounds even when ReplaceState itself is atomic).
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new InMemoryTransportDemandStore(
            changeFeedRetention: TimeSpan.FromHours(48),
            clock: () => now);
        Seed(store, now, demandCount: 4);

        var failures = new ConcurrentBag<Exception>();
        var violations = new ConcurrentBag<string>();
        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            ready.Wait();
            var i = 0;
            while (!stop.IsSet)
            {
                i++;
                var demands = Enumerable.Range(0, 4)
                    .Select(n => Demand($"r{i}-d{n}", now.AddMinutes(i % 5), "T", $"S{n}"))
                    .ToList();
                var alerts = Enumerable.Range(0, 4)
                    .Select(n => new IngestAlert(
                        Code: AlertCodes.ReappearAfterGone,
                        TaskType: "T",
                        Sublot: $"S{n}",
                        DemandId: $"r{i}-d{n}",
                        Message: $"bound-{i}-{n}",
                        CreatedAt: now.AddSeconds(i)))
                    .ToList();
                try
                {
                    store.ReplaceState(new ProjectionState(demands), alerts);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        });

        var readers = Enumerable.Range(0, 6).Select(__ => Task.Run(() =>
        {
            ready.Wait();
            while (!stop.IsSet)
            {
                try
                {
                    var visible = store.GetState().Demands
                        .Where(d => d.DemandId.Contains("-d", StringComparison.Ordinal))
                        .ToList();
                    if (visible.Count == 0)
                    {
                        continue;
                    }

                    var roundPrefixes = visible
                        .Select(d => d.DemandId.Split('-', 2)[0])
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (roundPrefixes.Count != 1)
                    {
                        violations.Add(
                            $"mixed demand rounds in GetState: [{string.Join(",", roundPrefixes)}]");
                    }

                    var createdIds = CollectCreatedDemandIds(store, now);
                    foreach (var demand in visible)
                    {
                        if (!createdIds.Contains(demand.DemandId))
                        {
                            violations.Add($"visible {demand.DemandId} missing CREATED feed entry");
                        }
                    }

                    var round = roundPrefixes[0];
                    var activeBound = store.ListAlerts(limit: 100)
                        .Where(a =>
                            a.IsActive
                            && string.Equals(a.Code, AlertCodes.ReappearAfterGone, StringComparison.Ordinal)
                            && a.DemandId is not null
                            && a.DemandId.StartsWith(round + "-", StringComparison.Ordinal))
                        .Select(a => a.DemandId!)
                        .ToHashSet(StringComparer.Ordinal);
                    // Empty is allowed across rounds; once any bound alert for this round appears,
                    // the write boundary must publish all four together.
                    if (activeBound.Count is > 0 and < 4)
                    {
                        violations.Add(
                            $"half alert set for {round}: [{string.Join(",", activeBound.OrderBy(x => x))}]");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    break;
                }
            }
        })).ToArray();

        RunStress(ready, stop, readers, writer);

        Assert.True(
            failures.IsEmpty,
            "Expected no concurrent failures, got: " + string.Join(" | ", failures.Select(Describe)));
        Assert.True(
            violations.IsEmpty,
            "Write-boundary violations: " + string.Join(" | ", violations.Take(8)));
    }

    private static HashSet<string> CollectCreatedDemandIds(
        InMemoryTransportDemandStore store,
        DateTimeOffset asOf)
    {
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        long after = 0;
        while (true)
        {
            var page = store.QueryChangeFeed(new DemandChangeFeedQuery
            {
                AfterSequence = after,
                Limit = 100,
                AsOf = asOf,
            });
            foreach (var entry in page.Items)
            {
                if (entry.ChangeType == DemandChangeType.Created)
                {
                    createdIds.Add(entry.DemandId);
                }
            }

            if (!page.HasMore || page.NextAfterSequence is null)
            {
                break;
            }

            after = page.NextAfterSequence.Value;
        }

        return createdIds;
    }

    private static void RunStress(
        ManualResetEventSlim ready,
        ManualResetEventSlim stop,
        Task[] readers,
        Task writer)
    {
        ready.Set();
        // Tight stress window: enough iterations for List version checks to trip.
        Thread.Sleep(750);
        stop.Set();
        Task.WaitAll(readers.Append(writer).ToArray());
    }

    private static void CheckDemandEnvelope(
        DemandListPage page,
        int limit,
        ConcurrentBag<string> violations)
    {
        if (page.Items is null)
        {
            violations.Add("DemandListPage.Items is null");
            return;
        }

        if (page.Items.Count > limit)
        {
            violations.Add($"DemandListPage.Items.Count {page.Items.Count} > limit {limit}");
        }

        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
        {
            violations.Add("DemandListPage.HasMore without NextCursor");
        }

        if (!page.HasMore && page.NextCursor is not null)
        {
            violations.Add("DemandListPage.NextCursor set while HasMore is false");
        }
    }

    private static void CheckAlertEnvelope(
        AlertListPage page,
        int limit,
        ConcurrentBag<string> violations)
    {
        if (page.Items is null)
        {
            violations.Add("AlertListPage.Items is null");
            return;
        }

        if (page.Items.Count > limit)
        {
            violations.Add($"AlertListPage.Items.Count {page.Items.Count} > limit {limit}");
        }

        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
        {
            violations.Add("AlertListPage.HasMore without NextCursor");
        }

        if (!page.HasMore && page.NextCursor is not null)
        {
            violations.Add("AlertListPage.NextCursor set while HasMore is false");
        }
    }

    private static void CheckChangeFeedEnvelope(
        DemandChangeFeedPage page,
        int limit,
        ConcurrentBag<string> violations)
    {
        if (page.Items is null)
        {
            violations.Add("DemandChangeFeedPage.Items is null");
            return;
        }

        if (page.Items.Count > limit)
        {
            violations.Add($"DemandChangeFeedPage.Items.Count {page.Items.Count} > limit {limit}");
        }

        if (page.HasMore && page.NextAfterSequence is null)
        {
            violations.Add("DemandChangeFeedPage.HasMore without NextAfterSequence");
        }

        if (!page.HasMore && page.NextAfterSequence is not null)
        {
            violations.Add("DemandChangeFeedPage.NextAfterSequence set while HasMore is false");
        }
    }

    private static void Seed(InMemoryTransportDemandStore store, DateTimeOffset now, int demandCount)
    {
        var demands = Enumerable.Range(0, demandCount)
            .Select(n => Demand($"d{n}", now, "T", $"S{n}"))
            .ToList();
        var alerts = Enumerable.Range(0, Math.Min(4, demandCount))
            .Select(n => new IngestAlert(
                Code: "POLL_FAILURE",
                TaskType: "T",
                Sublot: $"S{n}",
                DemandId: $"d{n}",
                Message: "seed",
                CreatedAt: now))
            .ToList();
        store.ReplaceState(new ProjectionState(demands), alerts);
    }

    private static TransportDemand Demand(
        string id,
        DateTimeOffset dates,
        string taskType,
        string sublot) =>
        new()
        {
            DemandId = id,
            TaskType = taskType,
            Sublot = sublot,
            Dates = dates,
            Status = DemandStatus.Visible,
            MesLastSeenAt = dates,
            CreatedAt = dates,
        };

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message} @ {FirstInterestingFrame(ex)}";

    private static string FirstInterestingFrame(Exception ex)
    {
        var lines = (ex.StackTrace ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("MesIngest.", StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return lines.FirstOrDefault()?.Trim() ?? "(no stack)";
    }
}
