using System.Text;
using System.IO;

namespace MesIngest.Watch.UiTests;

public sealed class WatchJourneyEvidenceTests
{
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
}
