using System.Text;

namespace ShowCPU.Services;

internal enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom
}

internal readonly record struct TaskbarSlot(
    bool IsVisible,
    IntPtr TaskbarHwnd,
    int X,
    int Y,
    int PixelWidth,
    int PixelHeight,
    double DipWidth,
    double DipHeight,
    int Dpi,
    bool Horizontal,
    int AlongStart,
    int AlongLength,
    int EffectiveOffset)
{
    public static TaskbarSlot Hidden => new(false, IntPtr.Zero, 0, 0, 0, 0, 122, 52, 96, true, 0, 0, 0);
}

/// <summary>
/// Places the pill on the primary taskbar, just beside the notification area.
/// A hidden or auto-hidden taskbar produces a slot with IsVisible false.
/// </summary>
internal static class TaskbarPlacement
{
    public const double WidgetDipWidth = 122;
    private const double WidgetDipHeight = 52;
    private const double MinDipHeight = 40;

    // The fullest tray we have seen. A screenshot hides tray icons for a moment
    // and the tray edge jumps right. Following that jump parks the widget on
    // top of the icons, and Windows then keeps them in the overflow chevron.
    private static bool _hasAnchor;
    private static int _anchorTaskbarLeft;
    private static int _anchorTaskbarTop;
    private static int _anchorAlongLength;
    private static int _anchorDpi;
    private static int _reservedTrayEdge = int.MaxValue;

    public static void ResetCaches()
    {
        _hasAnchor = false;
        _anchorTaskbarLeft = 0;
        _anchorTaskbarTop = 0;
        _anchorAlongLength = 0;
        _anchorDpi = 0;
        _reservedTrayEdge = int.MaxValue;
    }

    public static TaskbarSlot GetSlot(
        int? alongOffset = null,
        int? savedAlongLength = null,
        int? savedDpi = null)
    {
        try
        {
            return Compute(alongOffset, savedAlongLength, savedDpi);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return TaskbarSlot.Hidden;
        }
    }

    private static TaskbarSlot Compute(int? alongOffset, int? savedAlongLength, int? savedDpi)
    {
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindowVisible(taskbar))
            return TaskbarSlot.Hidden;

        if (!NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT tb))
            return TaskbarSlot.Hidden;

        IntPtr monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return TaskbarSlot.Hidden;

        NativeMethods.RECT screen = info.rcMonitor;
        TaskbarEdge edge = DetectEdge(tb, screen);
        int visibleThickness = edge is TaskbarEdge.Left or TaskbarEdge.Right
            ? Overlap(tb.Left, tb.Right, screen.Left, screen.Right)
            : Overlap(tb.Top, tb.Bottom, screen.Top, screen.Bottom);
        if (visibleThickness <= 6 || tb.Width < 10 || tb.Height < 10)
            return TaskbarSlot.Hidden;

        uint dpiRaw = NativeMethods.GetDpiForWindow(taskbar);
        int dpi = dpiRaw < 96 ? 96 : (int)dpiRaw;
        double scale = dpi / 96.0;

        double dipHeight = WidgetDipHeight;
        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            double available = (tb.Height / scale) - 4;
            if (available < dipHeight)
                dipHeight = Math.Max(MinDipHeight, available);
        }

        int alongLengthNow = edge is TaskbarEdge.Left or TaskbarEdge.Right ? tb.Height : tb.Width;
        if (!_hasAnchor
            || Moved(_anchorTaskbarLeft, tb.Left)
            || Moved(_anchorTaskbarTop, tb.Top)
            || Moved(_anchorAlongLength, alongLengthNow)
            || _anchorDpi != dpi)
        {
            _hasAnchor = true;
            _anchorTaskbarLeft = tb.Left;
            _anchorTaskbarTop = tb.Top;
            _anchorAlongLength = alongLengthNow;
            _anchorDpi = dpi;
            _reservedTrayEdge = int.MaxValue;
        }

        int pixelWidth = Math.Max(1, (int)Math.Round(WidgetDipWidth * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round(dipHeight * scale));
        int gap = Math.Max(6, (int)Math.Round(8 * scale));

        bool hasTray = TryGetDescendantRect(taskbar, "TrayNotifyWnd", out NativeMethods.RECT tray);
        bool trayOnRight = hasTray
            && tray.Width < (tb.Width * 2 / 3)
            && tray.Left > tb.Left + (tb.Width / 5);
        int x;
        int y;

        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            int trayLeft = trayOnRight ? tray.Left : tb.Right - (int)Math.Round(320 * scale);
            if (trayLeft < _reservedTrayEdge)
                _reservedTrayEdge = trayLeft;
            trayLeft = _reservedTrayEdge;
            x = trayLeft - gap - pixelWidth;

            int minX = tb.Left + gap;
            if (TryGetDescendantRect(taskbar, "Start", out NativeMethods.RECT start))
                minX = Math.Max(minX, start.Right + gap);

            if (x < minX)
                x = minX;
            if (x < tb.Left)
                x = tb.Left + gap;

            y = tb.Top + Math.Max(0, (tb.Height - pixelHeight) / 2);
        }
        else
        {
            int innerGap = 4;
            if (edge == TaskbarEdge.Left)
                x = pixelWidth > tb.Width - 8 ? tb.Left + innerGap : tb.Left + (tb.Width - pixelWidth) / 2;
            else
                x = pixelWidth > tb.Width - 8 ? tb.Right - pixelWidth - innerGap : tb.Left + (tb.Width - pixelWidth) / 2;

            int trayTop = hasTray ? tray.Top : tb.Bottom - (int)Math.Round(160 * scale);
            y = trayTop - gap - pixelHeight;
            if (y < tb.Top + gap)
                y = tb.Top + gap;
        }

        bool horizontal = edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
        int alongStart = horizontal ? tb.Left : tb.Top;
        int alongLength = horizontal ? tb.Width : tb.Height;
        int widgetAlong = horizontal ? pixelWidth : pixelHeight;
        int maxOffset = Math.Max(0, alongLength - widgetAlong);

        if (alongOffset is int offset)
        {
            offset = RescaleOffset(offset, savedAlongLength, savedDpi, alongLength, dpi, widgetAlong, maxOffset);
            offset = Math.Clamp(offset, 0, maxOffset);
            if (horizontal)
            {
                x = alongStart + offset;
                y = tb.Top + Math.Max(0, (tb.Height - pixelHeight) / 2);
            }
            else
            {
                x = pixelWidth > tb.Width - 8
                    ? (edge == TaskbarEdge.Left ? tb.Left + 4 : tb.Right - pixelWidth - 4)
                    : tb.Left + (tb.Width - pixelWidth) / 2;
                y = alongStart + offset;
            }
        }

        int effectiveOffset = horizontal ? x - alongStart : y - alongStart;
        effectiveOffset = Math.Clamp(effectiveOffset, 0, maxOffset);
        return new TaskbarSlot(
            true,
            taskbar,
            x,
            y,
            pixelWidth,
            pixelHeight,
            WidgetDipWidth,
            dipHeight,
            dpi,
            horizontal,
            alongStart,
            alongLength,
            effectiveOffset);
    }

    private static int RescaleOffset(
        int offset,
        int? savedAlongLength,
        int? savedDpi,
        int alongLength,
        int dpi,
        int widgetAlong,
        int maxOffset)
    {
        if (savedAlongLength is not int previousLength || previousLength <= 0)
            return offset;

        bool lengthMoved = Moved(previousLength, alongLength);
        bool dpiMoved = savedDpi is int previousDpi && previousDpi > 0 && previousDpi != dpi;
        if (!lengthMoved && !dpiMoved)
            return offset;

        int previousWidgetAlong = widgetAlong;
        if (savedDpi is int oldDpi && oldDpi > 0)
            previousWidgetAlong = Math.Max(1, (int)Math.Round(WidgetDipWidth * oldDpi / 96.0));

        int previousMax = Math.Max(1, previousLength - previousWidgetAlong);
        double t = Math.Clamp(offset / (double)previousMax, 0, 1);
        return (int)Math.Round(t * maxOffset);
    }

    private static bool Moved(int previous, int current)
    {
        long delta = (long)current - previous;
        if (delta < 0)
            delta = -delta;
        return delta > 20;
    }

    private static TaskbarEdge DetectEdge(NativeMethods.RECT tb, NativeMethods.RECT screen)
    {
        if (tb.Width >= tb.Height)
        {
            int fromTop = Math.Abs(tb.Top - screen.Top);
            int fromBottom = Math.Abs(tb.Bottom - screen.Bottom);
            return fromTop < fromBottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        int fromLeft = Math.Abs(tb.Left - screen.Left);
        int fromRight = Math.Abs(tb.Right - screen.Right);
        return fromLeft <= fromRight ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static int Overlap(int startA, int endA, int startB, int endB)
    {
        return Math.Max(0, Math.Min(endA, endB) - Math.Max(startA, startB));
    }

    private static bool TryGetDescendantRect(IntPtr root, string className, out NativeMethods.RECT rect)
    {
        rect = default;
        IntPtr hwnd = FindDescendant(root, className);
        if (hwnd == IntPtr.Zero)
            return false;
        return NativeMethods.GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    private static IntPtr FindDescendant(IntPtr parent, string className)
    {
        return FindDescendant(parent, className, 0);
    }

    private static IntPtr FindDescendant(IntPtr parent, string className, int depth)
    {
        if (depth > 5)
            return IntPtr.Zero;

        IntPtr child = IntPtr.Zero;
        for (int i = 0; i < 64; i++)
        {
            child = NativeMethods.FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero)
                return IntPtr.Zero;

            if (ClassName(child) == className)
                return child;

            IntPtr nested = FindDescendant(child, className, depth + 1);
            if (nested != IntPtr.Zero)
                return nested;
        }

        return IntPtr.Zero;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
