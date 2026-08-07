namespace MesIngest.Watch;

internal sealed record WatchOverviewProjectionModel(
    string ConclusionText,
    string PollHealthText,
    string AlertsText,
    string DemandsText);

internal static class WatchOverviewProjection
{
    public static WatchOverviewProjectionModel Project(
        WatchHostSessionState host,
        WatchOverviewState overview)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(overview);

        return new WatchOverviewProjectionModel(
            Conclusion(host, overview),
            PollHealth(overview.PollHealth),
            Alerts(overview.Alerts),
            Demands(overview.Demands));
    }

    private static string Conclusion(
        WatchHostSessionState host,
        WatchOverviewState overview)
    {
        if (host.Status == WatchHostConnectionStatus.Failed)
        {
            return host.FailureKind == WatchHostFailureKind.Contract
                ? "✕ 契约不兼容"
                : "✕ 连接失败";
        }

        if (overview.AllResourcesFailed)
        {
            return "✕ 连接失败";
        }

        if (overview.IsPartialFailure)
        {
            return "△ 部分失败";
        }

        if (host.Status is WatchHostConnectionStatus.Connecting or WatchHostConnectionStatus.NotConfigured)
        {
            return "○ 正在连接";
        }

        var health = overview.PollHealth.Value;
        if (health is null)
        {
            return "○ 尚无轮询";
        }

        if (!health.Success)
        {
            return "✕ 最近轮询失败";
        }

        if (health.TaskTypePauses.Any(pause => pause.PausedZeroDrop))
        {
            return "⏸ PausedZeroDrop";
        }

        if (overview.Alerts.Items.Any(alert =>
                string.Equals(alert.Severity, "ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            return "✕ 存在活动 ERROR";
        }

        if (overview.Alerts.Items.Count > 0)
        {
            return "△ 仅有 WARNING";
        }

        return "✓ 健康";
    }

    private static string PollHealth(WatchOverviewPollHealthState card)
    {
        var health = card.Value;
        var value = health is null
            ? "尚无成功轮询"
            : $"outcome={health.Outcome} · endedAt={WatchTimeDisplay.Format(health.EndedAt)}"
              + $" · durationMs={health.DurationMs} · 最近 MES 快照行数={health.RowCount}"
              + (string.IsNullOrWhiteSpace(health.FailureStage)
                  ? string.Empty
                  : $" · failureStage={health.FailureStage}")
              + FormatPausedTaskTypes(health.TaskTypePauses);
        return value + CardFreshness(card.LastSuccessfulAt, card.IsStale, card.Error);
    }

    private static string Alerts(WatchOverviewAlertState card)
    {
        var top = card.Items
            .Take(3)
            .Select(alert =>
                $"{alert.Code} · {AlertScope(alert)} · {FormatTime(alert.LastSeenAt)}")
            .ToArray();
        var details = top.Length == 0 ? "当前无活动告警" : string.Join(Environment.NewLine, top);
        return $"活动告警：{card.CountLabel}{Environment.NewLine}{details}"
               + CardFreshness(card.LastSuccessfulAt, card.IsStale, card.Error);
    }

    private static string Demands(WatchOverviewDemandState card) =>
        $"VISIBLE TransportDemand：{card.CountLabel}{Environment.NewLine}{card.TaskTypeSummary}"
        + CardFreshness(card.LastSuccessfulAt, card.IsStale, card.Error);

    private static string FormatPausedTaskTypes(IReadOnlyList<WatchTaskTypePauseDto> pauses)
    {
        var taskTypes = pauses
            .Where(pause => pause.PausedZeroDrop)
            .Select(pause => pause.TaskType)
            .ToArray();
        return taskTypes.Length == 0
            ? string.Empty
            : $" · 暂停 TASK_TYPE：{string.Join("、", taskTypes)}";
    }

    private static string AlertScope(WatchAlertDto alert)
    {
        var parts = new[] { alert.TaskType, alert.Sublot, alert.DemandId }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var scope = string.Join(" / ", parts);
        return string.IsNullOrEmpty(scope) ? "全局" : scope;
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value is null ? "时间未知" : WatchTimeDisplay.Format(value.Value);

    private static string CardFreshness(
        DateTimeOffset? lastSuccessfulAt,
        bool isStale,
        string? error)
    {
        var lastSuccess = lastSuccessfulAt is null
            ? "尚无成功读取"
            : $"最后成功：{WatchTimeDisplay.Format(lastSuccessfulAt.Value)}";
        var state = isStale ? "陈旧" : "最新";
        var errorText = string.IsNullOrWhiteSpace(error) ? string.Empty : $" · {error}";
        return $"{Environment.NewLine}{state} · {lastSuccess}{errorText}";
    }
}
