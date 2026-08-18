using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace MesIngest.Watch.UiTests;

internal static class WatchWindowNative
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    public static void SetClientSize(IntPtr handle, int width, int height)
    {
        var rect = new NativeRect(0, 0, width, height);
        var style = unchecked((uint)GetWindowLongPtr(handle, GwlStyle).ToInt64());
        var exStyle = unchecked((uint)GetWindowLongPtr(handle, GwlExStyle).ToInt64());
        var dpi = GetDpiForWindow(handle);
        if (!AdjustWindowRectExForDpi(ref rect, style, false, exStyle, dpi))
        {
            throw new InvalidOperationException(
                $"AdjustWindowRectExForDpi failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                24,
                24,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                0))
        {
            throw new InvalidOperationException(
                $"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        ShowWindow(handle, 5);
        SetForegroundWindow(handle);

        // FluentWindow uses custom non-client chrome whose effective client insets are
        // not fully represented by GWL_STYLE. Correct from the measured client area so
        // both stock Window and WPF-UI windows reach the exact baseline dimensions.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!GetClientRect(handle, out var clientRect)
                || !GetWindowRect(handle, out var windowRect))
            {
                throw new InvalidOperationException(
                    $"Reading the resized window failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            var clientWidth = clientRect.Right - clientRect.Left;
            var clientHeight = clientRect.Bottom - clientRect.Top;
            if (clientWidth == width && clientHeight == height)
            {
                return;
            }

            var outerWidth = windowRect.Right - windowRect.Left;
            var outerHeight = windowRect.Bottom - windowRect.Top;
            if (!SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    windowRect.Left,
                    windowRect.Top,
                    outerWidth + width - clientWidth,
                    outerHeight + height - clientHeight,
                    0))
            {
                throw new InvalidOperationException(
                    $"Correcting the client size failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }

        GetClientRect(handle, out var finalClientRect);
        throw new InvalidOperationException(
            $"Could not stabilize the client area at {width}x{height}; got "
            + $"{finalClientRect.Right - finalClientRect.Left}x{finalClientRect.Bottom - finalClientRect.Top}.");
    }

    public static byte[] CaptureClientArea(IntPtr handle)
        => CaptureClientArea(handle, 1440, 900);

    public static byte[] CaptureClientAreaAtCurrentSize(IntPtr handle)
        => CaptureClientArea(handle, null, null);

    public static bool IsForegroundWindow(IntPtr handle) => GetForegroundWindow() == handle;

    private static byte[] CaptureClientArea(IntPtr handle, int? expectedWidth, int? expectedHeight)
    {
        if (!GetClientRect(handle, out var rect))
        {
            throw new InvalidOperationException(
                $"GetClientRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Expected a non-empty client area, but got {width}x{height}.");
        }

        if (expectedWidth is not null
            && expectedHeight is not null
            && (width != expectedWidth || height != expectedHeight))
        {
            throw new InvalidOperationException(
                $"Expected a {expectedWidth}x{expectedHeight} client area, but got {width}x{height}.");
        }

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        try
        {
            // PrintWindow renders the client independently of desktop occlusion. A screen
            // rectangle capture can silently include another foreground window and poison
            // an otherwise valid pixel baseline.
            if (!PrintWindow(handle, deviceContext, PrintWindowClientOnly | PrintWindowFullContent))
            {
                throw new InvalidOperationException(
                    $"PrintWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    public static void MovePointerOffWindow()
    {
        var x = Math.Max(0, GetSystemMetrics(0) - 1);
        var y = Math.Max(0, GetSystemMetrics(1) - 1);
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException(
                $"SetCursorPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private const uint PrintWindowClientOnly = 0x00000001;
    private const uint PrintWindowFullContent = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectExForDpi(
        ref NativeRect rect,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool hasMenu,
        uint exStyle,
        uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
