using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;
using System.Text.Json;

namespace MesIngest.Tests;

public sealed class WatchErrorSearchPresentationTests
{
    private static readonly DateTimeOffset AsOf =
        DateTimeOffset.Parse("2026-08-14T05:06:07Z");

    [Fact]
    public void Loaded_page_preserves_host_total_facets_order_and_frozen_query_facts()
    {
        var filter = FullFilter();
        var snapshot = Snapshot(
            filter,
            total: 37,
            pageNumber: 2,
            totalPages: 4,
            items:
            [
                Item("series-host-first", ErrorSearchActivityStates.Ended, AsOf.AddHours(-3)),
                Item("series-host-second", ErrorSearchActivityStates.Active, AsOf.AddHours(-1)),
            ],
            facets: new ErrorSearchFacets(
                [
                    new ErrorSearchCategoryFacetSnapshot("OBSERVATION_CONFLICT", 19),
                    new ErrorSearchCategoryFacetSnapshot("DATA_FORMAT", 23),
                ],
                [
                    new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Ended, 31),
                    new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Active, 11),
                ]));

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot),
            new ErrorSearchQuery(
                filter,
                ErrorSearchWindowSelection.Last7Days,
                PageSize: snapshot.PageSize,
                SnapshotReference: snapshot.SnapshotReference));

        Assert.True(presentation.HasSnapshot);
        Assert.Contains("ErrorSearchAsOf 2026-08-14 05:06:07 UTC", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("commit-error-22", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("序列 220", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("PollTrace poll-error-22", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Equal(
            "UTC 半开窗口 [2026-08-07 05:06:07 UTC, 2026-08-14 05:06:07 UTC)",
            presentation.CommittedWindow);
        Assert.Contains("分类 DATA_FORMAT", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("错误码 INVALID_MES_FIELD_FORMAT", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("状态 ACTIVE、ENDED", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("SeriesId SERIES-TICKET-22", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("DemandId DEMAND-TICKET-22", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("SUBLOT 包含 SL-TICKET-22", presentation.CommittedConditions, StringComparison.Ordinal);
        Assert.Equal("精确 37 个 DemandSeries · 第 2 / 4 页", presentation.PageSummary);
        Assert.Empty(presentation.EmptyResultMessage);
        Assert.Equal($"Host 固定排序：{ErrorSearchOrder.Default}", presentation.OrderSummary);
        Assert.True(presentation.CanGoPrevious);
        Assert.True(presentation.CanGoNext);

        Assert.Equal(
            [("OBSERVATION_CONFLICT", 19L), ("DATA_FORMAT", 23L)],
            presentation.CategoryFacets.Select(value => (value.Category, value.SeriesCount)).ToArray());
        Assert.Equal(
            [(ErrorSearchActivityStates.Ended, 31L), (ErrorSearchActivityStates.Active, 11L)],
            presentation.ActivityFacets.Select(value => (value.State, value.SeriesCount)).ToArray());
        Assert.Equal(
            ["series-host-first", "series-host-second"],
            presentation.Rows.Select(row => row.SeriesId).ToArray());
        Assert.Equal(ErrorSearchActivityStates.Ended, presentation.Rows[0].ActivityState);
        Assert.Equal(ErrorSearchActivityStates.Active, presentation.Rows[1].ActivityState);
        Assert.Equal("命中 2 个期间", presentation.Rows[0].MatchedPeriodSummary);
        Assert.Equal("跨 2 个 Demand 世代", presentation.Rows[0].MatchedDemandGenerationSummary);
    }

    [Fact]
    public void Successful_zero_is_distinct_from_loading_and_failed_retention()
    {
        var committed = FullFilter();
        var zero = Snapshot(
            committed,
            total: 0,
            pageNumber: 1,
            totalPages: 0,
            items: [],
            facets: new ErrorSearchFacets(
                [new ErrorSearchCategoryFacetSnapshot("DATA_FORMAT", 0)],
                [new ErrorSearchActivityStateFacetSnapshot(ErrorSearchActivityStates.Ended, 0)]));
        var successfulView = Workspace(zero).ErrorSearch;
        var attempted = new ErrorSearchQuery(
            new ErrorSearchFilter
            {
                Categories = ["DATA_COMPLETENESS"],
                ErrorCodes = ["REQUIRED_MES_FIELD_MISSING"],
                ActivityStates = [ErrorSearchActivityStates.Active],
                SeriesId = "series-attempted",
            },
            ErrorSearchWindowSelection.Last15Days,
            PageSize: 200);

        var successful = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(successfulView),
            new ErrorSearchQuery(committed, ErrorSearchWindowSelection.Last7Days));
        var loading = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(successfulView with
            {
                IsRefreshing = true,
                PendingQueryKey = "attempted-error-query",
            }),
            attempted);
        var canceled = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(successfulView with
            {
                PendingQueryKey = "canceled-error-query",
            }),
            attempted);
        var failedAt = AsOf.AddMinutes(2);
        var failed = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(successfulView with
            {
                PendingQueryKey = "attempted-error-query",
                LastFailureAt = failedAt,
                FailedQueryKey = "attempted-error-query",
                FailureKind = WatchHostFailureKind.Http,
                FailureCode = ErrorSearchErrorCodes.InvalidCursor,
                ErrorMessage = "The frozen cursor was rejected.",
                CorrelationId = "correlation-error-22",
            }),
            attempted);

        Assert.Equal(
            "查询成功；Host 在当前已提交条件下精确 0 个 DemandSeries 命中。",
            successful.EmptyResultMessage);
        Assert.False(successful.IsInfoOpen);

        Assert.True(loading.IsRefreshing);
        Assert.True(loading.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Informational, loading.InfoSeverity);
        Assert.Equal("正在刷新错误历史", loading.InfoTitle);
        Assert.Empty(loading.EmptyResultMessage);
        Assert.Contains("继续显示冻结快照", loading.InfoMessage, StringComparison.Ordinal);

        Assert.True(canceled.IsStale);
        Assert.True(canceled.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, canceled.InfoSeverity);
        Assert.Contains("已取消", canceled.InfoTitle, StringComparison.Ordinal);
        Assert.Contains("继续显示冻结快照", canceled.InfoMessage, StringComparison.Ordinal);
        Assert.Empty(canceled.EmptyResultMessage);
        Assert.Equal(successful.SnapshotFacts, canceled.SnapshotFacts);
        Assert.Equal(successful.CommittedConditions, canceled.CommittedConditions);

        Assert.True(failed.IsStale);
        Assert.True(failed.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Warning, failed.InfoSeverity);
        Assert.Contains("已保留上次快照", failed.InfoTitle, StringComparison.Ordinal);
        Assert.Contains(ErrorSearchErrorCodes.InvalidCursor, failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("correlation-error-22", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Empty(failed.EmptyResultMessage);
        Assert.Contains("分类 DATA_FORMAT", failed.CommittedConditions, StringComparison.Ordinal);
        Assert.DoesNotContain("DATA_COMPLETENESS", failed.CommittedConditions, StringComparison.Ordinal);
        Assert.Contains("DATA_COMPLETENESS", failed.CurrentQuerySummary, StringComparison.Ordinal);
        Assert.Equal(successful.SnapshotFacts, failed.SnapshotFacts);
        Assert.Equal(successful.CommittedWindow, failed.CommittedWindow);
        Assert.Equal(0, Assert.Single(failed.CategoryFacets).SeriesCount);
    }

    [Fact]
    public void No_snapshot_loading_never_claims_an_empty_history()
    {
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 1,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            ErrorSearch = WatchV2ViewState<
                ErrorSearchListSnapshot,
                ErrorSearchDetailSnapshot>.Empty(1) with
            {
                IsRefreshing = true,
                PendingQueryKey = "initial-error-query",
            },
        };

        var presentation = WatchErrorSearchPresentation.Project(
            workspace,
            WatchErrorSearchQueries.StartLatest());

        Assert.False(presentation.HasSnapshot);
        Assert.True(presentation.IsRefreshing);
        Assert.Equal("正在读取错误历史", presentation.InfoTitle);
        Assert.Equal("尚无 Error Search Host 快照", presentation.SnapshotFacts);
        Assert.Empty(presentation.EmptyResultMessage);
        Assert.Empty(presentation.Rows);
        Assert.Null(presentation.Detail);
        Assert.False(presentation.RawEvidence.IsVisible);
    }

    [Fact]
    public void Matched_detail_preserves_window_boundaries_and_cross_generation_evidence()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf.AddMinutes(-1))]);
        var detail = Detail(snapshot);

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days));

        var selected = Assert.IsType<WatchErrorSearchDetailPresentation>(presentation.Detail);
        Assert.Equal("series-detail-22 · ACTIVE", selected.Heading);
        Assert.Equal(["demand-generation-1", "demand-generation-2"], selected.MatchedDemandIds);
        Assert.Equal(
            "跨 2 个 Demand 世代：demand-generation-1、demand-generation-2",
            selected.GenerationSummary);
        Assert.Equal(["period-before-after", "period-active"], selected.Periods.Select(period => period.PeriodId));

        var bounded = selected.Periods[0];
        Assert.True(bounded.StartsBeforeWindow);
        Assert.True(bounded.EndsAfterWindow);
        Assert.False(bounded.ActiveAtAsOf);
        Assert.Equal("期间开始早于窗口，结束晚于窗口", bounded.BoundarySummary);
        Assert.Equal("CONDITION_CLEARED", bounded.EndReason);
        var scalar = Assert.Single(bounded.Evidence);
        Assert.Equal("demand-generation-1", scalar.DemandId);
        Assert.Equal("AREA", scalar.SubjectKind);
        Assert.Equal("N03-08", scalar.DiagnosticSummary);
        Assert.Equal("^[A-Z][1-9][0-9]?$", scalar.ExpectedRule);
        Assert.Equal("poll-generation-1", scalar.PollTraceId);
        Assert.Equal("commit-generation-1", scalar.ProjectionCommitId);
        Assert.Equal("DemandId demand-generation-1", scalar.SubjectReference);
        Assert.False(scalar.CanReadRawEvidence);

        var active = selected.Periods[1];
        Assert.False(active.StartsBeforeWindow);
        Assert.True(active.EndsAfterWindow);
        Assert.True(active.ActiveAtAsOf);
        Assert.Equal("期间延伸到窗口之后 · ErrorSearchAsOf 时仍为 ACTIVE", active.BoundarySummary);
        var membership = active.Evidence[0];
        Assert.Equal("WorkType DIE_ATTACH、WIRE_TO_GATE", membership.DiagnosticSummary);
        Assert.Equal("DemandId demand-generation-2 · WorkType DIE_ATTACH、WIRE_TO_GATE", membership.SubjectReference);
        var rawSummary = active.Evidence[1];
        Assert.Equal("2 条原始观测 · SHA-256 digest-ticket-22", rawSummary.DiagnosticSummary);
        Assert.True(rawSummary.CanReadRawEvidence);
        Assert.False(presentation.RawEvidence.IsVisible);
    }

    [Fact]
    public void Detail_loading_failure_and_recovery_are_distinct_from_list_refresh_state()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var retainedList = Workspace(snapshot).ErrorSearch;
        var loadingView = retainedList with
        {
            SelectedId = "series-detail-22",
            IsDetailLoading = true,
        };

        var loading = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(loadingView),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days));
        var failedView = loadingView with
        {
            IsDetailLoading = false,
            DetailLastFailureAt = AsOf.AddMinutes(1),
            DetailFailureKind = WatchHostFailureKind.ServerQuery,
            DetailFailureCode = "DETAIL_FAILED",
            DetailErrorMessage = "The selected detail could not be read.",
            DetailCorrelationId = "correlation-detail-22",
        };
        var failed = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(failedView),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days));
        var recoveredView = failedView with
        {
            Detail = Detail(snapshot),
            DetailLastFailureAt = null,
            DetailFailureKind = WatchHostFailureKind.None,
            DetailFailureCode = null,
            DetailErrorMessage = null,
            DetailCorrelationId = null,
        };
        var recovered = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(recoveredView),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days));

        Assert.False(loading.IsInfoOpen);
        Assert.False(loading.IsStale);
        Assert.True(loading.DetailStatus.IsLoading);
        Assert.False(loading.DetailStatus.HasFailure);
        Assert.Equal("正在读取同快照错误详情", loading.DetailStatus.Title);

        Assert.False(failed.IsInfoOpen);
        Assert.False(failed.IsStale);
        Assert.False(failed.DetailStatus.IsLoading);
        Assert.True(failed.DetailStatus.HasFailure);
        Assert.Equal(WatchPresentationSeverity.Error, failed.DetailStatus.Severity);
        Assert.Contains("DETAIL_FAILED", failed.DetailStatus.Message, StringComparison.Ordinal);
        Assert.Contains("correlation-detail-22", failed.DetailStatus.Message, StringComparison.Ordinal);
        Assert.Equal(loading.SnapshotFacts, failed.SnapshotFacts);
        Assert.Equal(loading.CommittedConditions, failed.CommittedConditions);

        Assert.NotNull(recovered.Detail);
        Assert.False(recovered.DetailStatus.IsLoading);
        Assert.False(recovered.DetailStatus.HasFailure);
        Assert.Equal("错误详情已读取", recovered.DetailStatus.Title);
    }

    [Fact]
    public void A_detail_that_does_not_match_the_selected_series_is_hidden_without_claiming_success()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var mismatched = Detail(snapshot) with
        {
            Series = Item("series-other", ErrorSearchActivityStates.Active, AsOf),
        };
        var view = Workspace(snapshot).ErrorSearch with
        {
            SelectedId = "series-detail-22",
            Detail = mismatched,
        };

        var presentation = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(view),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days));

        Assert.Null(presentation.Detail);
        Assert.False(presentation.DetailStatus.IsLoading);
        Assert.False(presentation.DetailStatus.HasFailure);
        Assert.Equal("错误详情尚不可用", presentation.DetailStatus.Title);
        Assert.DoesNotContain("已读取", presentation.DetailStatus.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_selected_detail_is_not_tied_to_the_current_page_rows()
    {
        var selectedPage = Snapshot(
            FullFilter(),
            total: 2,
            pageNumber: 1,
            totalPages: 2,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var currentPage = selectedPage with
        {
            PageNumber = 2,
            Items = [Item("series-other", ErrorSearchActivityStates.Active, AsOf.AddMinutes(-1))],
        };
        var view = Workspace(currentPage).ErrorSearch with
        {
            SelectedId = "series-detail-22",
            Detail = Detail(selectedPage),
        };

        var presentation = WatchErrorSearchPresentation.Project(
            WorkspaceWithView(view),
            new ErrorSearchQuery(currentPage.Filter, ErrorSearchWindowSelection.Last7Days));

        Assert.NotNull(presentation.Detail);
        Assert.Equal("错误详情已读取", presentation.DetailStatus.Title);
    }

    [Fact]
    public void Explicit_raw_evidence_projects_only_returned_allowed_fields_and_host_limits()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var raw = RawSnapshot(snapshot, fields:
        [
            ErrorSearchRawEvidenceFields.WorkType,
            ErrorSearchRawEvidenceFields.Package,
        ]);

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        var rawPresentation = presentation.RawEvidence;
        Assert.True(rawPresentation.IsVisible);
        Assert.False(rawPresentation.IsLoading);
        Assert.False(rawPresentation.IsStale);
        Assert.False(rawPresentation.HasContractViolation);
        Assert.Equal(WatchPresentationSeverity.Success, rawPresentation.StatusSeverity);
        Assert.Equal(
            [ErrorSearchRawEvidenceFields.WorkType, ErrorSearchRawEvidenceFields.Package],
            rawPresentation.IncludedFields);
        Assert.Equal("最多 20 条 · 单条 2,048 字节 · 总计 65,536 字节", rawPresentation.LimitsSummary);
        Assert.Equal(1, rawPresentation.ItemCount);
        Assert.Equal(raw.PayloadBytes, rawPresentation.PayloadBytes);
        var row = Assert.Single(rawPresentation.Items);
        Assert.Equal("poll-raw-22", row.PollTraceId);
        Assert.Equal("demand-generation-2", row.DemandId);
        Assert.Equal(
            [ErrorSearchRawEvidenceFields.WorkType, ErrorSearchRawEvidenceFields.Package],
            row.Fields.Select(field => field.Name));
        Assert.DoesNotContain(row.Fields, field => field.Name == ErrorSearchRawEvidenceFields.Area);
        Assert.Equal("WIRE_TO_GATE", row.Fields[0].Value);
        Assert.Equal("[REDACTED]", row.Fields[1].Value);
    }

    [Fact]
    public void Raw_failure_retains_prior_raw_rows_and_never_erases_the_bounded_detail()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var retained = WatchErrorRawEvidenceState.Loaded(RawSnapshot(
            snapshot,
            [ErrorSearchRawEvidenceFields.Package]));
        var failed = WatchErrorRawEvidenceState.Failed(
            snapshot.SnapshotReference,
            "series-detail-22",
            "evidence-raw-22",
            AsOf.AddMinutes(1),
            ErrorSearchErrorCodes.RawAccessDenied,
            "Raw access is not authorized.",
            "correlation-raw-22",
            retained);

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            failed);

        Assert.NotNull(presentation.Detail);
        Assert.Equal(2, presentation.Detail.Periods.Count);
        Assert.True(presentation.RawEvidence.IsVisible);
        Assert.True(presentation.RawEvidence.IsStale);
        Assert.Equal(WatchPresentationSeverity.Warning, presentation.RawEvidence.StatusSeverity);
        Assert.Contains(ErrorSearchErrorCodes.RawAccessDenied, presentation.RawEvidence.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("correlation-raw-22", presentation.RawEvidence.StatusMessage, StringComparison.Ordinal);
        Assert.Single(presentation.RawEvidence.Items);
        Assert.Equal("[REDACTED]", Assert.Single(presentation.RawEvidence.Items[0].Fields).Value);
    }

    [Fact]
    public void Out_of_contract_raw_payload_is_isolated_without_rendering_excess_items()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var valid = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]);
        var excessive = valid with
        {
            ItemCount = ErrorSearchRawEvidenceLimits.MaximumItems + 1,
            Items = Enumerable.Range(1, ErrorSearchRawEvidenceLimits.MaximumItems + 1)
                .Select(ordinal => valid.Items[0] with { Ordinal = ordinal })
                .ToArray(),
        };

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(excessive));

        Assert.NotNull(presentation.Detail);
        Assert.True(presentation.RawEvidence.IsVisible);
        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Equal(WatchPresentationSeverity.Error, presentation.RawEvidence.StatusSeverity);
        Assert.Contains("超出受限原始证据契约", presentation.RawEvidence.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    [Fact]
    public void Raw_payload_with_a_non_allowlisted_included_field_is_rejected_before_rendering()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var raw = RawSnapshot(snapshot, ["operatorSecret"]);

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.NotNull(presentation.Detail);
        Assert.True(presentation.RawEvidence.IsVisible);
        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Equal(WatchPresentationSeverity.Error, presentation.RawEvidence.StatusSeverity);
        Assert.Empty(presentation.RawEvidence.IncludedFields);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    [Fact]
    public void Raw_payload_accepts_exactly_the_maximum_twenty_items()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var template = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]);
        var raw = WithMeasuredPayloadBytes(template with
        {
            ItemCount = ErrorSearchRawEvidenceLimits.MaximumItems,
            PayloadBytes = 0,
            Items = Enumerable.Range(1, ErrorSearchRawEvidenceLimits.MaximumItems)
                .Select(ordinal => template.Items[0] with { Ordinal = ordinal })
                .ToArray(),
        });

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.False(presentation.RawEvidence.HasContractViolation);
        Assert.Equal(WatchPresentationSeverity.Success, presentation.RawEvidence.StatusSeverity);
        Assert.Equal(ErrorSearchRawEvidenceLimits.MaximumItems, presentation.RawEvidence.ItemCount);
        Assert.Equal(ErrorSearchRawEvidenceLimits.MaximumItems, presentation.RawEvidence.Items.Count);
    }

    [Fact]
    public void Raw_payload_with_underreported_measured_utf8_bytes_is_rejected_before_rendering()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var raw = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]) with
        {
            PayloadBytes = 1,
        };

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    [Fact]
    public void Raw_payload_with_utf8_item_over_the_byte_limit_is_rejected_before_rendering()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var template = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]);
        var oversizedItem = template.Items[0] with
        {
            Fields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ErrorSearchRawEvidenceFields.Package] = new string('界', 700),
            },
        };
        var raw = WithMeasuredPayloadBytes(template with
        {
            PayloadBytes = 0,
            Items = [oversizedItem],
        });

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    [Fact]
    public void Raw_payload_with_measured_utf8_total_over_the_byte_limit_is_rejected_before_rendering()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var template = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]);
        var raw = WithMeasuredPayloadBytes(template with
        {
            PeriodId = new string('界', 22_000),
            PayloadBytes = 0,
        });

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    [Fact]
    public void Raw_payload_with_a_field_outside_included_fields_is_rejected_before_rendering()
    {
        var snapshot = Snapshot(
            FullFilter(),
            total: 1,
            pageNumber: 1,
            totalPages: 1,
            items: [Item("series-detail-22", ErrorSearchActivityStates.Active, AsOf)]);
        var detail = Detail(snapshot);
        var template = RawSnapshot(snapshot, [ErrorSearchRawEvidenceFields.Package]);
        var unexpectedField = template.Items[0] with
        {
            Fields = new Dictionary<string, string?>(template.Items[0].Fields, StringComparer.Ordinal)
            {
                [ErrorSearchRawEvidenceFields.Area] = "A1-1",
            },
        };
        var raw = WithMeasuredPayloadBytes(template with
        {
            PayloadBytes = 0,
            Items = [unexpectedField],
        });

        var presentation = WatchErrorSearchPresentation.Project(
            Workspace(snapshot, detail),
            new ErrorSearchQuery(snapshot.Filter, ErrorSearchWindowSelection.Last7Days),
            WatchErrorRawEvidenceState.Loaded(raw));

        Assert.True(presentation.RawEvidence.HasContractViolation);
        Assert.Empty(presentation.RawEvidence.Items);
    }

    private static ErrorSearchFilter FullFilter() => new()
    {
        Categories = ["DATA_FORMAT"],
        ErrorCodes = ["INVALID_MES_FIELD_FORMAT"],
        ActivityStates = [ErrorSearchActivityStates.Active, ErrorSearchActivityStates.Ended],
        SeriesId = "SERIES-TICKET-22",
        DemandId = "DEMAND-TICKET-22",
        SublotContains = "SL-TICKET-22",
    };

    private static ErrorSearchListSnapshot Snapshot(
        ErrorSearchFilter filter,
        long total,
        int pageNumber,
        int totalPages,
        IReadOnlyList<ErrorSearchListItemSnapshot> items,
        ErrorSearchFacets? facets = null) => new(
        "snapshot-error-22",
        Identity(),
        filter.Normalize(),
        new ErrorSearchResolvedWindow(
            ErrorSearchWindowKinds.Last7Days,
            AsOf.AddDays(-7),
            AsOf),
        ErrorSearchOrder.Default,
        total,
        facets ?? new ErrorSearchFacets([], []),
        PageSize: 10,
        PageNumber: pageNumber,
        TotalPages: totalPages,
        items,
        NextCursor: pageNumber < totalPages ? $"cursor-page-{pageNumber + 1}" : null,
        HasMore: pageNumber < totalPages);

    private static ErrorSearchListItemSnapshot Item(
        string seriesId,
        string activity,
        DateTimeOffset evidenceAt) => new(
        seriesId,
        "WIRE_TO_GATE",
        "SL-TICKET-22",
        activity,
        [
            new ErrorSearchMatchedErrorSnapshot(
                "INVALID_MES_FIELD_FORMAT",
                "DATA_FORMAT",
                "ERROR"),
            new ErrorSearchMatchedErrorSnapshot(
                "DUPLICATE_TRANSPORT_DEMAND_KEY",
                "OBSERVATION_CONFLICT",
                "ERROR"),
        ],
        evidenceAt,
        MatchedPeriodCount: 2,
        MatchedDemandGenerationCount: 2,
        MesArea: "A1-1",
        ErrorSearchMesAreaAvailability.CurrentTrusted);

    private static ErrorSearchDetailSnapshot Detail(ErrorSearchListSnapshot snapshot) => new(
        snapshot.SnapshotReference,
        snapshot.Snapshot,
        snapshot.Filter,
        snapshot.Window,
        snapshot.Order,
        snapshot.Items[0],
        [
            new ErrorSearchDetailPeriodSnapshot(
                "period-before-after",
                "INVALID_MES_FIELD_FORMAT",
                "DATA_FORMAT",
                "ERROR",
                "demand-generation-1:AREA",
                "AREA",
                "BOOTSTRAPPED_CURRENT_CONDITION",
                AsOf.AddDays(-8),
                AsOf.AddMinutes(1),
                "CONDITION_CLEARED",
                StartsBeforeWindow: true,
                EndsAfterWindow: true,
                ActiveAtAsOf: false,
                [
                    new ErrorSearchDetailEvidenceSnapshot(
                        "evidence-scalar-22",
                        "BOOTSTRAPPED_CURRENT_CONDITION",
                        "AREA",
                        AsOf.AddDays(-8),
                        "poll-generation-1",
                        "commit-generation-1",
                        "demand-generation-1",
                        [],
                        new ErrorSearchDiagnosticValueSnapshot(
                            ErrorSearchDiagnosticValueKinds.Scalar,
                            ScalarValue: "N03-08"),
                        "^[A-Z][1-9][0-9]?$",
                        RawEvidenceAvailable: false),
                ]),
            new ErrorSearchDetailPeriodSnapshot(
                "period-active",
                "SUBLOT_MULTIPLE_WORK_TYPES",
                "OBSERVATION_CONFLICT",
                "ERROR",
                "SL-TICKET-22",
                "SUBLOT",
                "CONDITION_OPENED",
                AsOf.AddHours(-1),
                EndedAt: null,
                EndReason: null,
                StartsBeforeWindow: false,
                EndsAfterWindow: true,
                ActiveAtAsOf: true,
                [
                    new ErrorSearchDetailEvidenceSnapshot(
                        "evidence-membership-22",
                        "CONDITION_OPENED",
                        "SUBLOT",
                        AsOf.AddHours(-1),
                        "poll-generation-2",
                        "commit-generation-2",
                        "demand-generation-2",
                        ["DIE_ATTACH", "WIRE_TO_GATE"],
                        new ErrorSearchDiagnosticValueSnapshot(
                            ErrorSearchDiagnosticValueKinds.WorkTypeMembership),
                        "ONE_WORK_TYPE_PER_SUBLOT",
                        RawEvidenceAvailable: false),
                    new ErrorSearchDetailEvidenceSnapshot(
                        "evidence-raw-22",
                        "CONDITION_OPENED",
                        "RAW_OBSERVATION_SET",
                        AsOf.AddMinutes(-30),
                        "poll-raw-22",
                        "commit-raw-22",
                        "demand-generation-2",
                        [],
                        new ErrorSearchDiagnosticValueSnapshot(
                            ErrorSearchDiagnosticValueKinds.RawObservationSet,
                            ObservationCount: 2,
                            Sha256Digest: "digest-ticket-22"),
                        "EXACTLY_ONE_RAW_OBSERVATION",
                        RawEvidenceAvailable: true),
                ]),
        ]);

    private static ErrorSearchRawEvidenceSnapshot RawSnapshot(
        ErrorSearchListSnapshot snapshot,
        IReadOnlyList<string> fields)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ErrorSearchRawEvidenceFields.WorkType] = "WIRE_TO_GATE",
            [ErrorSearchRawEvidenceFields.Package] = "[REDACTED]",
            [ErrorSearchRawEvidenceFields.Area] = "A1-1",
            ["operatorSecret"] = "must-not-render",
        };
        var selected = fields.ToDictionary(
            field => field,
            field => values.TryGetValue(field, out var value) ? value : "unexpected",
            StringComparer.Ordinal);
        return WithMeasuredPayloadBytes(new ErrorSearchRawEvidenceSnapshot(
            snapshot.SnapshotReference,
            snapshot.Snapshot,
            "series-detail-22",
            "period-active",
            "evidence-raw-22",
            "poll-raw-22",
            "commit-raw-22",
            "demand-generation-2",
            fields,
            ItemCount: 1,
            new ErrorSearchRawEvidenceLimitsSnapshot(
                ErrorSearchRawEvidenceLimits.MaximumItems,
                ErrorSearchRawEvidenceLimits.MaximumItemBytes,
                ErrorSearchRawEvidenceLimits.MaximumTotalBytes),
            PayloadBytes: 0,
            [
                new ErrorSearchRawEvidenceItemSnapshot(
                    Ordinal: 1,
                    "poll-raw-22",
                    "commit-raw-22",
                    "demand-generation-2",
                    AsOf.AddMinutes(-30),
                    selected),
            ]));
    }

    private static ErrorSearchRawEvidenceSnapshot WithMeasuredPayloadBytes(
        ErrorSearchRawEvidenceSnapshot snapshot)
    {
        var current = snapshot;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var measuredBytes = JsonSerializer.SerializeToUtf8Bytes(current).Length;
            if (measuredBytes == current.PayloadBytes)
            {
                return current;
            }
            current = current with { PayloadBytes = measuredBytes };
        }

        return current with
        {
            PayloadBytes = JsonSerializer.SerializeToUtf8Bytes(current).Length,
        };
    }

    private static ErrorSearchSnapshotIdentity Identity() => new(
        HistoryEpoch.FromGuid(Guid.Parse("55555555-5555-5555-5555-555555555555")),
        AsOf,
        "commit-error-22",
        ProjectionSequence: 220,
        AsOf.AddMinutes(-1),
        "poll-error-22");

    private static WatchV2WorkspaceState Workspace(
        ErrorSearchListSnapshot snapshot,
        ErrorSearchDetailSnapshot? detail = null) =>
        WorkspaceWithView(WatchV2ViewState<
            ErrorSearchListSnapshot,
            ErrorSearchDetailSnapshot>.Empty(3) with
        {
            PendingQueryKey = "error-query",
            CommittedQueryKey = "error-query",
            Snapshot = snapshot,
            LastSuccessfulAt = AsOf,
            SelectedId = detail?.Series.SeriesId,
            Detail = detail,
        });

    private static WatchV2WorkspaceState WorkspaceWithView(
        WatchV2ViewState<ErrorSearchListSnapshot, ErrorSearchDetailSnapshot> view) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: view.HostGeneration,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            ErrorSearch = view,
        };
}
