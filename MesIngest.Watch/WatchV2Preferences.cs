using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Watch;

/// <summary>
/// Local-only presentation preferences for the production V2 shell. Host
/// identity, credentials, queries, and snapshots deliberately do not belong in
/// this record.
/// </summary>
internal sealed record WatchV2DisplayPreferences
{
    public const double DefaultWindowWidth = 1440;
    public const double DefaultWindowHeight = 900;
    public const double MinimumWindowWidth = 720;
    public const double MinimumWindowHeight = 600;

    private const double MaximumWindowWidth = 7680;
    private const double MaximumWindowHeight = 4320;

    public WatchV2DisplayPreferences(
        bool rememberWindowSize = true,
        double windowWidth = DefaultWindowWidth,
        double windowHeight = DefaultWindowHeight,
        bool isNavigationPaneOpen = false,
        WatchWindowLayout? mainWindowLayout = null,
        WatchWindowLayout? inspectorWindowLayout = null)
    {
        if (!IsValidWindowSize(windowWidth, windowHeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowWidth),
                $"Window geometry must be finite and within {MinimumWindowWidth}x{MinimumWindowHeight} and {MaximumWindowWidth}x{MaximumWindowHeight}.");
        }

        RememberWindowSize = rememberWindowSize;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        IsNavigationPaneOpen = isNavigationPaneOpen;
        MainWindowLayout = mainWindowLayout;
        InspectorWindowLayout = inspectorWindowLayout;
    }

    public static WatchV2DisplayPreferences Default { get; } = new();

    public bool RememberWindowSize { get; }

    public bool RememberWindowLayout => RememberWindowSize;

    public double WindowWidth { get; }

    public double WindowHeight { get; }

    public bool IsNavigationPaneOpen { get; }

    public WatchWindowLayout? MainWindowLayout { get; }

    public WatchWindowLayout? InspectorWindowLayout { get; }

    public WatchV2DisplayPreferences WithInspectorWindowLayout(WatchWindowLayout layout) => new(
        RememberWindowLayout,
        WindowWidth,
        WindowHeight,
        IsNavigationPaneOpen,
        MainWindowLayout,
        layout);

    private static bool IsValidWindowSize(double width, double height) =>
        double.IsFinite(width)
        && double.IsFinite(height)
        && width is >= MinimumWindowWidth and <= MaximumWindowWidth
        && height is >= MinimumWindowHeight and <= MaximumWindowHeight;
}

internal sealed record WatchV2Preferences(
    WatchV2AutoRefreshSettings RefreshIntervals,
    WatchV2DisplayPreferences Display)
{
    public static WatchV2Preferences Default { get; } = new(
        WatchV2AutoRefreshSettings.Default,
        WatchV2DisplayPreferences.Default);
}

/// <summary>
/// Versioned, per-user storage for V2 refresh intervals and display choices.
/// The document intentionally has no connection or credential fields.
/// </summary>
internal static class WatchV2PreferencesStore
{
    // Version 1 was the legacy four-view auto-refresh document with Enabled
    // switches. Version 2 is intentionally incompatible and falls back safely.
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngest.Watch",
        "watch-v2-preferences.json");

    public static WatchV2Preferences Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path))
            {
                return WatchV2Preferences.Default;
            }

            var document = JsonSerializer.Deserialize<PreferencesDocument>(
                File.ReadAllText(path),
                JsonOptions);
            return document is not null
                && document.Version == CurrentVersion
                && document.IntervalSeconds is not null
                && document.Display is not null
                ? document.ToPreferences()
                : WatchV2Preferences.Default;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return WatchV2Preferences.Default;
        }
    }

    public static void Save(string path, WatchV2Preferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);
        Validate(preferences);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var persistedPreferences = preferences.Display.RememberWindowSize
                ? preferences
                : preferences with
                {
                    Display = new WatchV2DisplayPreferences(
                        rememberWindowSize: false,
                        isNavigationPaneOpen: preferences.Display.IsNavigationPaneOpen),
                };
            var document = PreferencesDocument.From(persistedPreferences);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            // Both files live in the same directory/volume. Replacing the
            // directory entry prevents readers from observing a partial JSON
            // document when an existing preference file is updated.
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(WatchV2Preferences preferences)
    {
        if (preferences.RefreshIntervals is null || preferences.Display is null)
        {
            throw new ArgumentException(
                "V2 preferences must include refresh intervals and display preferences.",
                nameof(preferences));
        }

        foreach (var view in Enum.GetValues<WatchV2DataView>())
        {
            var setting = preferences.RefreshIntervals.For(view);
            if (setting is null
                || !WatchV2AutoRefreshSetting.AllowedIntervals.Contains(setting.IntervalSeconds))
            {
                throw new ArgumentException(
                    "V2 preferences contain an unsupported refresh interval.",
                    nameof(preferences));
            }
        }
    }

    private sealed record PreferencesDocument(
        int Version,
        IntervalSecondsDocument? IntervalSeconds,
        DisplayDocument? Display)
    {
        public WatchV2Preferences ToPreferences() => new(
            new WatchV2AutoRefreshSettings(
                new WatchV2AutoRefreshSetting(IntervalSeconds!.Overview),
                new WatchV2AutoRefreshSetting(IntervalSeconds.DemandSeries),
                new WatchV2AutoRefreshSetting(IntervalSeconds.ReadabilityAudit),
                new WatchV2AutoRefreshSetting(IntervalSeconds.ErrorSearch),
                new WatchV2AutoRefreshSetting(IntervalSeconds.CurrentIngestAttention)),
            new WatchV2DisplayPreferences(
                Display!.RememberWindowSize,
                Display.WindowWidth,
                Display.WindowHeight,
                Display.IsNavigationPaneOpen,
                Display.MainWindowLayout,
                Display.InspectorWindowLayout));

        public static PreferencesDocument From(WatchV2Preferences preferences) => new(
            CurrentVersion,
            IntervalSecondsDocument.From(preferences.RefreshIntervals),
            DisplayDocument.From(preferences.Display));
    }

    private sealed record IntervalSecondsDocument(
        int Overview,
        int DemandSeries,
        int ReadabilityAudit,
        int ErrorSearch,
        int CurrentIngestAttention)
    {
        public static IntervalSecondsDocument From(WatchV2AutoRefreshSettings settings) => new(
            settings.Overview.IntervalSeconds,
            settings.DemandSeries.IntervalSeconds,
            settings.ReadabilityAudit.IntervalSeconds,
            settings.ErrorSearch.IntervalSeconds,
            settings.CurrentIngestAttention.IntervalSeconds);
    }

    private sealed record DisplayDocument(
        bool RememberWindowSize,
        double WindowWidth,
        double WindowHeight,
        bool IsNavigationPaneOpen,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        WatchWindowLayout? MainWindowLayout = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        WatchWindowLayout? InspectorWindowLayout = null)
    {
        public static DisplayDocument From(WatchV2DisplayPreferences display) => new(
            display.RememberWindowSize,
            display.WindowWidth,
            display.WindowHeight,
            display.IsNavigationPaneOpen,
            display.RememberWindowLayout ? display.MainWindowLayout : null,
            display.RememberWindowLayout ? display.InspectorWindowLayout : null);
    }
}
