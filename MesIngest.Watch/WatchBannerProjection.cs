namespace MesIngest.Watch;

/// <summary>
/// Mutable hold bookkeeping for min-visible banner duration and brief recovery text.
/// </summary>
internal sealed record WatchBannerHoldState(
    string? HeldErrorKey,
    string? HeldErrorMessage,
    DateTimeOffset? ErrorShownAt,
    DateTimeOffset? ErrorClearedAt,
    string? HeldWarningKey,
    string? HeldWarningMessage,
    DateTimeOffset? WarningShownAt,
    DateTimeOffset? WarningClearedAt,
    DateTimeOffset? RecoveredAt)
{
    public static WatchBannerHoldState Empty { get; } = new(
        null, null, null, null, null, null, null, null, null);
}

internal sealed record WatchBannerProjection(
    bool ShowError,
    string? ErrorSeverity,
    string? ErrorMessage,
    bool ShowWarning,
    string? WarningSeverity,
    string? WarningMessage,
    string? RecoveryMessage,
    WatchBannerHoldState HoldState)
{
    public static readonly TimeSpan DefaultMinHold = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RecoveryDisplay = TimeSpan.FromSeconds(5);

    public static WatchBannerProjection Project(
        WatchBannerHoldState previous,
        WatchPollHealthDto? health,
        string? fetchError,
        IReadOnlyList<WatchAlertDto> alerts,
        DateTimeOffset now,
        TimeSpan? minHold = null)
    {
        var hold = minHold ?? DefaultMinHold;
        var current = WatchBannerState.From(health, fetchError, alerts);

        var (showError, errorMessage, errorHold, errorJustRecovered) = AdvanceLane(
            previous.HeldErrorKey,
            previous.HeldErrorMessage,
            previous.ErrorShownAt,
            previous.ErrorClearedAt,
            current.ErrorKey,
            current.ErrorMessage,
            now,
            hold);

        var (showWarning, warningMessage, warningHold, warningJustRecovered) = AdvanceLane(
            previous.HeldWarningKey,
            previous.HeldWarningMessage,
            previous.WarningShownAt,
            previous.WarningClearedAt,
            current.WarningKey,
            current.WarningMessage,
            now,
            hold);

        DateTimeOffset? recoveredAt = previous.RecoveredAt;
        if ((errorJustRecovered || warningJustRecovered) && !showError && !showWarning)
        {
            recoveredAt = now;
        }
        else if (showError || showWarning)
        {
            recoveredAt = null;
        }
        else if (recoveredAt is { } at && now - at >= RecoveryDisplay)
        {
            recoveredAt = null;
        }

        var recovery = recoveredAt is not null ? "已恢复" : null;

        var nextHold = new WatchBannerHoldState(
            HeldErrorKey: errorHold.Key,
            HeldErrorMessage: errorHold.Message,
            ErrorShownAt: errorHold.ShownAt,
            ErrorClearedAt: errorHold.ClearedAt,
            HeldWarningKey: warningHold.Key,
            HeldWarningMessage: warningHold.Message,
            WarningShownAt: warningHold.ShownAt,
            WarningClearedAt: warningHold.ClearedAt,
            RecoveredAt: recoveredAt);

        return new WatchBannerProjection(
            ShowError: showError,
            ErrorSeverity: showError ? "ERROR" : null,
            ErrorMessage: showError ? errorMessage : null,
            ShowWarning: showWarning,
            WarningSeverity: showWarning ? "WARNING" : null,
            WarningMessage: showWarning ? warningMessage : null,
            RecoveryMessage: recovery,
            HoldState: nextHold);
    }

    private static (bool Show, string? Message, LaneHold Hold, bool JustRecovered) AdvanceLane(
        string? prevKey,
        string? prevMessage,
        DateTimeOffset? shownAt,
        DateTimeOffset? clearedAt,
        string? currentKey,
        string? currentMessage,
        DateTimeOffset now,
        TimeSpan minHold)
    {
        if (currentKey is not null)
        {
            var same = string.Equals(prevKey, currentKey, StringComparison.Ordinal);
            return (
                true,
                currentMessage,
                new LaneHold(
                    currentKey,
                    currentMessage,
                    same ? shownAt ?? now : now,
                    ClearedAt: null),
                JustRecovered: false);
        }

        if (prevKey is null)
        {
            return (false, null, LaneHold.Empty, JustRecovered: false);
        }

        var effectiveClearedAt = clearedAt ?? now;
        var effectiveShownAt = shownAt ?? effectiveClearedAt;
        if (now - effectiveShownAt < minHold)
        {
            return (
                true,
                prevMessage,
                new LaneHold(prevKey, prevMessage, effectiveShownAt, effectiveClearedAt),
                JustRecovered: false);
        }

        // Transitioning from held/visible to hidden — emit recovery once.
        return (false, null, LaneHold.Empty, JustRecovered: true);
    }

    private readonly record struct LaneHold(
        string? Key,
        string? Message,
        DateTimeOffset? ShownAt,
        DateTimeOffset? ClearedAt)
    {
        public static LaneHold Empty => new(null, null, null, null);
    }
}
