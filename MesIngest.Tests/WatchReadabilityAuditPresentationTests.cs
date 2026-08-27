using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchReadabilityAuditPresentationTests
{
    [Fact]
    public void English_catalog_and_presenter_keep_six_distinct_value_semantics_and_raw_blocker_codes()
    {
        var catalog = WatchTextCatalog.For(WatchDisplayLanguage.English);
        var text = catalog.ReadabilityAudit;
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 1,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected);

        var presentation = WatchReadabilityAuditPresentation.Project(
            workspace,
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas,
            catalog);
        var blocker = text.DescribeBlocker("REQUIRED_MES_FIELD_MISSING");

        Assert.Equal("Eligibility audit", text.PageTitle);
        Assert.Equal(
            [
                WatchDisplayValueKind.SourceNotProvided,
                WatchDisplayValueKind.SystemUnknown,
                WatchDisplayValueKind.NotApplicable,
                WatchDisplayValueKind.NotLoaded,
                WatchDisplayValueKind.EmptyResult,
                WatchDisplayValueKind.ReadFailed,
            ],
            text.ValueSemantics.Select(item => item.Kind).ToArray());
        Assert.Equal(6, text.ValueSemantics.Select(item => item.Heading).Distinct().Count());
        Assert.Equal("Required MES field is missing", blocker.Description);
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", blocker.RawCode);
        Assert.Equal("No Host eligibility-audit snapshot", presentation.SnapshotFacts);
        Assert.Equal("No eligibility-audit snapshot", presentation.PageSummary);
        Assert.Equal("Host committed scope: no snapshot", presentation.HostAreaScope);
    }

    [Fact]
    public void No_snapshot_reports_an_unavailable_audit_without_claiming_health()
    {
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 1,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            ReadabilityAudit = WatchV2ViewState<
                ReadabilityAuditListSnapshot,
                ReadabilityAuditDetailSnapshot>.Empty(1) with
            {
                IsRefreshing = true,
                PendingQueryKey = "initial-audit-query",
            },
        };
        var presentation = WatchReadabilityAuditPresentation.Project(
            workspace,
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);

        Assert.False(presentation.HasSnapshot);
        Assert.True(presentation.IsRefreshing);
        Assert.True(presentation.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Informational, presentation.InfoSeverity);
        Assert.Equal("正在读取资格审计", presentation.InfoTitle);
        Assert.Contains("等待 Host 返回冻结快照", presentation.InfoMessage, StringComparison.Ordinal);
        Assert.Equal("尚无 Host 资格审计快照", presentation.SnapshotFacts);
        Assert.Equal("尚无资格审计快照", presentation.PageSummary);
        Assert.Empty(presentation.EmptyResultMessage);
        Assert.Equal("Host 已提交范围：尚无快照", presentation.HostAreaScope);
        Assert.Empty(presentation.StateFacets);
        Assert.Empty(presentation.BlockerFacets);
        Assert.Empty(presentation.Rows);
        Assert.Null(presentation.Detail);
        Assert.False(presentation.CanGoPrevious);
        Assert.False(presentation.CanGoNext);
    }

    [Fact]
    public void Loaded_page_preserves_host_exact_facets_orthogonal_lifecycle_and_lead_vs_all_blockers()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var filter = new ReadabilityAuditFilter
        {
            ReadabilityStates = [ExternalReadabilityStates.NotReadable],
            WorkTypes = ["WIRE_TO_GATE"],
            Blockers = ["DUPLICATE_TRANSPORT_DEMAND_KEY"],
            DemandId = "demand-target",
            SublotContains = "SL-",
            MesAreas = ["A1-1"],
        };
        var snapshot = new ReadabilityAuditListSnapshot(
            Identity(at),
            "snapshot-audit-21",
            filter,
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 3,
            new ReadabilityAuditFacets(
                [
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.Readable, 1),
                    new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 2),
                ],
                [
                    new ReadabilityBlockerFacetSnapshot("DUPLICATE_TRANSPORT_DEMAND_KEY", 2),
                    new ReadabilityBlockerFacetSnapshot("REQUIRED_MES_FIELD_MISSING", 1),
                ]),
            PageSize: 2,
            PageNumber: 2,
            TotalPages: 2,
            [
                Item(
                    "demand-target",
                    demandStatus: "VISIBLE",
                    seriesPresence: "VISIBLE",
                    ExternalReadabilityStates.NotReadable,
                    leadBlocker: "DUPLICATE_TRANSPORT_DEMAND_KEY",
                    blockers:
                    [
                        "DUPLICATE_TRANSPORT_DEMAND_KEY",
                        "REQUIRED_MES_FIELD_MISSING",
                    ],
                    at),
                Item(
                    "demand-readable-gone",
                    demandStatus: "GONE",
                    seriesPresence: "GONE",
                    ExternalReadabilityStates.Readable,
                    leadBlocker: null,
                    blockers: [],
                    at),
            ],
            NextCursor: null,
            HasMore: false);
        var state = Workspace(snapshot, at);

        var presentation = WatchReadabilityAuditPresentation.Project(
            state,
            new ReadabilityAuditQuery(
                filter,
                PageSize: 2,
                PageNumber: 2,
                SnapshotReference: snapshot.SnapshotReference),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at));

        Assert.Contains("commit-audit-21", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("序列 321", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("PollTrace poll-audit-21", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("CatalogRevision 9", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Equal("精确 3 个 Demand 世代 · 第 2 / 2 页", presentation.PageSummary);
        Assert.Empty(presentation.EmptyResultMessage);
        Assert.Equal($"Host 固定排序：{ReadabilityAuditOrder.Default}", presentation.OrderSummary);
        Assert.Contains("资格 NOT_READABLE", presentation.HostFilterSummary, StringComparison.Ordinal);
        Assert.Contains("WorkType WIRE_TO_GATE", presentation.HostFilterSummary, StringComparison.Ordinal);
        Assert.Contains("阻断 DUPLICATE_TRANSPORT_DEMAND_KEY", presentation.HostFilterSummary, StringComparison.Ordinal);
        Assert.Contains("DemandId demand-target", presentation.HostFilterSummary, StringComparison.Ordinal);
        Assert.Contains("SUBLOT 包含 SL-", presentation.HostFilterSummary, StringComparison.Ordinal);
        Assert.Equal("Host 已提交范围：A1-1", presentation.HostAreaScope);
        Assert.False(presentation.IsAreaScopeDifferent);
        Assert.True(presentation.CanGoPrevious);
        Assert.False(presentation.CanGoNext);

        Assert.Equal(
            [(ExternalReadabilityStates.Readable, 1L), (ExternalReadabilityStates.NotReadable, 2L)],
            presentation.StateFacets.Select(facet => (facet.State, facet.DemandCount)).ToArray());
        Assert.Equal(
            [("DUPLICATE_TRANSPORT_DEMAND_KEY", 2L), ("REQUIRED_MES_FIELD_MISSING", 1L)],
            presentation.BlockerFacets.Select(facet => (facet.Code, facet.DemandCount)).ToArray());

        var blocked = presentation.Rows[0];
        Assert.Equal(ExternalReadabilityStates.NotReadable, blocked.ExternalReadabilityState);
        Assert.Equal("VISIBLE", blocked.DemandStatus);
        Assert.Equal("TRACKING", blocked.SeriesLifecycle);
        Assert.Equal("VISIBLE", blocked.SeriesCurrentPresence);
        Assert.Equal("Demand VISIBLE · Series TRACKING · VISIBLE", blocked.LifecycleSummary);
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", blocked.LeadReadabilityBlocker);
        Assert.Equal(
            ["DUPLICATE_TRANSPORT_DEMAND_KEY", "REQUIRED_MES_FIELD_MISSING"],
            blocked.ReadabilityBlockers);
        Assert.Equal(
            "DUPLICATE_TRANSPORT_DEMAND_KEY、REQUIRED_MES_FIELD_MISSING",
            blocked.AllBlockersSummary);
        Assert.Equal("A1-1", blocked.MesArea);

        var readableGone = presentation.Rows[1];
        Assert.Equal(ExternalReadabilityStates.Readable, readableGone.ExternalReadabilityState);
        Assert.Equal("GONE", readableGone.DemandStatus);
        Assert.Equal("GONE", readableGone.SeriesCurrentPresence);
        Assert.Equal("—", readableGone.LeadReadabilityBlocker);
        Assert.Equal("无", readableGone.AllBlockersSummary);
    }

    [Fact]
    public void Detail_keeps_all_checks_flattened_blocker_evidence_raw_conflicts_and_snapshot_provenance()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var demand = Item(
            "demand-conflict",
            demandStatus: "VISIBLE",
            seriesPresence: "VISIBLE",
            ExternalReadabilityStates.NotReadable,
            leadBlocker: "DUPLICATE_TRANSPORT_DEMAND_KEY",
            blockers:
            [
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "REQUIRED_MES_FIELD_MISSING",
            ],
            at) with
        {
            LiveMesFields = null,
            CurrentRawObservationCount = 2,
        };
        var snapshot = new ReadabilityAuditListSnapshot(
            Identity(at),
            "snapshot-audit-21",
            new ReadabilityAuditFilter(),
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 1,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 1)],
                [
                    new ReadabilityBlockerFacetSnapshot("DUPLICATE_TRANSPORT_DEMAND_KEY", 1),
                    new ReadabilityBlockerFacetSnapshot("REQUIRED_MES_FIELD_MISSING", 1),
                ]),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            [demand],
            NextCursor: null,
            HasMore: false);
        var detail = new ReadabilityAuditDetailSnapshot(
            snapshot.Snapshot,
            snapshot.SnapshotReference,
            demand,
            new ReadabilityAuditSeriesSnapshot(
                demand.SeriesId,
                demand.WorkType,
                demand.Sublot,
                Lifecycle: "TRACKING",
                CurrentPresence: "VISIBLE",
                StartedAt: at.AddHours(-2),
                ArchivedAt: null,
                CurrentDemandId: demand.DemandId),
            [
                new("DEMAND_VISIBLE", "DEMAND_GONE", ReadabilityQualificationCheckResults.Passed),
                new("SERIES_TRACKING", "SERIES_ARCHIVED", ReadabilityQualificationCheckResults.Passed),
                new("NOT_LONG_GONE_BUT_VISIBLE", "LONG_GONE_BUT_VISIBLE", ReadabilityQualificationCheckResults.Passed),
                new("UNIQUE_RAW_OBSERVATION", "DUPLICATE_TRANSPORT_DEMAND_KEY", ReadabilityQualificationCheckResults.Failed),
                new("ONE_WORK_TYPE_PER_SUBLOT", "SUBLOT_MULTIPLE_WORK_TYPES", ReadabilityQualificationCheckResults.Passed),
                new("REQUIRED_MES_FIELDS_PRESENT", "REQUIRED_MES_FIELD_MISSING", ReadabilityQualificationCheckResults.NotEvaluated),
                new("MES_FIELD_FORMAT_VALID", "INVALID_MES_FIELD_FORMAT", ReadabilityQualificationCheckResults.NotEvaluated),
            ],
            [
                new ReadabilityBlockerEvidenceSnapshot(
                    "DUPLICATE_TRANSPORT_DEMAND_KEY",
                    Priority: 20,
                    [
                        Evidence("RAW_OBSERVATION_SET", "2 rows", "exactly one row", at, ordinal: 1),
                        Evidence("MES_FIELD", "AREA differs", "one trusted value", at, ordinal: 2),
                    ]),
                new ReadabilityBlockerEvidenceSnapshot(
                    "REQUIRED_MES_FIELD_MISSING",
                    Priority: 40,
                    [Evidence("MES_FIELD", "EQP is blank", "non-whitespace EQP", at, ordinal: 3)]),
            ],
            [
                RawObservation(1, "A1-1", "EQP-A", at),
                RawObservation(2, "B2-2", "EQP-B", at),
            ],
            new ReadabilityAuditPollTraceSnapshot(
                "poll-audit-21",
                "MES_TASK_UNION_V2",
                "SUCCESS",
                StartedAt: at.AddSeconds(-3),
                CompletedAt: at,
                RowCount: 2,
                "digest-audit-21",
                "commit-audit-21",
                ProjectionSequence: 321));

        var presentation = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, detail),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);

        var selected = Assert.IsType<WatchReadabilityAuditDetailPresentation>(presentation.Detail);
        Assert.Equal("demand-conflict · WIRE_TO_GATE", selected.Heading);
        Assert.Equal(
            $"SL-demand-conflict · series-demand-conflict · Demand Generation 2 · 最后看见 {WatchTimeDisplay.Format(at)}",
            selected.BusinessIdentity);
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", selected.LeadReadabilityBlocker);
        Assert.Equal("Blocked", selected.SemanticState);
        Assert.Equal(
            "DUPLICATE_TRANSPORT_DEMAND_KEY",
            selected.PrimaryBlockerEvidence?.Code);
        Assert.Contains("snapshot-audit-21", selected.Facts, StringComparison.Ordinal);
        Assert.Contains("commit-audit-21", selected.Facts, StringComparison.Ordinal);
        Assert.Contains("序列 321", selected.Facts, StringComparison.Ordinal);
        Assert.Contains("PollTrace poll-audit-21", selected.Facts, StringComparison.Ordinal);
        Assert.Contains("CatalogRevision 9", selected.Facts, StringComparison.Ordinal);
        Assert.Contains("series-demand-conflict", selected.SeriesFacts, StringComparison.Ordinal);
        Assert.Contains("TRACKING · VISIBLE", selected.SeriesFacts, StringComparison.Ordinal);
        Assert.Null(selected.LiveMesFields);
        Assert.Contains("无可信 LiveMesFieldSet", selected.LiveMesFacts, StringComparison.Ordinal);
        Assert.Contains("2 条原始观测", selected.ObservationSummary, StringComparison.Ordinal);
        Assert.Contains("冲突证据", selected.ObservationSummary, StringComparison.Ordinal);

        Assert.Equal(
            [
                "DEMAND_VISIBLE",
                "SERIES_TRACKING",
                "NOT_LONG_GONE_BUT_VISIBLE",
                "UNIQUE_RAW_OBSERVATION",
                "ONE_WORK_TYPE_PER_SUBLOT",
                "REQUIRED_MES_FIELDS_PRESENT",
                "MES_FIELD_FORMAT_VALID",
            ],
            selected.QualificationChecks.Select(check => check.Code).ToArray());
        Assert.Equal(
            [
                ReadabilityQualificationCheckResults.Passed,
                ReadabilityQualificationCheckResults.Passed,
                ReadabilityQualificationCheckResults.Passed,
                ReadabilityQualificationCheckResults.Failed,
                ReadabilityQualificationCheckResults.Passed,
                ReadabilityQualificationCheckResults.NotEvaluated,
                ReadabilityQualificationCheckResults.NotEvaluated,
            ],
            selected.QualificationChecks.Select(check => check.Result).ToArray());

        Assert.Equal(3, selected.BlockerEvidence.Count);
        Assert.Equal(
            "DUPLICATE_TRANSPORT_DEMAND_KEY、REQUIRED_MES_FIELD_MISSING",
            selected.AllBlockersSummary);
        Assert.Equal(
            [
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "REQUIRED_MES_FIELD_MISSING",
            ],
            selected.BlockerEvidence.Select(evidence => evidence.Code).ToArray());
        Assert.Equal([1, 2, 1], selected.BlockerEvidence.Select(evidence => evidence.EvidenceOrdinal).ToArray());
        Assert.All(selected.BlockerEvidence, evidence =>
        {
            Assert.Equal("poll-evidence-21", evidence.PollTraceId);
            Assert.Equal("commit-evidence-21", evidence.ProjectionCommitId);
        });

        Assert.Equal(["A1-1", "B2-2"], selected.RawObservations.Select(row => row.Area).ToArray());
        Assert.Equal(["EQP-A", "EQP-B"], selected.RawObservations.Select(row => row.Eqp).ToArray());
        Assert.All(selected.RawObservations, row =>
        {
            Assert.Equal("poll-raw-21", row.PollTraceId);
            Assert.Equal("commit-raw-21", row.ProjectionCommitId);
        });
        Assert.Contains("MES_TASK_UNION_V2 · SUCCESS", selected.PollTraceFacts, StringComparison.Ordinal);
        Assert.Contains("2 行", selected.PollTraceFacts, StringComparison.Ordinal);
        Assert.Contains("digest-audit-21", selected.PollTraceFacts, StringComparison.Ordinal);
        Assert.Contains("commit-audit-21 · 序列 321", selected.PollTraceFacts, StringComparison.Ordinal);
        Assert.Equal(snapshot.SnapshotReference, selected.SnapshotReference);
        Assert.Equal(snapshot.Snapshot.ProjectionCommitId, selected.ProjectionCommitId);
        Assert.Equal(snapshot.Snapshot.ProjectionSequence, selected.ProjectionSequence);
        Assert.Equal(snapshot.Snapshot.PollTraceId, selected.PollTraceId);
        Assert.Equal(snapshot.Snapshot.CatalogRevision, selected.CatalogRevision);

        var english = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, detail),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas,
            WatchTextCatalog.For(WatchDisplayLanguage.English));
        var englishDetail = Assert.IsType<WatchReadabilityAuditDetailPresentation>(english.Detail);
        Assert.Contains("Last seen", englishDetail.BusinessIdentity, StringComparison.Ordinal);
        Assert.Contains("Audit snapshot", englishDetail.Facts, StringComparison.Ordinal);
        Assert.Contains("No trusted LiveMesFieldSet", englishDetail.LiveMesFacts, StringComparison.Ordinal);
        Assert.Contains("raw observations", englishDetail.ObservationSummary, StringComparison.Ordinal);
        Assert.Contains("rows", englishDetail.PollTraceFacts, StringComparison.Ordinal);
        Assert.DoesNotContain('最', englishDetail.BusinessIdentity);
        Assert.DoesNotContain('审', englishDetail.Facts);

        var withoutEvidence = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, detail with { Blockers = [] }),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);
        var selectedWithoutEvidence = Assert.IsType<WatchReadabilityAuditDetailPresentation>(
            withoutEvidence.Detail);
        Assert.Equal(ExternalReadabilityStates.NotReadable, selectedWithoutEvidence.ExternalReadabilityState);
        Assert.Equal("DUPLICATE_TRANSPORT_DEMAND_KEY", selectedWithoutEvidence.LeadReadabilityBlocker);
        Assert.Equal("Blocked", selectedWithoutEvidence.SemanticState);
        Assert.Null(selectedWithoutEvidence.PrimaryBlockerEvidence);
    }

    [Fact]
    public void Trusted_detail_projects_live_mes_fields_and_rejects_a_mixed_snapshot_identity()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var demand = Item(
            "demand-trusted",
            demandStatus: "VISIBLE",
            seriesPresence: "VISIBLE",
            ExternalReadabilityStates.Readable,
            leadBlocker: null,
            blockers: [],
            at);
        var snapshot = new ReadabilityAuditListSnapshot(
            Identity(at),
            "snapshot-trusted",
            new ReadabilityAuditFilter(),
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 1,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.Readable, 1)],
                []),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            [demand],
            NextCursor: null,
            HasMore: false);
        var detail = new ReadabilityAuditDetailSnapshot(
            snapshot.Snapshot,
            snapshot.SnapshotReference,
            demand,
            new ReadabilityAuditSeriesSnapshot(
                demand.SeriesId,
                demand.WorkType,
                demand.Sublot,
                "TRACKING",
                "VISIBLE",
                at.AddHours(-1),
                ArchivedAt: null,
                demand.DemandId),
            [new ReadabilityQualificationCheckSnapshot(
                "DEMAND_VISIBLE",
                "DEMAND_GONE",
                ReadabilityQualificationCheckResults.Passed)],
            Blockers: [],
            [RawObservation(1, "A1-1", "EQP-21", at) with
            {
                SeriesId = demand.SeriesId,
                DemandId = demand.DemandId,
                Sublot = demand.Sublot,
            }],
            new ReadabilityAuditPollTraceSnapshot(
                "poll-audit-21",
                "MES_TASK_UNION_V2",
                "SUCCESS",
                at.AddSeconds(-1),
                at,
                RowCount: 1,
                "digest-trusted",
                "commit-audit-21",
                ProjectionSequence: 321));

        var presentation = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, detail),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);

        var selected = Assert.IsType<WatchReadabilityAuditDetailPresentation>(presentation.Detail);
        var fields = Assert.IsType<WatchReadabilityLiveMesFieldSetPresentation>(selected.LiveMesFields);
        Assert.Equal("A1-1", fields.Area);
        Assert.Equal("EQP-21", fields.Eqp);
        Assert.Equal("STEP-21", fields.Step);
        Assert.Equal(WatchTimeDisplay.Format(at.AddDays(-1)), fields.MesSourceDate);
        Assert.Equal("PKG-21", fields.Package);
        Assert.Contains("可信 LiveMesFieldSet", selected.LiveMesFacts, StringComparison.Ordinal);
        Assert.Contains("当前可信 LiveMesFieldSet 可用", selected.ObservationSummary, StringComparison.Ordinal);

        var mixedDetail = detail with
        {
            Snapshot = detail.Snapshot with
            {
                ProjectionCommitId = "commit-from-another-snapshot",
            },
        };
        var mixed = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, mixedDetail),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);

        Assert.Null(mixed.Detail);

        var mixedCatalogRevision = detail with
        {
            Snapshot = detail.Snapshot with
            {
                CatalogRevision = detail.Snapshot.CatalogRevision + 1,
            },
        };
        var catalogMixed = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at, mixedCatalogRevision),
            new ReadabilityAuditQuery(new ReadabilityAuditFilter()),
            WatchAreaDisplayContext.AllAreas);

        Assert.Null(catalogMixed.Detail);
    }

    [Fact]
    public void Refresh_failure_retains_old_host_snapshot_while_current_query_and_selection_notice_stay_distinct()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var retainedFilter = new ReadabilityAuditFilter
        {
            ReadabilityStates = [ExternalReadabilityStates.NotReadable],
            MesAreas = ["A1-1"],
        };
        var retained = new ReadabilityAuditListSnapshot(
            Identity(at),
            "snapshot-retained-a1",
            retainedFilter,
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 1,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 1)],
                [new ReadabilityBlockerFacetSnapshot("DEMAND_GONE", 1)]),
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 1,
            [Item(
                "demand-retained",
                "GONE",
                "GONE",
                ExternalReadabilityStates.NotReadable,
                "DEMAND_GONE",
                ["DEMAND_GONE"],
                at)],
            NextCursor: null,
            HasMore: false);
        var successful = Workspace(retained, at).ReadabilityAudit;
        var query = new ReadabilityAuditQuery(new ReadabilityAuditFilter
        {
            ReadabilityStates = [ExternalReadabilityStates.Readable],
            MesAreas = ["B2-2"],
        });
        var local = new WatchAreaDisplayContext("本机 B2 班次", ["B2-2"], "本机已选择", at);

        var refreshing = WatchReadabilityAuditPresentation.Project(
            WorkspaceWithView(successful with
            {
                RequestGeneration = 2,
                IsRefreshing = true,
                PendingQueryKey = "audit-area-b2",
            }),
            query,
            local);

        Assert.True(refreshing.HasSnapshot);
        Assert.True(refreshing.IsRefreshing);
        Assert.True(refreshing.IsStale);
        Assert.True(refreshing.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Informational, refreshing.InfoSeverity);
        Assert.Equal("正在刷新资格审计", refreshing.InfoTitle);
        Assert.Contains("继续显示 Host 快照", refreshing.InfoMessage, StringComparison.Ordinal);
        Assert.Equal("Host 已提交范围：A1-1", refreshing.HostAreaScope);
        Assert.Contains("资格 NOT_READABLE", refreshing.HostFilterSummary, StringComparison.Ordinal);
        Assert.Contains("资格 READABLE", refreshing.CurrentQuerySummary, StringComparison.Ordinal);
        Assert.Contains("AREA B2-2", refreshing.CurrentQuerySummary, StringComparison.Ordinal);
        Assert.True(refreshing.IsAreaScopeDifferent);
        Assert.Equal("demand-retained", Assert.Single(refreshing.Rows).DemandId);

        var failedAt = at.AddMinutes(2);
        var failed = WatchReadabilityAuditPresentation.Project(
            WorkspaceWithView(successful with
            {
                RequestGeneration = 2,
                PendingQueryKey = "audit-area-b2",
                LastFailureAt = failedAt,
                FailedQueryKey = "audit-area-b2",
                FailureKind = WatchHostFailureKind.Timeout,
                FailureCode = "READABILITY_TIMEOUT",
                ErrorMessage = "Host readability request timed out.",
                CorrelationId = "correlation-ticket-21",
            }),
            query,
            local);

        Assert.False(failed.IsRefreshing);
        Assert.True(failed.IsStale);
        Assert.True(failed.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, failed.InfoSeverity);
        Assert.Contains("已保留上次快照", failed.InfoTitle, StringComparison.Ordinal);
        Assert.Contains(WatchTimeDisplay.Format(failedAt), failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("timed out", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("correlation-ticket-21", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("最近失败", failed.ClientAttemptFacts, StringComparison.Ordinal);
        Assert.Equal("snapshot-retained-a1", retained.SnapshotReference);
        Assert.Equal("Host 已提交范围：A1-1", failed.HostAreaScope);
        Assert.Equal("demand-retained", Assert.Single(failed.Rows).DemandId);

        var selectionLost = WatchReadabilityAuditPresentation.Project(
            WorkspaceWithView(successful with
            {
                SelectionNotice = WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            }),
            new ReadabilityAuditQuery(retained.Filter),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at));

        Assert.True(selectionLost.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, selectionLost.InfoSeverity);
        Assert.Equal("原选择已不在刷新结果中", selectionLost.InfoTitle);
        Assert.Contains("已清除详情", selectionLost.InfoMessage, StringComparison.Ordinal);
        Assert.Equal(
            WatchV2SelectionNotices.NoLongerMatchesRefreshedSnapshot,
            selectionLost.SelectionNotice);
        Assert.Null(selectionLost.Detail);
    }

    [Fact]
    public void Successful_zero_result_only_reports_zero_matches_for_the_committed_conditions()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var filter = new ReadabilityAuditFilter
        {
            ReadabilityStates = [ExternalReadabilityStates.NotReadable],
            Blockers = ["DEMAND_GONE"],
            MesAreas = ["A1-1"],
        };
        var snapshot = new ReadabilityAuditListSnapshot(
            Identity(at),
            "snapshot-zero",
            filter,
            ReadabilityAuditOrder.Default,
            ExactTotalDemandCount: 0,
            new ReadabilityAuditFacets(
                [new ReadabilityStateFacetSnapshot(ExternalReadabilityStates.NotReadable, 0)],
                [new ReadabilityBlockerFacetSnapshot("DEMAND_GONE", 0)]),
            PageSize: ReadabilityAuditQuery.DefaultPageSize,
            PageNumber: 1,
            TotalPages: 0,
            Items: [],
            NextCursor: null,
            HasMore: false);

        var presentation = WatchReadabilityAuditPresentation.Project(
            Workspace(snapshot, at),
            new ReadabilityAuditQuery(filter),
            new WatchAreaDisplayContext("封装车间", ["A1-1"], "本机已应用", at));

        Assert.Equal("精确 0 个 Demand 世代 · 第 0 / 0 页", presentation.PageSummary);
        Assert.Equal(
            "查询成功；Host 在当前已提交条件下精确 0 个 Demand 世代命中。",
            presentation.EmptyResultMessage);
        Assert.DoesNotContain("健康", presentation.EmptyResultMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("无异常", presentation.EmptyResultMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("全部可读", presentation.EmptyResultMessage, StringComparison.Ordinal);
        Assert.Empty(presentation.Rows);
        Assert.Equal(0, Assert.Single(presentation.StateFacets).DemandCount);
        Assert.Equal(0, Assert.Single(presentation.BlockerFacets).DemandCount);
        Assert.False(presentation.CanGoPrevious);
        Assert.False(presentation.CanGoNext);
    }

    private static WatchV2WorkspaceState Workspace(
        ReadabilityAuditListSnapshot snapshot,
        DateTimeOffset lastSuccessfulAt,
        ReadabilityAuditDetailSnapshot? detail = null) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: 3,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            ReadabilityAudit = WatchV2ViewState<
                ReadabilityAuditListSnapshot,
                ReadabilityAuditDetailSnapshot>.Empty(3) with
            {
                PendingQueryKey = "audit-query",
                CommittedQueryKey = "audit-query",
                Snapshot = snapshot,
                Detail = detail,
                LastSuccessfulAt = lastSuccessfulAt,
            },
        };

    private static WatchV2WorkspaceState WorkspaceWithView(
        WatchV2ViewState<ReadabilityAuditListSnapshot, ReadabilityAuditDetailSnapshot> view) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: view.HostGeneration,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            ReadabilityAudit = view,
        };

    private static ReadabilityAuditSnapshotIdentity Identity(DateTimeOffset at) => new(
        HistoryEpoch.CreateNew(),
        "commit-audit-21",
        ProjectionSequence: 321,
        at,
        "poll-audit-21",
        CatalogRevision: 9);

    private static ReadabilityAuditListItemSnapshot Item(
        string demandId,
        string demandStatus,
        string seriesPresence,
        string readability,
        string? leadBlocker,
        IReadOnlyList<string> blockers,
        DateTimeOffset at) => new(
        demandId,
        $"series-{demandId}",
        "WIRE_TO_GATE",
        $"SL-{demandId}",
        Generation: 2,
        PredecessorDemandId: $"predecessor-{demandId}",
        demandStatus,
        SeriesLifecycle: "TRACKING",
        SeriesCurrentPresence: seriesPresence,
        IsCurrentGeneration: true,
        DemandCreatedAt: at.AddHours(-1),
        DemandLastSeenAt: at,
        GoneConfirmedAt: demandStatus == "GONE" ? at.AddMinutes(1) : null,
        new LiveMesFieldSetSnapshot(
            "A1-1",
            "EQP-21",
            "STEP-21",
            at.AddDays(-1),
            "PKG-21"),
        CurrentRawObservationCount: 1,
        readability,
        leadBlocker,
        blockers,
        LatestObservationPollTraceId: "poll-audit-21",
        LatestObservationProjectionCommitId: "commit-audit-21",
        LatestObservationAt: at);

    private static ReadabilityEvidenceItemSnapshot Evidence(
        string subjectKind,
        string observedValue,
        string expectedRule,
        DateTimeOffset at,
        int ordinal) => new(
        subjectKind,
        observedValue,
        expectedRule,
        at.AddMilliseconds(ordinal),
        "poll-evidence-21",
        "commit-evidence-21");

    private static DemandRawObservationSnapshot RawObservation(
        int ordinal,
        string area,
        string eqp,
        DateTimeOffset at) => new(
        ordinal,
        "poll-raw-21",
        "commit-raw-21",
        MesObservationAssignment.Assigned,
        "series-demand-conflict",
        "demand-conflict",
        "WIRE_TO_GATE",
        "SL-demand-conflict",
        area,
        eqp,
        "STEP-21",
        at.AddDays(-1),
        "PKG-21",
        at,
        "2026-08-13 05:06:07");
}
