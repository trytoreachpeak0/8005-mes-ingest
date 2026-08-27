using MesIngest.Core.SeriesProjection;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchCurrentIngestAttentionPresentationTests
{
    [Fact]
    public void Host_total_facets_order_and_page_state_remain_authoritative()
    {
        var snapshot = Snapshot(
            exactTotal: 407,
            pageNumber: 2,
            totalPages: 5,
            kinds:
            [
                CurrentIngestAttentionKinds.SeriesError,
                CurrentIngestAttentionKinds.PollRunFailure,
            ],
            severities: [CurrentIngestAttentionSeverities.Error],
            items: SevenKinds());
        snapshot = snapshot with
        {
            Facets = new CurrentIngestAttentionFacets(
                [
                    new(CurrentIngestAttentionKinds.TaskTypeProtection, 17),
                    new(CurrentIngestAttentionKinds.SeriesError, 388),
                    new(CurrentIngestAttentionKinds.PollRunFailure, 1),
                    new(CurrentIngestAttentionKinds.UnassignedMesObservation, 1),
                    new(CurrentIngestAttentionKinds.HistoryCleanupFailure, 0),
                    new(CurrentIngestAttentionKinds.StoragePressure, 0),
                    new(CurrentIngestAttentionKinds.HistoryReset, 0),
                ],
                [
                    new(CurrentIngestAttentionSeverities.Warning, 17),
                    new(CurrentIngestAttentionSeverities.Error, 390),
                ]),
        };
        var currentDraft = WatchCurrentIngestAttentionQueries.StartLatest(
            [CurrentIngestAttentionKinds.UnassignedMesObservation],
            [CurrentIngestAttentionSeverities.Warning],
            pageSize: 200);

        var presentation = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            currentDraft);

        Assert.True(presentation.HasSnapshot);
        Assert.Equal("精确 407 个当前关注项 · 第 2 / 5 页", presentation.PageSummary);
        Assert.Equal(
            $"Host 固定排序：{CurrentIngestAttentionOrder.Default}",
            presentation.OrderSummary);
        Assert.Contains(CurrentIngestAttentionKinds.SeriesError, presentation.HostFilterSummary);
        Assert.Contains(CurrentIngestAttentionSeverities.Error, presentation.HostFilterSummary);
        Assert.DoesNotContain(
            CurrentIngestAttentionKinds.UnassignedMesObservation,
            presentation.HostFilterSummary,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentIngestAttentionKinds.UnassignedMesObservation,
            presentation.CurrentQuerySummary,
            StringComparison.Ordinal);
        Assert.True(presentation.CanGoPrevious);
        Assert.True(presentation.CanGoNext);
        Assert.Equal(
            [17L, 388L, 1L, 1L, 0L, 0L, 0L],
            presentation.TypeFacets.Select(facet => facet.ItemCount).ToArray());
        Assert.Equal(
            [
                CurrentIngestAttentionSeverities.Warning,
                CurrentIngestAttentionSeverities.Error,
            ],
            presentation.SeverityFacets.Select(facet => facet.Value).ToArray());
        Assert.Equal(
            SevenKinds().Select(item => item.StableIdentity),
            presentation.Rows.Select(row => row.StableIdentity));
        Assert.Equal(
            "全 Host 当前关注；本机 AREA 配置不会筛选、计数或翻页此页面。",
            presentation.AreaIsolationNotice);
    }

    [Fact]
    public void All_seven_kinds_keep_stable_identity_and_structured_evidence()
    {
        var presentation = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(Snapshot(items: SevenKinds())),
            WatchCurrentIngestAttentionQueries.StartLatest());

        Assert.Equal(
            CurrentIngestAttentionKinds.All.Order(StringComparer.Ordinal),
            presentation.Rows.Select(row => row.Kind).Order(StringComparer.Ordinal));

        var series = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.SeriesError));
        var identity = Assert.IsType<WatchSeriesErrorIdentityPresentation>(
            series.SeriesErrorIdentity);
        Assert.Equal(
            "series-22:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
            identity.StableIdentity);
        Assert.Equal("series-22", identity.SeriesId);
        Assert.Equal("REQUIRED_MES_FIELD_MISSING", identity.ErrorCode);
        Assert.Equal("DATA_COMPLETENESS", identity.Category);
        Assert.Equal("DEMAND:demand-22", identity.Target);
        Assert.Equal("EQP", identity.SubjectKind);
        Assert.Equal("poll-series-22", series.Evidence.PollTraceId);
        Assert.Equal("demand-22", series.Evidence.DemandId);
        Assert.Contains("PollTrace poll-series-22", series.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("Demand demand-22", series.Evidence.Facts, StringComparison.Ordinal);
        Assert.Equal("活动需求系列错误 · SERIES_ERROR", series.KindLabel);
        Assert.Equal(WatchPresentationSeverity.Error, series.SeverityStyle);
        var drill = Assert.IsType<ErrorSearchQuery>(series.ErrorSearchDrill);
        Assert.Equal([ErrorSearchActivityStates.Active], drill.Filter.ActivityStates);
        Assert.Equal([identity.Category], drill.Filter.Categories);
        Assert.Equal([identity.ErrorCode], drill.Filter.ErrorCodes);
        Assert.Equal("SERIES-22", drill.Filter.SeriesId);
        Assert.Equal(ErrorSearchWindowKinds.AllHistory, drill.Window.Kind);
        Assert.Null(drill.SnapshotReference);
        Assert.Null(drill.Cursor);

        var poll = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.PollRunFailure));
        Assert.Null(poll.SeriesErrorIdentity);
        Assert.Null(poll.ErrorSearchDrill);
        Assert.Equal("poll-failure-22", poll.Evidence.PollTraceId);
        Assert.Equal(44, poll.Evidence.PollTraceSequence);
        Assert.Equal("INCOMPLETE", poll.Evidence.Outcome);
        Assert.Contains("PollTrace poll-failure-22", poll.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("结果 INCOMPLETE", poll.Evidence.Facts, StringComparison.Ordinal);
        Assert.Equal("轮询运行失败 · POLL_RUN_FAILURE", poll.KindLabel);
        Assert.Equal(OverviewNavigationTargets.PollTrace, poll.Navigation.Target);

        var protection = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.TaskTypeProtection));
        Assert.Equal("WIRE_TO_NITROGEN", protection.WorkType);
        Assert.Equal("WIRE_TO_NITROGEN", protection.Evidence.WorkType);
        Assert.Equal("RECOVERING", protection.Evidence.Phase);
        Assert.Contains("WorkType WIRE_TO_NITROGEN", protection.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("阶段 RECOVERING", protection.Evidence.Facts, StringComparison.Ordinal);
        Assert.Equal("任务类型保护 · TASK_TYPE_PROTECTION", protection.KindLabel);
        Assert.Equal(WatchPresentationSeverity.Warning, protection.SeverityStyle);
        Assert.Equal(OverviewNavigationTargets.TaskTypeProtection, protection.Navigation.Target);

        var unassigned = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.UnassignedMesObservation));
        Assert.Equal("poll-unassigned-22", unassigned.Evidence.PollTraceId);
        Assert.Equal(45, unassigned.Evidence.PollTraceSequence);
        Assert.Equal(7, unassigned.Evidence.ObservationOrdinal);
        Assert.Equal("digest-unassigned-22", unassigned.Evidence.ContentDigest);
        Assert.Contains("观测序号 7", unassigned.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("Digest digest-unassigned-22", unassigned.Evidence.Facts, StringComparison.Ordinal);
        Assert.Equal("未归属 MES 观测 · UNASSIGNED_MES_OBSERVATION", unassigned.KindLabel);
        Assert.Equal(OverviewNavigationTargets.PollTrace, unassigned.Navigation.Target);

        var cleanup = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.HistoryCleanupFailure));
        Assert.Equal("HISTORY_CLEANUP_FAILURE", cleanup.StableIdentity);
        Assert.Equal("历史清理失败 · HISTORY_CLEANUP_FAILURE", cleanup.KindLabel);
        Assert.Equal(WatchPresentationSeverity.Error, cleanup.SeverityStyle);
        Assert.Equal(OverviewNavigationTargets.CurrentIngestAttention, cleanup.Navigation.Target);

        var storage = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.StoragePressure));
        Assert.Equal("存储压力 · STORAGE_PRESSURE", storage.KindLabel);
        Assert.Contains("数据库 MesIngest", storage.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("卷 D:\\", storage.Evidence.Facts, StringComparison.Ordinal);
        Assert.Contains("可用 9.5%", storage.Evidence.Facts, StringComparison.Ordinal);
        Assert.Equal(WatchPresentationSeverity.Error, storage.SeverityStyle);
        Assert.Equal(OverviewNavigationTargets.CurrentIngestAttention, storage.Navigation.Target);

        Assert.Equal(
            "只读当前关注项；不创建 fingerprint incident，不提供人工确认、人工恢复或关闭操作。",
            presentation.SemanticsNotice);
        Assert.DoesNotContain(
            typeof(WatchCurrentIngestAttentionRowPresentation).GetProperties(),
            property => property.Name.Contains("Incident", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Acknowledge", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Recover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Storage_pause_and_history_reset_project_complete_local_recovery_guidance()
    {
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var earliest = DateTimeOffset.Parse("2026-07-15T05:06:07Z");
        var pausedAt = DateTimeOffset.Parse("2026-08-14T05:05:00Z");
        var snapshot = Snapshot(items: SevenKinds()) with
        {
            Snapshot = Identity() with { HistoryEpoch = epoch },
            HistoryCleanup = HistoryCleanupStateSnapshot.NotRun with
            {
                Status = HistoryCleanupRunStatuses.Succeeded,
                EarliestAvailableHostUtc = earliest,
            },
            StoragePressure = new StoragePressureStateSnapshot(
                StoragePressureStatuses.Paused,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 9.5m),
                pausedAt,
                pausedAt,
                "pause-22",
                "database volume below the 10 percent pause threshold",
                RecoveryAuditId: null),
        };

        var presentation = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            WatchCurrentIngestAttentionQueries.StartLatest());

        var storage = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.StoragePressure));
        var storageProtection = Assert.IsType<WatchProtectionDetailPresentation>(storage.Protection);
        Assert.Equal(StoragePressureStatuses.Paused, storageProtection.Status);
        Assert.Contains("database volume below", storageProtection.Reason, StringComparison.Ordinal);
        Assert.Contains("poll-attention-22", storageProtection.LastSuccessfulWindow, StringComparison.Ordinal);
        Assert.Contains("commit-attention-22", storageProtection.LastSuccessfulWindow, StringComparison.Ordinal);
        Assert.Contains(DisplayTime(earliest), storageProtection.EarliestAvailable, StringComparison.Ordinal);
        Assert.Contains("数据库主机本地控制台", storageProtection.LocalAdministrationGuidance, StringComparison.Ordinal);
        Assert.Contains("resume-storage-pressure", storageProtection.LocalAdministrationGuidance, StringComparison.Ordinal);
        Assert.Contains("--database \"MesIngest\"", storageProtection.LocalAdministrationGuidance, StringComparison.Ordinal);
        Assert.Contains(epoch.ToString(), storageProtection.LocalAdministrationGuidance, StringComparison.Ordinal);

        var historyReset = Assert.Single(presentation.Rows.Where(row =>
            row.Kind == CurrentIngestAttentionKinds.HistoryReset));
        Assert.Equal("历史重置 · HISTORY_RESET", historyReset.KindLabel);
        var resetProtection = Assert.IsType<WatchProtectionDetailPresentation>(historyReset.Protection);
        Assert.Equal(HistoryResetStatuses.AcknowledgementRequired, resetProtection.Status);
        Assert.Contains(epoch.ToString(), resetProtection.RebuildProgress, StringComparison.Ordinal);
        Assert.Contains("INGEST_NOT_CURRENT", resetProtection.CurrentReadRestriction, StringComparison.Ordinal);
        Assert.Contains("503", resetProtection.CurrentReadRestriction, StringComparison.Ordinal);
        Assert.Contains("acknowledge-history-reset", resetProtection.LocalAdministrationGuidance, StringComparison.Ordinal);
        Assert.Contains(
            HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance,
            resetProtection.LocalAdministrationGuidance,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(WatchCurrentIngestAttentionRowPresentation).GetProperties(),
            property => property.PropertyType == typeof(System.Windows.Input.ICommand));
    }

    [Fact]
    public void Storage_warning_does_not_claim_pause_current_read_503_or_offer_resume()
    {
        var epoch = HistoryEpoch.FromGuid(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var warningItem = SevenKinds()[1] with
        {
            Severity = CurrentIngestAttentionSeverities.Warning,
            ErrorCode = StoragePressureStatuses.Warning,
            Evidence = SevenKinds()[1].Evidence with
            {
                Phase = StoragePressureStatuses.Warning,
                FailureReason = null,
                AvailablePercent = 14.5m,
            },
        };
        var snapshot = Snapshot(exactTotal: 1, items: [warningItem]) with
        {
            Snapshot = Identity() with { HistoryEpoch = epoch },
            StoragePressure = new StoragePressureStateSnapshot(
                StoragePressureStatuses.Warning,
                epoch,
                "MesIngest",
                @"D:\SqlData\MesIngest.mdf",
                VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 14.5m),
                DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
                PausedAt: null,
                PauseId: null,
                PauseReason: null,
                RecoveryAuditId: null),
        };

        var presentation = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            WatchCurrentIngestAttentionQueries.StartLatest());

        var warning = Assert.IsType<WatchProtectionDetailPresentation>(
            Assert.Single(presentation.Rows).Protection);
        Assert.Equal(StoragePressureStatuses.Warning, warning.Status);
        Assert.Contains("尚未进入", warning.CurrentReadRestriction, StringComparison.Ordinal);
        Assert.Contains("不会仅因该预警返回 503", warning.CurrentReadRestriction, StringComparison.Ordinal);
        Assert.Contains("不执行 resume-storage-pressure", warning.LocalAdministrationGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("仅限授权管理员", warning.LocalAdministrationGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_without_a_success_snapshot_is_not_presented_as_an_empty_result()
    {
        var workspace = WatchV2WorkspaceState.Reset(
            hostGeneration: 3,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            CurrentAttention = WatchV2ViewState<
                CurrentIngestAttentionSnapshot,
                WatchNoDetail>.Empty(3) with
            {
                RequestGeneration = 1,
                IsRefreshing = true,
                PendingQueryKey = "attention-loading",
            },
        };

        var presentation = WatchCurrentIngestAttentionPresentation.Project(
            workspace,
            WatchCurrentIngestAttentionQueries.StartLatest());

        Assert.False(presentation.HasSnapshot);
        Assert.True(presentation.IsRefreshing);
        Assert.False(presentation.IsStale);
        Assert.True(presentation.IsInfoOpen);
        Assert.Equal(WatchPresentationSeverity.Informational, presentation.InfoSeverity);
        Assert.Equal("正在读取当前接入关注", presentation.InfoTitle);
        Assert.Contains("等待 Host", presentation.InfoMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, presentation.EmptyResultMessage);
        Assert.Empty(presentation.Rows);
        Assert.Equal("尚无当前接入关注快照", presentation.PageSummary);
    }

    [Fact]
    public void Refresh_and_failure_retain_the_last_successful_snapshot_and_committed_conditions()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        var retained = Snapshot(
            exactTotal: 1,
            kinds: [CurrentIngestAttentionKinds.SeriesError],
            severities: [CurrentIngestAttentionSeverities.Error],
            items: [SevenKinds()[4]]);
        var successful = Workspace(retained).CurrentAttention;
        var attempted = WatchCurrentIngestAttentionQueries.StartLatest(
            [CurrentIngestAttentionKinds.PollRunFailure],
            [CurrentIngestAttentionSeverities.Warning],
            pageSize: 200,
            pageNumber: 1);

        var refreshing = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(successful with
            {
                RequestGeneration = 2,
                IsRefreshing = true,
                PendingQueryKey = "attempted-poll-failures",
            }),
            attempted);

        Assert.True(refreshing.HasSnapshot);
        Assert.True(refreshing.IsRefreshing);
        Assert.True(refreshing.IsStale);
        Assert.Equal("正在刷新当前接入关注", refreshing.InfoTitle);
        Assert.Contains("继续显示 Host 快照", refreshing.InfoMessage, StringComparison.Ordinal);
        Assert.Contains(CurrentIngestAttentionKinds.SeriesError, refreshing.HostFilterSummary);
        Assert.Contains(CurrentIngestAttentionKinds.PollRunFailure, refreshing.CurrentQuerySummary);
        Assert.Equal(retained.Snapshot.ProjectionCommitId, refreshing.ProjectionCommitId);
        Assert.Equal(retained.Snapshot.SnapshotAsOf, refreshing.SnapshotAsOf);
        Assert.Equal(
            retained.Items.Select(item => item.StableIdentity),
            refreshing.Rows.Select(row => row.StableIdentity));
        Assert.Equal(string.Empty, refreshing.EmptyResultMessage);

        var canceled = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(successful with
            {
                RequestGeneration = 2,
                IsRefreshing = false,
                PendingQueryKey = "attempted-poll-failures",
            }),
            attempted);

        Assert.True(canceled.HasSnapshot);
        Assert.True(canceled.IsStale);
        Assert.True(canceled.IsInfoOpen);
        Assert.Equal("刷新未提交，已保留上次快照", canceled.InfoTitle);
        Assert.Contains("取消", canceled.InfoMessage, StringComparison.Ordinal);
        Assert.Equal(
            retained.Items.Select(item => item.StableIdentity),
            canceled.Rows.Select(row => row.StableIdentity));
        Assert.Equal(string.Empty, canceled.EmptyResultMessage);

        var failedAt = at.AddMinutes(2);
        var failed = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(successful with
            {
                RequestGeneration = 2,
                PendingQueryKey = "attempted-poll-failures",
                LastFailureAt = failedAt,
                FailedQueryKey = "attempted-poll-failures",
                FailureKind = WatchHostFailureKind.Timeout,
                FailureCode = "ATTENTION_TIMEOUT",
                ErrorMessage = "Host attention request timed out.",
                CorrelationId = "correlation-attention-22",
            }),
            attempted);

        Assert.True(failed.HasSnapshot);
        Assert.False(failed.IsRefreshing);
        Assert.True(failed.IsStale);
        Assert.Equal(WatchPresentationSeverity.Warning, failed.InfoSeverity);
        Assert.Equal("当前接入关注刷新失败，已保留上次快照", failed.InfoTitle);
        Assert.Contains("timed out", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("correlation-attention-22", failed.InfoMessage, StringComparison.Ordinal);
        Assert.Contains("最近失败", failed.ClientAttemptFacts, StringComparison.Ordinal);
        Assert.Equal(retained.Snapshot.ProjectionCommitId, failed.ProjectionCommitId);
        Assert.Equal(retained.Snapshot.SnapshotAsOf, failed.SnapshotAsOf);
        Assert.Equal(
            retained.Items.Select(item => item.StableIdentity),
            failed.Rows.Select(row => row.StableIdentity));
        Assert.Equal(string.Empty, failed.EmptyResultMessage);
    }

    [Fact]
    public void Only_a_successful_zero_snapshot_reports_no_current_attention()
    {
        var zero = Snapshot(exactTotal: 0, pageNumber: 1, totalPages: 0, items: []);

        var successful = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(zero),
            WatchCurrentIngestAttentionQueries.StartLatest());

        Assert.True(successful.HasSnapshot);
        Assert.Equal("精确 0 个当前关注项 · 第 0 / 0 页", successful.PageSummary);
        Assert.Equal(
            "查询成功；Host 在当前已提交条件下精确 0 个当前接入关注项。已结束的需求系列错误仍可在错误检索中查找。",
            successful.EmptyResultMessage);
        Assert.DoesNotContain("系统健康", successful.EmptyResultMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("人工恢复", successful.EmptyResultMessage, StringComparison.Ordinal);
        Assert.Empty(successful.Rows);
        Assert.False(successful.CanGoPrevious);
        Assert.False(successful.CanGoNext);

        var retainedDuringRefresh = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(Workspace(zero).CurrentAttention with
            {
                RequestGeneration = 2,
                IsRefreshing = true,
                PendingQueryKey = "new-attention-query",
            }),
            WatchCurrentIngestAttentionQueries.StartLatest(
                [CurrentIngestAttentionKinds.PollRunFailure]));
        Assert.Equal(string.Empty, retainedDuringRefresh.EmptyResultMessage);
        Assert.Equal("正在刷新当前接入关注", retainedDuringRefresh.InfoTitle);

        var retainedAfterCancellation = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(Workspace(zero).CurrentAttention with
            {
                RequestGeneration = 2,
                PendingQueryKey = "new-attention-query",
            }),
            WatchCurrentIngestAttentionQueries.StartLatest(
                [CurrentIngestAttentionKinds.PollRunFailure]));
        Assert.Equal(string.Empty, retainedAfterCancellation.EmptyResultMessage);
        Assert.Equal("刷新未提交，已保留上次快照", retainedAfterCancellation.InfoTitle);

        var retainedAfterFailure = WatchCurrentIngestAttentionPresentation.Project(
            WorkspaceWithView(Workspace(zero).CurrentAttention with
            {
                RequestGeneration = 2,
                PendingQueryKey = "new-attention-query",
                LastFailureAt = DateTimeOffset.Parse("2026-08-14T05:08:07Z"),
                FailedQueryKey = "new-attention-query",
                FailureKind = WatchHostFailureKind.ServerQuery,
                FailureCode = "ATTENTION_QUERY_FAILED",
                ErrorMessage = "Query failed.",
            }),
            WatchCurrentIngestAttentionQueries.StartLatest(
                [CurrentIngestAttentionKinds.PollRunFailure]));
        Assert.Equal(string.Empty, retainedAfterFailure.EmptyResultMessage);
        Assert.Contains("已保留上次快照", retainedAfterFailure.InfoTitle, StringComparison.Ordinal);

        var hostFailure = WatchV2WorkspaceState.Reset(
            hostGeneration: 4,
            baseUrl: "http://host-b",
            WatchHostConnectionStatus.Failed) with
        {
            FailureKind = WatchHostFailureKind.Network,
            FailureCode = "HOST_OFFLINE",
            ErrorMessage = "Host is offline.",
            CorrelationId = "correlation-host-22",
        };
        var failed = WatchCurrentIngestAttentionPresentation.Project(
            hostFailure,
            WatchCurrentIngestAttentionQueries.StartLatest());

        Assert.False(failed.HasSnapshot);
        Assert.Equal("无法连接 Host", failed.InfoTitle);
        Assert.Equal(WatchPresentationSeverity.Error, failed.InfoSeverity);
        Assert.Equal(string.Empty, failed.EmptyResultMessage);
    }

    [Fact]
    public void English_projection_localizes_attention_meanings_while_preserving_technical_facts_and_offset_times()
    {
        var snapshot = Snapshot(items: SevenKinds());

        var chinese = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            WatchCurrentIngestAttentionQueries.StartLatest(),
            WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese));
        var english = WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            WatchCurrentIngestAttentionQueries.StartLatest(),
            WatchTextCatalog.For(WatchDisplayLanguage.English));

        var chineseSeries = Assert.Single(chinese.Rows, row => row.Kind == CurrentIngestAttentionKinds.SeriesError);
        var englishSeries = Assert.Single(english.Rows, row => row.Kind == CurrentIngestAttentionKinds.SeriesError);
        Assert.NotEqual(chineseSeries.KindLabel, englishSeries.KindLabel);
        Assert.Contains("Active series error", englishSeries.KindLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CurrentIngestAttentionKinds.SeriesError, englishSeries.KindLabel, StringComparison.Ordinal);
        Assert.Contains("REQUIRED_MES_FIELD_MISSING", englishSeries.SubjectSummary, StringComparison.Ordinal);
        Assert.Contains("Required MES field", englishSeries.SubjectSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("series-22", englishSeries.SubjectSummary, StringComparison.Ordinal);
        Assert.Equal("2026-08-14 05:08:07 +00:00", englishSeries.OccurredAt);
        Assert.Equal(chineseSeries.StableIdentity, englishSeries.StableIdentity);
        Assert.Equal(chineseSeries.Evidence.PollTraceId, englishSeries.Evidence.PollTraceId);
        Assert.Equal(chineseSeries.Evidence.DemandId, englishSeries.Evidence.DemandId);
        Assert.Equal(chineseSeries.WorkType, englishSeries.WorkType);
    }

    [Fact]
    public void Unknown_attention_kind_is_neutral_and_preserves_the_raw_code_without_known_guidance()
    {
        const string unknownKind = "FUTURE_ATTENTION_KIND_08";
        var item = SevenKinds()[0] with
        {
            Kind = unknownKind,
            Severity = "FUTURE_SEVERITY_08",
            StableIdentity = "future-attention-08",
            ErrorCode = "FUTURE_STATUS_08",
        };
        var snapshot = Snapshot(exactTotal: 1, items: [item]);

        var row = Assert.Single(WatchCurrentIngestAttentionPresentation.Project(
            Workspace(snapshot),
            WatchCurrentIngestAttentionQueries.StartLatest(),
            WatchTextCatalog.For(WatchDisplayLanguage.English)).Rows);

        Assert.Contains("unknown", row.KindLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unknownKind, row.KindLabel, StringComparison.Ordinal);
        Assert.Equal(WatchPresentationSeverity.Informational, row.SeverityStyle);
        Assert.Null(row.Protection);
        Assert.Null(row.ErrorSearchDrill);
        Assert.DoesNotContain("resume-storage-pressure", row.SubjectSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static WatchV2WorkspaceState Workspace(CurrentIngestAttentionSnapshot snapshot) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: 3,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            CurrentAttention = WatchV2ViewState<
                CurrentIngestAttentionSnapshot,
                WatchNoDetail>.Empty(3) with
            {
                RequestGeneration = 1,
                PendingQueryKey = "attention-committed",
                CommittedQueryKey = "attention-committed",
                Snapshot = snapshot,
                LastSuccessfulAt = DateTimeOffset.Parse("2026-08-14T05:06:09Z"),
            },
        };

    private static WatchV2WorkspaceState WorkspaceWithView(
        WatchV2ViewState<CurrentIngestAttentionSnapshot, WatchNoDetail> view) =>
        WatchV2WorkspaceState.Reset(
            hostGeneration: view.HostGeneration,
            baseUrl: "http://host-a",
            WatchHostConnectionStatus.Connected) with
        {
            CurrentAttention = view,
        };

    private static CurrentIngestAttentionSnapshot Snapshot(
        long exactTotal = 7,
        int pageNumber = 1,
        int totalPages = 1,
        IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? severities = null,
        IReadOnlyList<CurrentIngestAttentionItemSnapshot>? items = null) => new(
        Identity(),
        exactTotal,
        new CurrentIngestAttentionFacets(
            CurrentIngestAttentionKinds.All
                .Select(kind => new CurrentIngestAttentionFacetSnapshot(kind, 1))
                .ToArray(),
            [
                new(CurrentIngestAttentionSeverities.Error, 6),
                new(CurrentIngestAttentionSeverities.Warning, 1),
            ]),
        CurrentIngestAttentionOrder.Default,
        PageSize: 100,
        PageNumber: pageNumber,
        TotalPages: totalPages,
        Kinds: kinds ?? [],
        Severities: severities ?? [],
        Items: items ?? SevenKinds());

    private static CurrentIngestAttentionItemSnapshot[] SevenKinds()
    {
        var at = DateTimeOffset.Parse("2026-08-14T05:06:07Z");
        return
        [
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.HistoryReset,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(6),
                "HISTORY_RESET:33333333-3333-3333-3333-333333333333",
                SeriesId: null,
                WorkType: null,
                ErrorCode: HistoryResetStatuses.AcknowledgementRequired,
                Target: "MesIngest",
                SubjectKind: "HISTORY_EPOCH",
                new CurrentIngestAttentionEvidenceSnapshot(
                    Phase: HistoryResetStatuses.AcknowledgementRequired,
                    FailureReason: "prior history and tombstones are unrecoverable",
                    DatabaseName: "MesIngest"),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    AttentionKinds: [CurrentIngestAttentionKinds.HistoryReset],
                    AttentionSeverities: [CurrentIngestAttentionSeverities.Error])),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.StoragePressure,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(5),
                "STORAGE_PRESSURE",
                SeriesId: null,
                WorkType: null,
                ErrorCode: StoragePressureStatuses.Paused,
                Target: "MesIngest",
                SubjectKind: "DATABASE_VOLUME",
                new CurrentIngestAttentionEvidenceSnapshot(
                    EvidenceId: "pause-22",
                    Phase: StoragePressureStatuses.Paused,
                    FailureReason: "low space",
                    DatabaseName: "MesIngest",
                    VolumeRoot: @"D:\",
                    AvailablePercent: 9.5m),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    AttentionKinds: [CurrentIngestAttentionKinds.StoragePressure],
                    AttentionSeverities: [CurrentIngestAttentionSeverities.Error])),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.HistoryCleanupFailure,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(4),
                "HISTORY_CLEANUP_FAILURE",
                SeriesId: null,
                WorkType: null,
                ErrorCode: HistoryCleanupFailureCodes.BatchFailed,
                Target: null,
                SubjectKind: "HISTORY_CLEANUP",
                new CurrentIngestAttentionEvidenceSnapshot(
                    EvidenceId: "cleanup-run-22",
                    Phase: HistoryCleanupRunStatuses.Failed,
                    FailureReason: nameof(InvalidOperationException),
                    NextCheckAt: at.AddHours(1)),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.CurrentIngestAttention,
                    AttentionKinds: [CurrentIngestAttentionKinds.HistoryCleanupFailure],
                    AttentionSeverities: [CurrentIngestAttentionSeverities.Error])),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.PollRunFailure,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(3),
                "POLL_RUN_FAILURE",
                SeriesId: null,
                WorkType: null,
                ErrorCode: null,
                Target: "POLL_TRACE:poll-failure-22",
                SubjectKind: "POLL_TRACE",
                new CurrentIngestAttentionEvidenceSnapshot(
                    ProjectionCommitId: "commit-attention-22",
                    ProjectionSequence: 422,
                    PollTraceId: "poll-failure-22",
                    PollTraceSequence: 44,
                    Outcome: "INCOMPLETE"),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.PollTrace,
                    PollTraceId: "poll-failure-22")),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.SeriesError,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(2),
                "series-22:REQUIRED_MES_FIELD_MISSING:DEMAND:demand-22:EQP",
                SeriesId: "series-22",
                WorkType: "WIRE_TO_GATE",
                ErrorCode: "REQUIRED_MES_FIELD_MISSING",
                Target: "DEMAND:demand-22",
                SubjectKind: "EQP",
                new CurrentIngestAttentionEvidenceSnapshot(
                    ProjectionCommitId: "commit-series-22",
                    ProjectionSequence: 420,
                    PollTraceId: "poll-series-22",
                    PollTraceSequence: 42,
                    SeriesId: "series-22",
                    DemandId: "demand-22",
                    WorkType: "WIRE_TO_GATE",
                    EvidenceId: "evidence-series-22"),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.ErrorSearch,
                    PageNumber: 1,
                    ErrorActivityStates: [ErrorSearchActivityStates.Active],
                    ErrorWindow: ErrorSearchWindowKinds.AllHistory,
                    SeriesId: "series-22")),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.UnassignedMesObservation,
                CurrentIngestAttentionSeverities.Error,
                at.AddMinutes(1),
                "UNASSIGNED_MES_OBSERVATION:poll-unassigned-22:7",
                SeriesId: null,
                WorkType: null,
                ErrorCode: null,
                Target: "RAW_OBSERVATION:7",
                SubjectKind: "RAW_OBSERVATION",
                new CurrentIngestAttentionEvidenceSnapshot(
                    ProjectionCommitId: "commit-attention-22",
                    ProjectionSequence: 422,
                    PollTraceId: "poll-unassigned-22",
                    PollTraceSequence: 45,
                    ObservationOrdinal: 7,
                    ContentDigest: "digest-unassigned-22"),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.PollTrace,
                    PollTraceId: "poll-unassigned-22")),
            new CurrentIngestAttentionItemSnapshot(
                CurrentIngestAttentionKinds.TaskTypeProtection,
                CurrentIngestAttentionSeverities.Warning,
                at,
                "TASK_TYPE_PROTECTION:WIRE_TO_NITROGEN",
                SeriesId: null,
                WorkType: "WIRE_TO_NITROGEN",
                ErrorCode: null,
                Target: "WORK_TYPE:WIRE_TO_NITROGEN",
                SubjectKind: "WORK_TYPE",
                new CurrentIngestAttentionEvidenceSnapshot(
                    ProjectionCommitId: "commit-attention-22",
                    ProjectionSequence: 422,
                    PollTraceId: "poll-protection-22",
                    PollTraceSequence: 43,
                    WorkType: "WIRE_TO_NITROGEN",
                    EvidenceId: "protection-event-22",
                    Phase: "RECOVERING"),
                new OverviewNavigationIntent(
                    OverviewNavigationTargets.TaskTypeProtection,
                    WorkType: "WIRE_TO_NITROGEN")),
        ];
    }

    private static OperationalSnapshotIdentity Identity() => new(
        "commit-attention-22",
        ProjectionSequence: 422,
        DateTimeOffset.Parse("2026-08-14T05:06:07Z"),
        "poll-attention-22",
        PollTraceHighWater: 45,
        CatalogRevision: 9,
        DateTimeOffset.Parse("2026-08-14T05:06:08Z"));

    private static string DisplayTime(DateTimeOffset value) =>
        WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).FormatAbsoluteTime(value);
}
