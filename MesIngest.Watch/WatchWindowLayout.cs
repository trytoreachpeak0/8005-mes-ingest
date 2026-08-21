using System.Runtime.InteropServices;

namespace MesIngest.Watch;

internal sealed record WatchWindowLayout(
    double Left,
    double Top,
    double Width,
    double Height,
    string? MonitorDeviceName,
    bool Maximized);

internal sealed record WatchDisplayWorkArea(
    string DeviceName,
    Rect Bounds,
    bool IsPrimary);

internal static class WatchWindowLayoutService
{
    private const double MinimumVisibleLength = 1;

    internal static WatchWindowLayout Apply(
        Window window,
        WatchWindowLayout? requested,
        double defaultWidth,
        double defaultHeight,
        double minimumWidth,
        double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        var applied = Resolve(
            requested,
            GetDisplayWorkAreas(),
            defaultWidth,
            defaultHeight,
            minimumWidth,
            minimumHeight);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.Left = applied.Left;
        window.Top = applied.Top;
        window.Width = applied.Width;
        window.Height = applied.Height;
        if (applied.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        return applied;
    }

    internal static WatchWindowLayout Capture(
        Window window,
        bool restoreToMaximized = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (!IsUsable(bounds))
        {
            bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
        }

        var workAreas = GetDisplayWorkAreas();
        var monitor = FindBestWorkArea(bounds, workAreas)
            ?? workAreas.FirstOrDefault(area => area.IsPrimary)
            ?? workAreas[0];
        return new WatchWindowLayout(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            monitor.DeviceName,
            window.WindowState == WindowState.Maximized || restoreToMaximized);
    }

    internal static WatchWindowLayout Resolve(
        WatchWindowLayout? requested,
        IReadOnlyList<WatchDisplayWorkArea> workAreas,
        double defaultWidth,
        double defaultHeight,
        double minimumWidth,
        double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            throw new ArgumentException("At least one visible work area is required.", nameof(workAreas));
        }

        var primary = workAreas.FirstOrDefault(area => area.IsPrimary) ?? workAreas[0];
        var namedMonitor = string.IsNullOrWhiteSpace(requested?.MonitorDeviceName)
            ? null
            : workAreas.FirstOrDefault(area => string.Equals(
                area.DeviceName,
                requested.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
        var monitorMissing = requested is not null
            && !string.IsNullOrWhiteSpace(requested.MonitorDeviceName)
            && namedMonitor is null;
        var target = namedMonitor ?? primary;
        var work = target.Bounds;

        var requestedBounds = requested is null
            ? Rect.Empty
            : new Rect(
                FiniteOrZero(requested.Left),
                FiniteOrZero(requested.Top),
                PositiveFiniteOrDefault(requested.Width, defaultWidth),
                PositiveFiniteOrDefault(requested.Height, defaultHeight));
        var isLegacySizeOnly = requested is not null
            && string.IsNullOrWhiteSpace(requested.MonitorDeviceName)
            && (!double.IsFinite(requested.Left) || !double.IsFinite(requested.Top));
        var invalidCoordinates = requested is not null
            && !isLegacySizeOnly
            && (!double.IsFinite(requested.Left)
                || !double.IsFinite(requested.Top)
                || (!monitorMissing && !HasVisibleIntersection(requestedBounds, work)));
        var sourceWidth = invalidCoordinates
            ? defaultWidth
            : PositiveFiniteOrDefault(requested?.Width, defaultWidth);
        var sourceHeight = invalidCoordinates
            ? defaultHeight
            : PositiveFiniteOrDefault(requested?.Height, defaultHeight);
        var width = ClampLength(sourceWidth, minimumWidth, work.Width);
        var height = ClampLength(sourceHeight, minimumHeight, work.Height);

        double left;
        double top;
        if (requested is null || isLegacySizeOnly || invalidCoordinates || monitorMissing)
        {
            left = work.Left + Math.Max(0, (work.Width - width) / 2);
            top = work.Top + Math.Max(0, (work.Height - height) / 2);
        }
        else
        {
            left = Math.Clamp(requested.Left, work.Left, work.Right - width);
            top = Math.Clamp(requested.Top, work.Top, work.Bottom - height);
        }

        return new WatchWindowLayout(
            left,
            top,
            width,
            height,
            target.DeviceName,
            requested?.Maximized == true);
    }

    private static IReadOnlyList<WatchDisplayWorkArea> GetDisplayWorkAreas()
    {
        var scale = GetSystemDpiScale();
        var areas = new List<WatchDisplayWorkArea>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfo
                {
                    Size = Marshal.SizeOf<MonitorInfo>(),
                };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var work = info.WorkArea;
                    areas.Add(new WatchDisplayWorkArea(
                        info.DeviceName,
                        new Rect(
                            work.Left / scale,
                            work.Top / scale,
                            (work.Right - work.Left) / scale,
                            (work.Bottom - work.Top) / scale),
                        (info.Flags & MonitorInfoPrimary) != 0));
                }

                return true;
            },
            IntPtr.Zero);
        if (areas.Count > 0)
        {
            return areas;
        }

        return
        [
            new WatchDisplayWorkArea(
                "PRIMARY",
                SystemParameters.WorkArea,
                IsPrimary: true),
        ];
    }

    private static WatchDisplayWorkArea? FindBestWorkArea(
        Rect bounds,
        IReadOnlyList<WatchDisplayWorkArea> workAreas)
    {
        var match = workAreas.Select(area => new
        {
            Area = area,
            Intersection = Rect.Intersect(bounds, area.Bounds),
        })
            .Select(candidate => new
            {
                candidate.Area,
                VisibleArea = candidate.Intersection.IsEmpty
                    ? 0
                    : candidate.Intersection.Width * candidate.Intersection.Height,
            })
            .OrderByDescending(candidate => candidate.VisibleArea)
            .FirstOrDefault();
        return match is { VisibleArea: > 0 } ? match.Area : null;
    }

    private static bool HasVisibleIntersection(Rect bounds, Rect workArea)
    {
        var intersection = Rect.Intersect(bounds, workArea);
        return !intersection.IsEmpty
            && intersection.Width >= MinimumVisibleLength
            && intersection.Height >= MinimumVisibleLength;
    }

    private static bool IsUsable(Rect bounds) => !bounds.IsEmpty
        && double.IsFinite(bounds.Left)
        && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width > 0
        && bounds.Height > 0;

    private static double ClampLength(double value, double minimum, double available)
    {
        var maximum = Math.Max(MinimumVisibleLength, available);
        var effectiveMinimum = Math.Min(Math.Max(MinimumVisibleLength, minimum), maximum);
        return Math.Clamp(value, effectiveMinimum, maximum);
    }

    private static double PositiveFiniteOrDefault(double? value, double fallback) =>
        value is > 0 && double.IsFinite(value.Value) ? value.Value : fallback;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static double GetSystemDpiScale()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? dpi / 96d : 1d;
        }
        catch (EntryPointNotFoundException)
        {
            return 1d;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private const int MonitorInfoPrimary = 1;

    private delegate bool MonitorEnumProcedure(
        IntPtr monitor,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
