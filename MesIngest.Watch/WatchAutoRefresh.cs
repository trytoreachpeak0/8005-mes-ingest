using System.IO;
using System.Text.Json;

namespace MesIngest.Watch;

internal enum WatchRefreshView
{
    Overview,
    Visible,
    Gone,
    Alerts,
}

internal sealed record WatchAutoRefreshSetting
{
    public static IReadOnlyList<int> AllowedIntervals { get; } = [10, 30, 60, 300];

    public WatchAutoRefreshSetting(bool enabled = false, int intervalSeconds = 10)
    {
        if (!AllowedIntervals.Contains(intervalSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalSeconds),
                intervalSeconds,
                "Auto-refresh interval must be 10, 30, 60, or 300 seconds.");
        }

        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
    }

    public bool Enabled { get; }
    public int IntervalSeconds { get; }
}

internal sealed record WatchAutoRefreshPreferences(
    WatchAutoRefreshSetting Overview,
    WatchAutoRefreshSetting Visible,
    WatchAutoRefreshSetting Gone,
    WatchAutoRefreshSetting Alerts)
{
    public static WatchAutoRefreshPreferences Default { get; } = new(
        new(),
        new(),
        new(),
        new());

    public WatchAutoRefreshSetting For(WatchRefreshView view) => view switch
    {
        WatchRefreshView.Overview => Overview,
        WatchRefreshView.Visible => Visible,
        WatchRefreshView.Gone => Gone,
        WatchRefreshView.Alerts => Alerts,
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
    };

    public WatchAutoRefreshPreferences With(
        WatchRefreshView view,
        WatchAutoRefreshSetting setting) => view switch
        {
            WatchRefreshView.Overview => this with { Overview = setting },
            WatchRefreshView.Visible => this with { Visible = setting },
            WatchRefreshView.Gone => this with { Gone = setting },
            WatchRefreshView.Alerts => this with { Alerts = setting },
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
        };
}

internal static class WatchAutoRefreshPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngestWatch",
        "auto-refresh.json");

    public static WatchAutoRefreshPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return WatchAutoRefreshPreferences.Default;
            }

            var document = JsonSerializer.Deserialize<PreferencesDocument>(
                File.ReadAllText(path),
                JsonOptions);
            return document is null
                || document.Overview is null
                || document.Visible is null
                || document.Gone is null
                || document.Alerts is null
                ? WatchAutoRefreshPreferences.Default
                : new WatchAutoRefreshPreferences(
                    document.Overview.ToSetting(),
                    document.Visible.ToSetting(),
                    document.Gone.ToSetting(),
                    document.Alerts.ToSetting());
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return WatchAutoRefreshPreferences.Default;
        }
    }

    public static void Save(string path, WatchAutoRefreshPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new PreferencesDocument(
            PreferenceDocument.From(preferences.Overview),
            PreferenceDocument.From(preferences.Visible),
            PreferenceDocument.From(preferences.Gone),
            PreferenceDocument.From(preferences.Alerts));
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private sealed record PreferencesDocument(
        PreferenceDocument? Overview,
        PreferenceDocument? Visible,
        PreferenceDocument? Gone,
        PreferenceDocument? Alerts);

    private sealed record PreferenceDocument(bool Enabled, int IntervalSeconds)
    {
        public WatchAutoRefreshSetting ToSetting() => new(Enabled, IntervalSeconds);

        public static PreferenceDocument From(WatchAutoRefreshSetting setting) =>
            new(setting.Enabled, setting.IntervalSeconds);
    }
}

/// <summary>
/// Owns the per-view auto-refresh clock. Requests themselves remain owned by the
/// view sessions; this schedule only decides when automatic work may begin.
/// </summary>
internal sealed class WatchAutoRefreshSchedule
{
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<WatchRefreshView, DateTimeOffset?> _nextDue = [];
    private readonly Dictionary<WatchRefreshView, long> _requestGenerations = [];
    private WatchAutoRefreshPreferences _preferences;

    public WatchAutoRefreshSchedule(
        WatchAutoRefreshPreferences preferences,
        TimeProvider? timeProvider = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _timeProvider = timeProvider ?? TimeProvider.System;
        foreach (var view in Enum.GetValues<WatchRefreshView>())
        {
            _nextDue[view] = null;
            _requestGenerations[view] = 0;
        }
    }

    public WatchRefreshView? ActiveView { get; private set; }
    public WatchAutoRefreshPreferences Preferences => _preferences;

    public bool Activate(WatchRefreshView view)
    {
        var changed = ActiveView != view;
        ActiveView = view;
        _nextDue[view] = null;
        return changed && _preferences.For(view).Enabled;
    }

    public void Deactivate()
    {
        ActiveView = null;
    }

    public void Update(WatchRefreshView view, WatchAutoRefreshSetting setting)
    {
        _preferences = _preferences.With(view, setting);
        _nextDue[view] = ActiveView == view && setting.Enabled
            ? _timeProvider.GetUtcNow().AddSeconds(setting.IntervalSeconds)
            : null;
    }

    public long BeginRequest(WatchRefreshView view)
    {
        var generation = _requestGenerations[view] + 1;
        _requestGenerations[view] = generation;
        _nextDue[view] = null;
        return generation;
    }

    public void EndRequest(WatchRefreshView view, long generation)
    {
        if (_requestGenerations[view] != generation)
        {
            return;
        }

        ScheduleNext(view);
    }

    public void CancelCurrentRequest(WatchRefreshView view)
    {
        _requestGenerations[view]++;
        ScheduleNext(view);
    }

    public bool TryTakeDue(WatchRefreshView view, bool requestInProgress)
    {
        if (requestInProgress
            || ActiveView != view
            || !_preferences.For(view).Enabled
            || _nextDue[view] is not { } due
            || _timeProvider.GetUtcNow() < due)
        {
            return false;
        }

        _nextDue[view] = null;
        return true;
    }

    private void ScheduleNext(WatchRefreshView view)
    {
        var setting = _preferences.For(view);
        _nextDue[view] = ActiveView == view && setting.Enabled
            ? _timeProvider.GetUtcNow().AddSeconds(setting.IntervalSeconds)
            : null;
    }
}
