using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchDemandSeriesNavigationContextTests
{
    [Fact]
    public void Error_search_drill_preserves_the_matching_frozen_series_and_projection_fence()
    {
        var (list, detail) = CreateErrorSearchSource();

        var context = Assert.IsType<WatchDemandSeriesNavigationContext>(
            WatchDemandSeriesNavigationContext.FromErrorSearch(
                list,
                "series-error-source",
                detail));

        Assert.Equal("错误检索", context.SourceName);
        Assert.Equal("series-error-source", context.SeriesId);
        Assert.Null(context.FocusedDemandId);
        Assert.Equal("commit-error-source", context.SourceProjectionCommitId);
        Assert.Equal(320, context.SourceProjectionSequence);
        Assert.Equal(list.Snapshot.ProjectionCommittedAt, context.SourceProjectionCommittedAt);
        Assert.Equal(list.Snapshot.ErrorSearchAsOf, context.SourceSnapshotAsOf);
        Assert.Empty(context.RequestedMesAreas);
        var facts = Assert.IsType<WatchDemandSeriesObjectFacts>(context.SourceFacts);
        Assert.Equal("series-error-source", facts.SeriesId);
        Assert.Null(facts.DemandId);
        Assert.Equal("WIRE_TO_GATE", facts.WorkType);
        Assert.Equal("SL-ERROR-SOURCE", facts.Sublot);
        Assert.Null(facts.Generation);
        Assert.Null(facts.Lifecycle);
    }

    [Fact]
    public void Error_search_drill_fails_closed_without_a_selected_matching_frozen_detail()
    {
        var (list, detail) = CreateErrorSearchSource();

        Assert.Null(WatchDemandSeriesNavigationContext.FromErrorSearch(
            list,
            selectedId: null,
            detail));
        Assert.Null(WatchDemandSeriesNavigationContext.FromErrorSearch(
            list,
            "series-other",
            detail));
        Assert.Null(WatchDemandSeriesNavigationContext.FromErrorSearch(
            list,
            "series-error-source",
            detail with { SnapshotReference = "snapshot-other" }));
        Assert.Null(WatchDemandSeriesNavigationContext.FromErrorSearch(
            list,
            "series-error-source",
            detail: null));
    }

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

    [Fact]
    public void Current_attention_drill_preserves_series_demand_and_source_projection_fence()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var identity = new OperationalSnapshotIdentity(
            "commit-attention-22",
            ProjectionSequence: 422,
            at,
            "poll-attention-22",
            PollTraceHighWater: 45,
            CatalogRevision: 9,
            at.AddSeconds(1));
        var item = new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.SeriesError,
            CurrentIngestAttentionSeverities.Error,
            at,
            "series-22:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
            SeriesId: "series-22",
            WorkType: "WIRE_TO_GATE",
            ErrorCode: "REQUIRED_MES_FIELD_MISSING",
            Target: "DEMAND:demand-22",
            SubjectKind: "EQP",
            new CurrentIngestAttentionEvidenceSnapshot(
                ProjectionCommitId: "commit-attention-22",
                ProjectionSequence: 422,
                PollTraceId: "poll-attention-22",
                PollTraceSequence: 45,
                SeriesId: "series-22",
                DemandId: "demand-22",
                WorkType: "WIRE_TO_GATE"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.ErrorSearch,
                SeriesId: "series-22"));
        var source = new CurrentIngestAttentionSnapshot(
            identity,
            ExactTotalItemCount: 1,
            new CurrentIngestAttentionFacets([], []),
            CurrentIngestAttentionOrder.Default,
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            Kinds: [],
            Severities: [],
            Items: [item]);
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 1,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            CurrentAttention = WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail>
                .Empty(1) with
            {
                Snapshot = source,
                LastSuccessfulAt = at,
            },
        };
        var selected = Assert.Single(
            WatchCurrentIngestAttentionPresentation.Project(
                workspace,
                new CurrentIngestAttentionQuery()).Rows);

        var context = Assert.IsType<WatchDemandSeriesNavigationContext>(
            WatchDemandSeriesNavigationContext.FromCurrentAttention(source, selected));

        Assert.Equal("当前关注", context.SourceName);
        Assert.Equal("series-22", context.SeriesId);
        Assert.Equal("demand-22", context.FocusedDemandId);
        Assert.Equal("commit-attention-22", context.SourceProjectionCommitId);
        Assert.Equal(422, context.SourceProjectionSequence);
        Assert.Equal(at, context.SourceProjectionCommittedAt);
        Assert.Equal(at.AddSeconds(1), context.SourceSnapshotAsOf);
        Assert.True(context.OpenInspector);
        var facts = Assert.IsType<WatchDemandSeriesObjectFacts>(context.SourceFacts);
        Assert.Equal("WIRE_TO_GATE", facts.WorkType);
        Assert.Equal("demand-22", facts.DemandId);
    }

    private static (ErrorSearchListSnapshot List, ErrorSearchDetailSnapshot Detail)
        CreateErrorSearchSource()
    {
        var asOf = DateTimeOffset.Parse("2026-08-14T06:00:00Z");
        var identity = new ErrorSearchSnapshotIdentity(
            HistoryEpoch.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            asOf,
            "commit-error-source",
            ProjectionSequence: 320,
            asOf.AddSeconds(-2),
            "poll-error-source");
        var filter = new ErrorSearchFilter().Normalize();
        var window = ErrorSearchWindowSelection.Last7Days.Resolve(asOf);
        var item = new ErrorSearchListItemSnapshot(
            "series-error-source",
            "WIRE_TO_GATE",
            "SL-ERROR-SOURCE",
            ErrorSearchActivityStates.Active,
            [new ErrorSearchMatchedErrorSnapshot(
                "INVALID_MES_FIELD_FORMAT",
                "DATA_FORMAT",
                "ERROR")],
            asOf.AddMinutes(-1),
            MatchedPeriodCount: 1,
            MatchedDemandGenerationCount: 2,
            MesArea: "A1-1",
            ErrorSearchMesAreaAvailability.CurrentTrusted);
        var list = new ErrorSearchListSnapshot(
            "snapshot-error-source",
            identity,
            filter,
            window,
            ErrorSearchOrder.Default,
            TotalSeriesCount: 1,
            new ErrorSearchFacets([], []),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            Items: [item],
            NextCursor: null,
            HasMore: false);
        var detail = new ErrorSearchDetailSnapshot(
            list.SnapshotReference,
            identity,
            filter,
            window,
            ErrorSearchOrder.Default,
            item,
            Periods: []);
        return (list, detail);
    }
}
