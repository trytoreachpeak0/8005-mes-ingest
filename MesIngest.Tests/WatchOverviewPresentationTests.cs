using System.Globalization;
using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchOverviewPresentationTests
{
    [Fact]
    public void English_overview_catalog_and_presenter_explain_unloaded_metrics_without_isolated_dashes()
    {
        var catalog = WatchTextCatalog.For(WatchDisplayLanguage.English);
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 1,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected);

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas,
            catalog,
            DateTimeOffset.Parse("2026-08-27T14:05:06+08:00"));

        Assert.Equal("Overview", catalog.Overview.PageTitle);
        Assert.Equal("No Host business snapshot", presentation.SnapshotFacts);
        Assert.Equal("Not loaded", presentation.SeriesValue);
        Assert.Equal("Demand series", presentation.SeriesUnit);
        Assert.Equal("Not loaded", presentation.ReadabilityValue);
        Assert.Equal("Demands", presentation.ReadabilityUnit);
        Assert.Equal("Host committed scope: no snapshot", presentation.HostAreaScope);
    }

    [Fact]
    public void English_failed_refresh_retains_one_complete_snapshot_with_system_local_relative_time_units_and_navigation()
    {
        var snapshot = OverviewSnapshot();
        var failedAt = snapshot.Snapshot.SnapshotAsOf.AddSeconds(25);
        var view = SuccessfulView(snapshot) with
        {
            RequestGeneration = 2,
            LastFailureAt = failedAt,
            FailureKind = WatchHostFailureKind.Timeout,
            FailureCode = "WATCH_TIMEOUT",
            ErrorMessage = "The overview request timed out.",
        };
        var catalog = WatchTextCatalog.For(WatchDisplayLanguage.English);

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(view),
            WatchAreaDisplayContext.AllAreas,
            catalog,
            snapshot.Snapshot.SnapshotAsOf.AddSeconds(18));

        Assert.True(presentation.IsStale);
        Assert.Contains("last complete snapshot retained", presentation.InfoTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continues to show Host snapshot", presentation.InfoMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DisplayTime(snapshot.Snapshot.SnapshotAsOf), presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Contains("18 seconds ago", presentation.SnapshotFacts, StringComparison.Ordinal);
        Assert.Equal("Demand series", presentation.SeriesUnit);
        Assert.Equal("Demands", presentation.ReadabilityUnit);
        Assert.Same(snapshot.Readability.Navigation, presentation.ReadabilityNavigation);
        Assert.Equal("All AREA", presentation.LocalAreaHeading);
    }

    private const long HostGeneration = 19;
    private const string CommittedQueryKey = "area=A1-1";

    [Fact]
    public void Atomic_snapshot_projects_all_four_summaries_and_keeps_host_and_client_times_distinct()
    {
        var snapshot = OverviewSnapshot();
        var clientSuccessfulAt = DateTimeOffset.Parse("2026-08-14T01:23:45Z");
        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot, clientSuccessfulAt)),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(presentation.HasSnapshot);
        Assert.Equal("1,234", presentation.SeriesValue);
        Assert.Equal("700 / 777", presentation.ReadabilityValue);
        Assert.Equal("12", presentation.ErrorsValue);
        Assert.Equal("9", presentation.AttentionValue);
        Assert.Contains("34 归档后仍可见", presentation.SeriesDetail, StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(snapshot.Snapshot.SnapshotAsOf),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(snapshot.Snapshot.ProjectionCommittedAt),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.Contains(
            DisplayTime(clientSuccessfulAt),
            presentation.ClientAttemptFacts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DisplayTime(clientSuccessfulAt),
            presentation.SnapshotFacts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DisplayTime(snapshot.Snapshot.SnapshotAsOf),
            presentation.ClientAttemptFacts,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_recent_highlights_uses_the_required_empty_text_without_claiming_overall_health()
    {
        var snapshot = OverviewSnapshot() with
        {
            RecentActivity = [],
            RecentActivityState = WatchOverviewRecentActivityStates.NoRecentHighlights,
            EmptyStateMessage = null,
        };

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal("近期无重点动态", presentation.RecentActivityHeading);
        Assert.Empty(presentation.RecentActivity);

        var visibleText = string.Join(
            " | ",
            presentation.HostStatus,
            presentation.HostDetail,
            presentation.InfoTitle,
            presentation.InfoMessage,
            presentation.SnapshotFacts,
            presentation.ClientAttemptFacts,
            presentation.SeriesValue,
            presentation.SeriesDetail,
            presentation.ReadabilityValue,
            presentation.ReadabilityDetail,
            presentation.ErrorsValue,
            presentation.ErrorsDetail,
            presentation.AttentionValue,
            presentation.AttentionDetail,
            presentation.LocalAreaHeading,
            presentation.LocalAreaDetail,
            presentation.HostAreaScope,
            presentation.RecentActivityHeading);
        Assert.DoesNotContain("健康", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("一切正常", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("无异常", visibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void Refreshing_a_different_query_keeps_the_entire_committed_snapshot_and_marks_it_stale()
    {
        var snapshot = OverviewSnapshot();
        var committed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);
        var refreshingView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 2,
            IsRefreshing = true,
            PendingQueryKey = "area=B2-2",
        };

        var refreshing = WatchOverviewPresentation.Project(
            ConnectedWorkspace(refreshingView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(refreshing.HasSnapshot);
        Assert.True(refreshing.IsRefreshing);
        Assert.True(refreshing.IsStale);
        AssertSnapshotProjectionEqual(committed, refreshing);
        Assert.Contains("继续显示服务端快照", refreshing.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_refresh_keeps_the_entire_committed_snapshot_and_marks_it_stale()
    {
        var snapshot = OverviewSnapshot();
        var committed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);
        var failedView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 2,
            IsRefreshing = false,
            LastFailureAt = DateTimeOffset.Parse("2026-08-14T01:25:00Z"),
            FailureKind = WatchHostFailureKind.Timeout,
            FailureCode = "WATCH_TIMEOUT",
            ErrorMessage = "The overview request timed out.",
            CorrelationId = "correlation-ticket-19",
        };

        var failed = WatchOverviewPresentation.Project(
            ConnectedWorkspace(failedView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(failed.HasSnapshot);
        Assert.False(failed.IsRefreshing);
        Assert.True(failed.IsStale);
        AssertSnapshotProjectionEqual(committed, failed);
        Assert.Contains("已保留上次完整快照", failed.InfoTitle, StringComparison.Ordinal);
        Assert.Contains("最近失败", failed.ClientAttemptFacts, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrying_after_a_failed_refresh_reports_refreshing_while_retaining_failure_context()
    {
        var snapshot = OverviewSnapshot();
        var failedAt = DateTimeOffset.Parse("2026-08-14T01:25:00Z");
        var retryingView = SuccessfulView(snapshot) with
        {
            RequestGeneration = 3,
            IsRefreshing = true,
            PendingQueryKey = "area=B2-2",
            LastFailureAt = failedAt,
            FailureKind = WatchHostFailureKind.Timeout,
            FailureCode = "WATCH_TIMEOUT",
            ErrorMessage = "The prior overview request timed out.",
        };

        var retrying = WatchOverviewPresentation.Project(
            ConnectedWorkspace(retryingView),
            WatchAreaDisplayContext.AllAreas);

        Assert.True(retrying.IsRefreshing);
        Assert.True(retrying.IsStale);
        Assert.Equal("正在刷新概览", retrying.InfoTitle);
        Assert.Contains("继续显示服务端快照", retrying.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("上次失败", retrying.InfoMessage, StringComparison.Ordinal);
        Assert.Contains(DisplayTime(failedAt), retrying.InfoMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void New_local_area_context_does_not_relabel_the_retained_host_snapshot_scope()
    {
        var snapshot = OverviewSnapshot() with { MesAreas = ["A1-1"] };
        var localContext = new WatchAreaDisplayContext(
            "本机 B2-2 班次",
            ["B2-2"],
            "本机已选择",
            DateTimeOffset.Parse("2026-08-14T01:30:00Z"));

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            localContext);

        Assert.Equal("本机 B2-2 班次", presentation.LocalAreaHeading);
        Assert.Contains("本机已选择", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Contains("1 个区域", presentation.LocalAreaDetail, StringComparison.Ordinal);
        Assert.Equal("服务端已提交范围：A1-1", presentation.HostAreaScope);
        Assert.DoesNotContain("B2-2", presentation.HostAreaScope, StringComparison.Ordinal);
        Assert.DoesNotContain("A1-1", presentation.LocalAreaHeading, StringComparison.Ordinal);
        Assert.DoesNotContain("A1-1", presentation.LocalAreaDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_host_replacement_with_no_snapshot_explicitly_shows_that_old_data_was_cleared()
    {
        var workspace = WatchV2WorkspaceState.Reset(
            HostGeneration + 1,
            "https://replacement-host.example",
            WatchHostConnectionStatus.Failed) with
        {
            FailureKind = WatchHostFailureKind.Contract,
            FailureCode = "CONTRACT_MISMATCH",
            ErrorMessage = "The replacement Host contract is incompatible.",
            CorrelationId = "replacement-correlation",
        };

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas);

        Assert.False(presentation.HasSnapshot);
        Assert.False(presentation.IsStale);
        Assert.Equal("尚未加载", presentation.SeriesValue);
        Assert.Equal("尚未加载", presentation.ReadabilityValue);
        Assert.Equal("尚未加载", presentation.ErrorsValue);
        Assert.Equal("尚未加载", presentation.AttentionValue);
        Assert.Equal("服务端已提交范围：尚无快照", presentation.HostAreaScope);
        Assert.Contains("旧服务端数据已清空", presentation.InfoMessage, StringComparison.Ordinal);
        Assert.Null(presentation.SeriesNavigation);
        Assert.Null(presentation.ReadabilityNavigation);
        Assert.Null(presentation.ErrorsNavigation);
        Assert.Null(presentation.AttentionNavigation);
        Assert.Empty(presentation.RecentActivity);
    }

    [Fact]
    public void Navigation_intent_objects_are_projected_unchanged()
    {
        var seriesIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.DemandSeries,
            PageNumber: 1,
            MesAreas: ["A1-1"],
            Lifecycles: ["TRACKING"],
            WorkType: "WT-A",
            Cursor: null);
        var readabilityIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit,
            PageNumber: 1,
            MesAreas: ["A1-1"],
            ReadabilityStates: ["NOT_READABLE"],
            Cursor: null);
        var errorsIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.ErrorSearch,
            PageNumber: 1,
            ErrorActivityStates: ["ACTIVE"],
            ErrorWindow: "PRIOR_7_DAYS",
            Cursor: null);
        var attentionIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention,
            PageNumber: 1,
            AttentionKinds: [CurrentIngestAttentionKinds.PollRunFailure],
            AttentionSeverities: [CurrentIngestAttentionSeverities.Error],
            PollTraceId: "poll-ticket-19",
            Cursor: null);
        var activityIntent = new OverviewNavigationIntent(
            OverviewNavigationTargets.PollTrace,
            PageNumber: 1,
            PollTraceId: "poll-ticket-19",
            Cursor: null);
        var snapshot = OverviewSnapshot(
            seriesIntent,
            readabilityIntent,
            errorsIntent,
            attentionIntent,
            activityIntent);

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        Assert.Same(seriesIntent, presentation.SeriesNavigation);
        Assert.Same(readabilityIntent, presentation.ReadabilityNavigation);
        Assert.Same(errorsIntent, presentation.ErrorsNavigation);
        Assert.Same(attentionIntent, presentation.AttentionNavigation);
        Assert.Single(presentation.RecentActivity);
        Assert.Equal("制造执行系统轮询失败", presentation.RecentActivity[0].Heading);
        Assert.Same(activityIntent, presentation.RecentActivity[0].Navigation);
        Assert.Equal(1, presentation.SeriesNavigation!.PageNumber);
        Assert.Null(presentation.SeriesNavigation.Cursor);
        Assert.Equal(["A1-1"], presentation.SeriesNavigation.MesAreas);
    }

    public static IEnumerable<object[]> KnownOverviewEventCases()
    {
        yield return ["DEMAND_SERIES_STARTED", "需求系列开始跟踪", "INFORMATION", "Informational", "信息", OverviewNavigationTargets.DemandSeriesDetail, null!];
        yield return ["DEMAND_GONE", "运输需求已消失", "WARNING", "Warning", "警告", OverviewNavigationTargets.DemandSeriesDetail, null!];
        yield return ["GONE_TIMEOUT_ARCHIVED", "需求系列已超时归档", "WARNING", "Warning", "警告", OverviewNavigationTargets.DemandSeriesDetail, null!];
        yield return ["SERIES_ERROR_PERIOD_STARTED", "需求系列错误已开始", "ERROR", "Error", "错误", OverviewNavigationTargets.ErrorSearch, null!];
        yield return ["SERIES_ERROR_PERIOD_ENDED", "需求系列错误已恢复", "SUCCESS", "Success", "已恢复", OverviewNavigationTargets.ErrorSearch, "CONDITION_CLEARED"];
        yield return ["TRANSPORT_DEMAND_CREATED", "运输需求再次出现", "INFORMATION", "Informational", "信息", OverviewNavigationTargets.DemandSeriesDetail, null!];
        yield return ["TASK_TYPE_PROTECTION_ENTERED", "工序类型保护已启动", "WARNING", "Warning", "警告", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["TASK_TYPE_PROTECTION_RECOVERY_PROGRESS", "工序类型保护正在恢复", "WARNING", "Warning", "警告", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["TASK_TYPE_PROTECTION_CLEARED", "工序类型保护已解除", "SUCCESS", "Success", "已恢复", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["TASK_TYPE_ABSENCE_AUTHORITY_RESTORED", "工序类型缺席判定已恢复", "SUCCESS", "Success", "已恢复", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["UNASSIGNED_MES_OBSERVATION_APPEARED", "出现未归属的制造执行系统观测", "ERROR", "Error", "错误", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED", "未归属的制造执行系统观测已变化", "ERROR", "Error", "错误", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["UNASSIGNED_MES_OBSERVATION_CLEARED", "未归属的制造执行系统观测已清除", "SUCCESS", "Success", "已恢复", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["POLL_RUN_FAILED", "制造执行系统轮询失败", "ERROR", "Error", "错误", OverviewNavigationTargets.CurrentIngestAttention, null!];
        yield return ["POLL_RUN_RECOVERED", "制造执行系统轮询已恢复", "SUCCESS", "Success", "已恢复", OverviewNavigationTargets.CurrentIngestAttention, null!];
    }

    [Theory]
    [MemberData(nameof(KnownOverviewEventCases))]
    public void Known_overview_event_types_project_human_conclusions_semantic_severity_and_navigation(
        string eventType,
        string expectedHeading,
        string sourceSeverity,
        string expectedSeverity,
        string expectedSeverityText,
        string expectedNavigationTarget,
        string? endReason)
    {
        var activity = ProjectActivity(
            eventType,
            sourceSeverity,
            expectedNavigationTarget,
            endReason is null ? null : new WatchOverviewActivityExplanation(EndReason: endReason));

        Assert.Equal(expectedHeading, activity.Heading);
        Assert.Equal(expectedSeverity, activity.Severity.ToString());
        Assert.Equal(expectedSeverityText, activity.SeverityText);
        Assert.Equal(expectedNavigationTarget, activity.Navigation.Target);
        Assert.DoesNotContain(eventType, activity.Heading, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONDITION_CLEARED", "需求系列错误已恢复", "错误条件已消除")]
    [InlineData("DEMAND_GONE", "需求消失，错误期间已结束", "需求已消失")]
    [InlineData("GONE_TIMEOUT_ARCHIVED", "需求系列归档，错误期间已结束", "需求系列已归档")]
    public void Ended_error_periods_distinguish_recovery_demand_disappearance_and_series_archive(
        string endReason,
        string expectedHeading,
        string expectedExplanation)
    {
        var activity = ProjectActivity(
            "SERIES_ERROR_PERIOD_ENDED",
            endReason == "CONDITION_CLEARED" ? "SUCCESS" : "WARNING",
            OverviewNavigationTargets.ErrorSearch,
            new WatchOverviewActivityExplanation(EndReason: endReason));

        Assert.Equal(expectedHeading, activity.Heading);
        Assert.Contains(expectedExplanation, activity.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_area_format_explains_observed_and_expected_values_from_the_frozen_snapshot()
    {
        var activity = ProjectActivity(
            "SERIES_ERROR_PERIOD_STARTED",
            "ERROR",
            OverviewNavigationTargets.ErrorSearch,
            new WatchOverviewActivityExplanation(
                Code: "INVALID_MES_FIELD_FORMAT",
                SubjectKind: "AREA",
                ObservedValue: "D7-04",
                ExpectedRule: "D7-4"),
            workType: "WIRE_TO_NITROGEN");

        Assert.Contains("D7-04", activity.Explanation, StringComparison.Ordinal);
        Assert.Contains("D7-4", activity.Explanation, StringComparison.Ordinal);
        Assert.Contains("对象类型：区域", activity.Metadata, StringComparison.Ordinal);
        Assert.Contains("需求系列：", activity.Metadata, StringComparison.Ordinal);
        Assert.Contains("工序类型：焊线1机台 → 氮气柜", activity.Metadata, StringComparison.Ordinal);
        Assert.Contains("轮询追踪：", activity.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("INVALID_MES_FIELD_FORMAT", activity.Metadata, StringComparison.Ordinal);
        Assert.Contains("Code=INVALID_MES_FIELD_FORMAT", activity.TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("SeriesId=series-activity-test", activity.TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("PollTraceId=poll-activity-test", activity.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Chinese_activity_metadata_localizes_known_work_type_and_escalates_unknown_codes()
    {
        var known = ProjectActivity(
            "DEMAND_SERIES_STARTED",
            "INFORMATION",
            OverviewNavigationTargets.DemandSeriesDetail,
            workType: "WIRE_TO_NITROGEN");
        var unknown = ProjectActivity(
            "FUTURE_OVERVIEW_EVENT_99",
            "WARNING",
            OverviewNavigationTargets.CurrentIngestAttention,
            workType: "FUTURE_WORK_TYPE_99");

        Assert.Contains("工序类型：焊线1机台 → 氮气柜", known.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("WIRE_TO_NITROGEN", known.Metadata, StringComparison.Ordinal);
        Assert.Equal("未知概览事件（请升级应用）", unknown.Heading);
        Assert.Contains("请升级", unknown.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch", unknown.Explanation, StringComparison.Ordinal);
        Assert.Contains("未知工序（请升级应用）", unknown.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("FUTURE_WORK_TYPE_99", unknown.Metadata, StringComparison.Ordinal);
        Assert.Contains("EventType=FUTURE_OVERVIEW_EVENT_99", unknown.TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("WorkType=FUTURE_WORK_TYPE_99", unknown.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Poll_failure_uses_safe_detail_without_reclassifying_it_as_an_observed_value()
    {
        const string safeDetail = "连接制造执行系统超时，已保留上次完整快照。";

        var activity = ProjectActivity(
            "POLL_RUN_FAILED",
            "ERROR",
            OverviewNavigationTargets.CurrentIngestAttention,
            new WatchOverviewActivityExplanation(SafeDetail: safeDetail));

        Assert.Contains(safeDetail, activity.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("观测值：", activity.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("观测值：", activity.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_conflict_explanations_use_counts_and_localized_related_work_types()
    {
        var duplicate = ProjectActivity(
            "SERIES_ERROR_PERIOD_STARTED",
            "ERROR",
            OverviewNavigationTargets.ErrorSearch,
            new WatchOverviewActivityExplanation(
                Code: "DUPLICATE_TRANSPORT_DEMAND_KEY",
                ObservationCount: 3));
        var multipleWorkTypes = ProjectActivity(
            "SERIES_ERROR_PERIOD_STARTED",
            "ERROR",
            OverviewNavigationTargets.ErrorSearch,
            new WatchOverviewActivityExplanation(
                Code: "SUBLOT_MULTIPLE_WORK_TYPES",
                RelatedWorkTypes: ["WIRE_TO_NITROGEN", "DIE_TO_OVEN"]));

        Assert.Contains("3", duplicate.Explanation, StringComparison.Ordinal);
        Assert.Contains("焊线1机台 → 氮气柜", multipleWorkTypes.Explanation, StringComparison.Ordinal);
        Assert.Contains("装片机台 → 烘箱间", multipleWorkTypes.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("WIRE_TO_NITROGEN", multipleWorkTypes.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("DIE_TO_OVEN", multipleWorkTypes.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_health_surfaces_history_reset_with_epoch_and_current_read_restriction()
    {
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var snapshot = OverviewSnapshot() with
        {
            Snapshot = OverviewSnapshot().Snapshot with { HistoryEpoch = epoch },
            Attention = OverviewSnapshot().Attention with
            {
                Types =
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionKinds.HistoryReset,
                        1,
                        new OverviewNavigationIntent(
                            OverviewNavigationTargets.CurrentIngestAttention,
                            AttentionKinds: [CurrentIngestAttentionKinds.HistoryReset])),
                ],
            },
        };

        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal("历史重置待确认", presentation.Protection.Status);
        Assert.Equal(WatchPresentationSeverity.Error, presentation.Protection.Severity);
        Assert.True(presentation.Protection.RequiresAttention);
        Assert.Contains(epoch.ToString(), presentation.Protection.Detail, StringComparison.Ordinal);
        Assert.Contains("接入告警", presentation.Protection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_history_epoch_uses_explicit_catalog_semantics()
    {
        var snapshot = OverviewSnapshot() with
        {
            Attention = OverviewSnapshot().Attention with
            {
                Types =
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionKinds.HistoryReset,
                        1,
                        new OverviewNavigationIntent(
                            OverviewNavigationTargets.CurrentIngestAttention)),
                ],
            },
        };

        foreach (var (language, expectedMissing) in new[]
        {
            (WatchDisplayLanguage.SimplifiedChinese, "系统未知"),
            (WatchDisplayLanguage.English, "Unknown to system"),
        })
        {
            var presentation = WatchOverviewPresentation.Project(
                ConnectedWorkspace(SuccessfulView(snapshot)),
                WatchAreaDisplayContext.AllAreas,
                WatchTextCatalog.For(language));

            Assert.Contains(expectedMissing, presentation.Protection.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("—", presentation.Protection.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Newer_overview_without_protection_facets_clears_an_older_reset_detail()
    {
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var overview = OverviewSnapshot() with
        {
            Snapshot = OverviewSnapshot().Snapshot with { HistoryEpoch = epoch },
        };
        var resetItem = new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.HistoryReset,
            CurrentIngestAttentionSeverities.Error,
            overview.Snapshot.SnapshotAsOf,
            $"HISTORY_RESET:{epoch}",
            SeriesId: null,
            WorkType: null,
            ErrorCode: HistoryResetStatuses.AcknowledgementRequired,
            Target: "MesIngest",
            SubjectKind: "HISTORY_EPOCH",
            new CurrentIngestAttentionEvidenceSnapshot(
                Phase: HistoryResetStatuses.AcknowledgementRequired,
                DatabaseName: "MesIngest"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention));
        var resetSnapshot = new CurrentIngestAttentionSnapshot(
            overview.Snapshot,
            1,
            new CurrentIngestAttentionFacets(
                [new(CurrentIngestAttentionKinds.HistoryReset, 1)],
                [new(CurrentIngestAttentionSeverities.Error, 1)]),
            CurrentIngestAttentionOrder.Default,
            2,
            1,
            1,
            [CurrentIngestAttentionKinds.HistoryReset],
            [],
            [resetItem]);
        var workspace = ConnectedWorkspace(SuccessfulView(
            overview,
            DateTimeOffset.Parse("2026-08-14T02:00:00Z"))) with
        {
            Protection = WatchV2ViewState<
                CurrentIngestAttentionSnapshot,
                WatchNoDetail>.Empty(HostGeneration) with
            {
                RequestGeneration = 1,
                PendingQueryKey = "protection",
                CommittedQueryKey = "protection",
                Snapshot = resetSnapshot,
                LastSuccessfulAt = DateTimeOffset.Parse("2026-08-14T01:59:59Z"),
            },
        };

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal("未报告存储或历史保护项", presentation.Protection.Status);
        Assert.Equal(WatchPresentationSeverity.Success, presentation.Protection.Severity);
    }

    [Fact]
    public void Protection_slot_remains_authoritative_when_a_newer_filtered_attention_page_omits_reset()
    {
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var overview = OverviewSnapshot() with
        {
            Snapshot = OverviewSnapshot().Snapshot with { HistoryEpoch = epoch },
            Attention = OverviewSnapshot().Attention with
            {
                Types =
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionKinds.HistoryReset,
                        1,
                        new OverviewNavigationIntent(
                            OverviewNavigationTargets.CurrentIngestAttention)),
                ],
            },
        };
        var resetItem = new CurrentIngestAttentionItemSnapshot(
            CurrentIngestAttentionKinds.HistoryReset,
            CurrentIngestAttentionSeverities.Error,
            overview.Snapshot.SnapshotAsOf,
            $"HISTORY_RESET:{epoch}",
            SeriesId: null,
            WorkType: null,
            ErrorCode: HistoryResetStatuses.AcknowledgementRequired,
            Target: "MesIngest",
            SubjectKind: "HISTORY_EPOCH",
            new CurrentIngestAttentionEvidenceSnapshot(
                Phase: HistoryResetStatuses.AcknowledgementRequired,
                DatabaseName: "MesIngest"),
            new OverviewNavigationIntent(
                OverviewNavigationTargets.CurrentIngestAttention));
        var resetSnapshot = new CurrentIngestAttentionSnapshot(
            overview.Snapshot,
            1,
            new CurrentIngestAttentionFacets(
                [new(CurrentIngestAttentionKinds.HistoryReset, 1)],
                [new(CurrentIngestAttentionSeverities.Error, 1)]),
            CurrentIngestAttentionOrder.Default,
            2,
            1,
            1,
            [CurrentIngestAttentionKinds.HistoryReset],
            [],
            [resetItem]);
        var filteredSnapshot = resetSnapshot with
        {
            ExactTotalItemCount = 0,
            Facets = new CurrentIngestAttentionFacets([], []),
            TotalPages = 0,
            Kinds = [CurrentIngestAttentionKinds.SeriesError],
            Items = [],
        };
        var workspace = ConnectedWorkspace(SuccessfulView(overview)) with
        {
            Protection = AttentionView(
                resetSnapshot,
                "protection",
                DateTimeOffset.Parse("2026-08-14T01:21:00Z")),
            CurrentAttention = AttentionView(
                filteredSnapshot,
                "kind=SERIES_ERROR",
                DateTimeOffset.Parse("2026-08-14T01:22:00Z")),
        };

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal("历史重置待确认", presentation.Protection.Status);
        Assert.Equal(WatchPresentationSeverity.Error, presentation.Protection.Severity);
    }

    [Theory]
    [InlineData(StoragePressureStatuses.Healthy, 18.0, "存储与历史保护正常", (int)WatchPresentationSeverity.Success, false)]
    [InlineData(StoragePressureStatuses.Warning, 14.5, "存储空间严重告警", (int)WatchPresentationSeverity.Warning, true)]
    [InlineData(StoragePressureStatuses.Paused, 9.5, "StoragePressurePause", (int)WatchPresentationSeverity.Error, true)]
    public void Current_attention_storage_state_drives_precise_global_protection_status(
        string storageStatus,
        double availablePercent,
        string expectedStatus,
        int expectedSeverity,
        bool requiresAttention)
    {
        var overview = OverviewSnapshot();
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("44444444-4444-4444-4444-444444444444"));
        overview = overview with
        {
            Snapshot = overview.Snapshot with { HistoryEpoch = epoch },
        };
        var attentionSnapshot = new CurrentIngestAttentionSnapshot(
            overview.Snapshot,
            ExactTotalItemCount: 0,
            new CurrentIngestAttentionFacets([], []),
            CurrentIngestAttentionOrder.Default,
            PageSize: 100,
            PageNumber: 1,
            TotalPages: 0,
            Kinds: [],
            Severities: [],
            Items: [],
            HistoryCleanupStateSnapshot.NotRun,
            new StoragePressureStateSnapshot(
                storageStatus,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(
                    @"D:\",
                    1_000_000,
                    Convert.ToDecimal(availablePercent, CultureInfo.InvariantCulture)),
                DateTimeOffset.Parse("2026-08-14T01:16:00Z"),
                storageStatus == StoragePressureStatuses.Paused
                    ? DateTimeOffset.Parse("2026-08-14T01:15:30Z")
                    : null,
                storageStatus == StoragePressureStatuses.Paused ? "pause-21" : null,
                storageStatus == StoragePressureStatuses.Paused ? "low space" : null,
                RecoveryAuditId: null));
        var workspace = ConnectedWorkspace(SuccessfulView(overview)) with
        {
            CurrentAttention = WatchV2ViewState<
                CurrentIngestAttentionSnapshot,
                WatchNoDetail>.Empty(HostGeneration) with
            {
                RequestGeneration = 1,
                PendingQueryKey = "attention",
                CommittedQueryKey = "attention",
                Snapshot = attentionSnapshot,
                LastSuccessfulAt = DateTimeOffset.Parse("2026-08-14T01:21:01Z"),
            },
        };

        var presentation = WatchOverviewPresentation.Project(
            workspace,
            WatchAreaDisplayContext.AllAreas);

        Assert.Equal(expectedStatus, presentation.Protection.Status);
        Assert.Equal((WatchPresentationSeverity)expectedSeverity, presentation.Protection.Severity);
        Assert.Equal(requiresAttention, presentation.Protection.RequiresAttention);
        Assert.Contains($"{availablePercent:0.###}%", presentation.Protection.Detail, StringComparison.Ordinal);
    }

    private static WatchV2WorkspaceState ConnectedWorkspace(
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> overview) =>
        WatchV2WorkspaceState.Reset(
            HostGeneration,
            "https://host-a.example",
            WatchHostConnectionStatus.Connected) with
        {
            Overview = overview,
        };

    private static WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail> SuccessfulView(
        WatchOverviewSnapshot snapshot,
        DateTimeOffset? lastSuccessfulAt = null) =>
        WatchV2ViewState<WatchOverviewSnapshot, WatchNoDetail>.Empty(HostGeneration) with
        {
            RequestGeneration = 1,
            PendingQueryKey = CommittedQueryKey,
            CommittedQueryKey = CommittedQueryKey,
            Snapshot = snapshot,
            LastSuccessfulAt = lastSuccessfulAt
                ?? DateTimeOffset.Parse("2026-08-14T01:20:00Z"),
        };

    private static WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> AttentionView(
        CurrentIngestAttentionSnapshot snapshot,
        string queryKey,
        DateTimeOffset lastSuccessfulAt) =>
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail>.Empty(HostGeneration) with
        {
            RequestGeneration = 1,
            PendingQueryKey = queryKey,
            CommittedQueryKey = queryKey,
            Snapshot = snapshot,
            LastSuccessfulAt = lastSuccessfulAt,
        };

    private static WatchOverviewSnapshot OverviewSnapshot(
        OverviewNavigationIntent? seriesIntent = null,
        OverviewNavigationIntent? readabilityIntent = null,
        OverviewNavigationIntent? errorsIntent = null,
        OverviewNavigationIntent? attentionIntent = null,
        OverviewNavigationIntent? activityIntent = null)
    {
        seriesIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.DemandSeries);
        readabilityIntent ??= new OverviewNavigationIntent(
            OverviewNavigationTargets.ReadabilityAudit);
        errorsIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.ErrorSearch);
        attentionIntent ??= new OverviewNavigationIntent(
            OverviewNavigationTargets.CurrentIngestAttention);
        activityIntent ??= new OverviewNavigationIntent(OverviewNavigationTargets.PollTrace);
        var snapshotAsOf = DateTimeOffset.Parse("2026-08-14T01:15:00Z");
        var projectionCommittedAt = DateTimeOffset.Parse("2026-08-14T01:14:00Z");
        return new WatchOverviewSnapshot(
            new OperationalSnapshotIdentity(
                "projection-ticket-19",
                419,
                projectionCommittedAt,
                "poll-ticket-19",
                421,
                23,
                snapshotAsOf),
            ["A1-1"],
            new WatchOverviewSeriesSummary(
                1_234,
                900,
                200,
                100,
                34,
                seriesIntent,
                seriesIntent,
                seriesIntent,
                seriesIntent,
                seriesIntent),
            new WatchOverviewReadabilitySummary(
                777,
                700,
                77,
                readabilityIntent,
                readabilityIntent,
                readabilityIntent),
            new WatchOverviewErrorSummary(
                12,
                34,
                errorsIntent,
                errorsIntent,
                errorsIntent),
            new WatchOverviewAttentionSummary(
                9,
                [],
                [
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Error,
                        2,
                        attentionIntent),
                    new OverviewFacetSnapshot(
                        CurrentIngestAttentionSeverities.Warning,
                        7,
                        attentionIntent),
                ],
                attentionIntent),
            [
                new WatchOverviewActivitySnapshot(
                    "event-ticket-19",
                    CurrentIngestAttentionKinds.PollRunFailure,
                    "POLL_RUN_FAILED",
                    CurrentIngestAttentionSeverities.Error,
                    DateTimeOffset.Parse("2026-08-14T01:12:00Z"),
                    "series-ticket-19",
                    "WT-A",
                    "poll-ticket-19",
                    "projection-ticket-19",
                    activityIntent),
            ],
            WatchOverviewRecentActivityStates.HasRecentHighlights,
            null);
    }

    private static WatchOverviewActivityPresentation ProjectActivity(
        string eventType,
        string severity,
        string navigationTarget,
        WatchOverviewActivityExplanation? explanation = null,
        string? workType = null)
    {
        var kind = eventType switch
        {
            "SERIES_ERROR_PERIOD_STARTED" or "SERIES_ERROR_PERIOD_ENDED" => "SERIES_ERROR_PERIOD",
            "TASK_TYPE_PROTECTION_ENTERED" or "TASK_TYPE_PROTECTION_RECOVERY_PROGRESS"
                or "TASK_TYPE_PROTECTION_CLEARED" or "TASK_TYPE_ABSENCE_AUTHORITY_RESTORED" => "TASK_TYPE_PROTECTION",
            "UNASSIGNED_MES_OBSERVATION_APPEARED" or "UNASSIGNED_MES_OBSERVATION_CONTENT_CHANGED"
                or "UNASSIGNED_MES_OBSERVATION_CLEARED" => "UNASSIGNED_MES_OBSERVATION",
            "POLL_RUN_FAILED" or "POLL_RUN_RECOVERED" => "POLL_RUN_FAILURE",
            _ => "SERIES_LIFECYCLE",
        };
        var navigation = new OverviewNavigationIntent(
            navigationTarget,
            SeriesId: "series-activity-test",
            WorkType: workType,
            PollTraceId: "poll-activity-test");
        var snapshot = OverviewSnapshot() with
        {
            RecentActivity =
            [
                new WatchOverviewActivitySnapshot(
                    "event-activity-test",
                    kind,
                    eventType,
                    severity,
                    DateTimeOffset.Parse("2026-08-14T01:12:00Z"),
                    "series-activity-test",
                    workType,
                    "poll-activity-test",
                    "projection-ticket-19",
                    navigation,
                    explanation),
            ],
        };
        var presentation = WatchOverviewPresentation.Project(
            ConnectedWorkspace(SuccessfulView(snapshot)),
            WatchAreaDisplayContext.AllAreas);

        return Assert.Single(presentation.RecentActivity);
    }

    private static void AssertSnapshotProjectionEqual(
        WatchOverviewPresentation expected,
        WatchOverviewPresentation actual)
    {
        Assert.Equal(expected.SnapshotFacts, actual.SnapshotFacts);
        Assert.Equal(expected.SeriesValue, actual.SeriesValue);
        Assert.Equal(expected.SeriesDetail, actual.SeriesDetail);
        Assert.Equal(expected.ReadabilityValue, actual.ReadabilityValue);
        Assert.Equal(expected.ReadabilityDetail, actual.ReadabilityDetail);
        Assert.Equal(expected.ErrorsValue, actual.ErrorsValue);
        Assert.Equal(expected.ErrorsDetail, actual.ErrorsDetail);
        Assert.Equal(expected.AttentionValue, actual.AttentionValue);
        Assert.Equal(expected.AttentionDetail, actual.AttentionDetail);
        Assert.Equal(expected.HostAreaScope, actual.HostAreaScope);
        Assert.Equal(expected.RecentActivityHeading, actual.RecentActivityHeading);
        Assert.Equal(expected.RecentActivity, actual.RecentActivity);
        Assert.Same(expected.SeriesNavigation, actual.SeriesNavigation);
        Assert.Same(expected.ReadabilityNavigation, actual.ReadabilityNavigation);
        Assert.Same(expected.ErrorsNavigation, actual.ErrorsNavigation);
        Assert.Same(expected.AttentionNavigation, actual.AttentionNavigation);
    }

    private static string DisplayTime(DateTimeOffset value) =>
        WatchTimeDisplay.Format(value);
}
