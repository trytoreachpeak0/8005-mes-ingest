using System.Text;
using System.IO;

namespace MesIngest.Watch.UiTests;

public sealed class WatchJourneyEvidenceTests
{
    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public void Production_timeline_formatter_keeps_session_and_endpoint_shapes_only()
    {
        var timeline = CreateSensitiveProductionTimeline();

        var text = string.Join(
            Environment.NewLine,
            WatchWorkspaceProductionJourneyTests.FormatTimeline(timeline)
                .Append(WatchWorkspaceProductionJourneyTests.FormatTimelineSummary(timeline)));

        Assert.Contains("session=production-preview-19-22", text, StringComparison.Ordinal);
        Assert.Contains(
            "endpoint=/api/v2/demand-series/{seriesId}{?redacted-query}",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "endpoint=/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations{?redacted-query}",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "endpoint=/api/v2/readability-audit/{demandId}{?redacted-query}",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTE-SERIES-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTE-DEMAND-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTE-EVIDENCE-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SNAPSHOT-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CURSOR-SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QUERY-DEMAND-SECRET", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public void Production_timeline_evidence_does_not_persist_route_or_query_values()
    {
        var root = Path.Combine(Path.GetTempPath(), $"watch-evidence-{Guid.NewGuid():N}");
        try
        {
            var timeline = CreateSensitiveProductionTimeline();
            var evidence = new WatchJourneyEvidence(root, "production-preview", []);
            evidence.RecordFakeHostTimeline(
                WatchWorkspaceProductionJourneyTests.FormatTimeline(timeline),
                WatchWorkspaceProductionJourneyTests.FormatTimelineSummary(timeline));

            var persisted = string.Join(
                Environment.NewLine,
                File.ReadAllText(Path.Combine(evidence.DirectoryPath, "fake-host-timeline.txt")),
                File.ReadAllText(Path.Combine(evidence.DirectoryPath, "fake-host-summary.txt")));

            Assert.Contains("session=production-preview-19-22", persisted, StringComparison.Ordinal);
            Assert.Contains("{seriesId}", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("ROUTE-SERIES-SECRET", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("ROUTE-DEMAND-SECRET", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("ROUTE-EVIDENCE-SECRET", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("SNAPSHOT-SECRET", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("CURSOR-SECRET", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("QUERY-DEMAND-SECRET", persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "watch-vm-tests")]
    public void Failed_journey_evidence_is_complete_and_redacted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"watch-evidence-{Guid.NewGuid():N}");
        var secret = "fake-secret-value";
        var cursor = "fake-cursor-value";
        var demandId = "fake-demand-id";
        try
        {
            var evidence = new WatchJourneyEvidence(
                root,
                "slow-request-cancel",
                [secret, cursor, demandId]);

            evidence.RecordStep("request-started", Encoding.UTF8.GetBytes("fake screenshot"));
            evidence.RecordUiaTree($"Button secret={secret}");
            evidence.RecordProcessOutput($"stdout cursor={cursor}", $"stderr demand={demandId}");
            evidence.RecordFakeHostTimeline(
                [$"GET /api/demands?cursor={cursor}&demandId={demandId}"],
                $"credential={secret}");
            evidence.RecordEnvironment("100% DPI; zh-CN; light; 1440x900");
            evidence.RecordFailure(
                "cancel-slow-request",
                new TimeoutException($"credential={secret}"),
                TimeSpan.FromSeconds(5));

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Contains("environment.txt", files);
            Assert.Contains("failure.txt", files);
            Assert.Contains("fake-host-timeline.txt", files);
            Assert.Contains("fake-host-summary.txt", files);
            Assert.Contains("stderr.txt", files);
            Assert.Contains("stdout.txt", files);
            Assert.Contains("uia-tree.txt", files);
            Assert.Contains("watch-logs.txt", files);
            Assert.Contains("request-started.png", files);

            foreach (var path in Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path);
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                Assert.DoesNotContain(cursor, text, StringComparison.Ordinal);
                Assert.DoesNotContain(demandId, text, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static FakeHostRequestEvent[] CreateSensitiveProductionTimeline() =>
    [
        new(
            1,
            new FakeHostRequestMatch(
                "production-preview-19-22",
                FakeHostOperation.DemandSeriesDetailV2,
                FakeHostRequestState.Started),
            "/api/v2/demand-series/ROUTE-SERIES-SECRET"
            + "?snapshotReference=SNAPSHOT-SECRET&cursor=CURSOR-SECRET"
            + "&demandId=QUERY-DEMAND-SECRET"),
        new(
            2,
            new FakeHostRequestMatch(
                "production-preview-19-22",
                FakeHostOperation.ReadabilityAuditDetailV2,
                FakeHostRequestState.Completed),
            "/api/v2/readability-audit/ROUTE-DEMAND-SECRET"
            + "?snapshotReference=SNAPSHOT-SECRET&cursor=CURSOR-SECRET"),
        new(
            3,
            new FakeHostRequestMatch(
                "production-preview-19-22",
                FakeHostOperation.ErrorSearchRawEvidenceV2,
                FakeHostRequestState.Completed),
            "/api/v2/error-search/ROUTE-SERIES-SECRET/evidence/ROUTE-EVIDENCE-SECRET"
            + "/raw-observations?snapshotReference=SNAPSHOT-SECRET"
            + "&cursor=CURSOR-SECRET&demandId=QUERY-DEMAND-SECRET"),
    ];
}
