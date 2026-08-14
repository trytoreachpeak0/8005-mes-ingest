using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesNavigationContextTests
{
    [Fact]
    public void Readability_audit_drill_preserves_series_demand_area_and_source_projection_fence()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var source = new ReadabilityAuditListSnapshot(
            new ReadabilityAuditSnapshotIdentity(
                "commit-audit-20",
                220,
                at,
                "poll-audit-20",
                CatalogRevision: 7),
            "snapshot-audit-20",
            new ReadabilityAuditFilter { MesAreas = ["A1-1"] },
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 1,
            new ReadabilityAuditFacets([], []),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            Items:
            [
                new ReadabilityAuditListItemSnapshot(
                    "demand-audit-20",
                    "series-audit-20",
                    "WIRE_TO_GATE",
                    "SL-AUDIT-20",
                    Generation: 3,
                    PredecessorDemandId: "demand-audit-19",
                    DemandStatus: "VISIBLE",
                    SeriesLifecycle: "TRACKING",
                    SeriesCurrentPresence: "VISIBLE",
                    IsCurrentGeneration: true,
                    DemandCreatedAt: at.AddHours(-1),
                    DemandLastSeenAt: at,
                    GoneConfirmedAt: null,
                    new LiveMesFieldSetSnapshot("A1-1", "EQP-20", "STEP-20", at.AddDays(-1), "PKG-20"),
                    CurrentRawObservationCount: 1,
                    ExternalReadabilityState: "NOT_READABLE",
                    LeadReadabilityBlocker: "REQUIRED_MES_FIELD_MISSING",
                    ReadabilityBlockers: ["REQUIRED_MES_FIELD_MISSING"],
                    LatestObservationPollTraceId: "poll-audit-20",
                    LatestObservationProjectionCommitId: "commit-audit-20",
                    LatestObservationAt: at)
            ],
            NextCursor: null,
            HasMore: false);

        var context = WatchDemandSeriesNavigationContext.FromReadabilityAudit(
            source,
            "series-audit-20",
            "demand-audit-20");

        Assert.Equal("资格审计", context.SourceName);
        Assert.Equal("series-audit-20", context.SeriesId);
        Assert.Equal("demand-audit-20", context.FocusedDemandId);
        Assert.Equal("commit-audit-20", context.SourceProjectionCommitId);
        Assert.Equal(220, context.SourceProjectionSequence);
        Assert.Equal(at, context.SourceProjectionCommittedAt);
        Assert.Equal(at, context.SourceSnapshotAsOf);
        Assert.Equal(["A1-1"], context.RequestedMesAreas);
        var facts = Assert.IsType<WatchDemandSeriesObjectFacts>(context.SourceFacts);
        Assert.Equal("WIRE_TO_GATE", facts.WorkType);
        Assert.Equal("SL-AUDIT-20", facts.Sublot);
        Assert.Equal(3, facts.Generation);
        Assert.Equal("NOT_READABLE", facts.ExternalReadabilityState);
        Assert.Equal(["REQUIRED_MES_FIELD_MISSING"], facts.ReadabilityBlockers);
    }
}
