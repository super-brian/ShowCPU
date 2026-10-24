using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ShowCPU.Services;

namespace ShowCPU;

public partial class WidgetWindow : Window
{
    private IntPtr _hwnd;
    private bool _lock;
    private bool _dragging;
    private int _grab;
    private int _dragStartOffset;
    private TaskbarSlot _slot;
    private TaskbarSlot _pendingSlot;
    private int _pendingTicks;
    private long _hiddenSince;

    private const int StableMoveTicks = 3;
    private const long HideDelayMs = 1200;

    internal bool IsDragging => _dragging;

    internal event Action<int>? DragCompleted;
    internal event Action? DisplayMetricsChanged;
    private int _regionWidth;
    private int _regionHeight;
    private int _appliedDpi;

    public WidgetWindow()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        DpiChanged += OnWindowDpiChanged;
    }

    private static readonly System.Windows.Media.SolidColorBrush CpuYellowBrush = CreateFrozenBrush(0xFF, 0xD6, 0x0A);
    private static readonly System.Windows.Media.SolidColorBrush ClockBlueBrush = CreateFrozenBrush(0x6E, 0xCB, 0xFF);
    private static readonly System.Windows.Media.SolidColorBrush ClockRedBrush = CreateFrozenBrush(0xFF, 0x3B, 0x30);
    private static readonly System.Windows.Media.SolidColorBrush PowerOrangeBrush = CreateFrozenBrush(0xFF, 0x95, 0x00);

    public void SetUsage(
        double? cpuPercent,
        int? cpuShown,
        double? clockPercent,
        double? clockMhz,
        double? clockScaleMax = null,
        double? clockBaseMhz = null,
        double? powerWatts = null,
        int? powerShown = null,
        double? powerScaleMax = null)
    {
        if (cpuShown is int shown)
        {
            PercentText.Text = FormatPercent(shown);
            PercentText.Foreground = CpuYellowBrush;
            CpuLabel.Foreground = CpuYellowBrush;
        }

        Graph.SetClockScaleMax(clockScaleMax);
        Graph.SetClockBaseMhz(clockBaseMhz);
        Graph.SetPowerScaleMax(powerScaleMax);
        Graph.Push(cpuPercent, clockPercent, powerWatts);

        // Null means this tick had no reading -- keep the last shown value.
        if (clockMhz is double mhz)
        {
            ClockText.Text = FormatGhz(mhz);
            var clockBrush = (clockPercent ?? 0) >= 100 ? ClockRedBrush : ClockBlueBrush;
            ClockText.Foreground = clockBrush;
            ClockLabel.Foreground = clockBrush;
        }

        if (powerShown is int watts)
        {
            PowerText.Text = FormatWatts(watts);
            PowerText.Foreground = PowerOrangeBrush;
            PowerLabel.Foreground = PowerOrangeBrush;
        }
    }

    // Always 4 monospace chars ("  5%", " 97%", "100%") so layout width stays put.
    private static string FormatPercent(int value)
    {
        return (value.ToString(CultureInfo.InvariantCulture) + "%").PadLeft(4);
    }

    // Always 4 monospace chars ("2.4G", "5.1G").
    private static string FormatGhz(double mhz)
    {
        double ghz = Math.Clamp(mhz, 0, 99_999) / 1000.0;
        string text = ghz >= 10.0
            ? Math.Round(ghz, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "G"
            : ghz.ToString("0.0", CultureInfo.InvariantCulture) + "G";
        return text.PadLeft(4);
    }

    // Always 4 monospace chars ("  5W", " 42W", "999W").
    private static string FormatWatts(int value)
    {
        int clamped = Math.Clamp(value, 0, 999);
        return (clamped.ToString(CultureInfo.InvariantCulture) + "W").PadLeft(4);
    }

    private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    internal void Apply(TaskbarSlot slot, bool force = false)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (!slot.IsVisible)
        {
            _pendingTicks = 0;
            if (!IsVisible)
                return;

            long now = Environment.TickCount64;
            if (_hiddenSince == 0)
                _hiddenSince = now;
            if (now - _hiddenSince < HideDelayMs)
                return;

            _lock = false;
            Hide();
            return;
        }

        _hiddenSince = 0;
        if (IsVisible && _lock && SamePlace(_slot, slot) && !force)
        {
            _pendingTicks = 0;
            // The taskbar paints over a window that stops asking to stay on top.
            Raise(show: false);
            return;
        }

        bool geometryChanged = IsVisible
            && _lock
            && (_slot.AlongStart != slot.AlongStart || _slot.AlongLength != slot.AlongLength);
        bool sizeChanged = IsVisible
            && _lock
            && (_slot.PixelWidth != slot.PixelWidth
                || _slot.PixelHeight != slot.PixelHeight
                || Math.Abs(_slot.DipHeight - slot.DipHeight) > 0.1
                || _slot.Dpi != slot.Dpi
                || _slot.TaskbarHwnd != slot.TaskbarHwnd);
        bool dpiChanged = _appliedDpi != 0 && _appliedDpi != slot.Dpi;
        if (dpiChanged)
            force = true;

        // After sleep the taskbar rect jumps for a few checks. Wait until the
        // new place repeats before moving a widget that is already on screen.
        // A main-display or taskbar-size / DPI change should move right away.
        if (IsVisible && !geometryChanged && !sizeChanged && !force)
        {
            if (_pendingTicks == 0 || !SamePlace(_pendingSlot, slot))
            {
                _pendingSlot = slot;
                _pendingTicks = 1;
                return;
            }

            _pendingTicks++;
            if (_pendingTicks < StableMoveTicks)
                return;
        }

        _pendingTicks = 0;
        _slot = slot;
        _lock = true;
        _appliedDpi = slot.Dpi;

        // Force another measure after a display / DPI change so transitional
        // metrics do not leave the pill permanently clipped or wrongly scaled.
        if (force || sizeChanged || dpiChanged)
        {
            _regionWidth = 0;
            _regionHeight = 0;
            Width = slot.DipWidth;
            Height = slot.DipHeight;
            InvalidateVisual();
            Graph.InvalidateVisual();
        }
        else
        {
            if (double.IsNaN(Width) || Math.Abs(Width - slot.DipWidth) > 0.1)
                Width = slot.DipWidth;
            if (double.IsNaN(Height) || Math.Abs(Height - slot.DipHeight) > 0.1)
                Height = slot.DipHeight;
        }

        Pill.CornerRadius = new CornerRadius(4);

        if (!Topmost)
            Topmost = true;

        bool wasVisible = IsVisible;
        if (!wasVisible)
            Show();

        ApplyExtendedStyle();

        // Stay above the Windows 11 taskbar. Owning Shell_TrayWnd paints the pill
        // underneath the taskbar, so it only flashes during a screenshot.
        Raise(show: true);
        UpdateRegion();
    }

    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
    {
        _regionWidth = 0;
        _regionHeight = 0;
        DisplayMetricsChanged?.Invoke();
    }

    private void Raise(bool show)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        uint flags = NativeMethods.SWP_NOACTIVATE;
        if (show)
            flags |= NativeMethods.SWP_SHOWWINDOW;

        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopmost,
            _slot.X,
            _slot.Y,
            _slot.PixelWidth,
            _slot.PixelHeight,
            flags);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExtendedStyle();
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private void ApplyExtendedStyle()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        int updated = style;
        updated |= NativeMethods.WS_EX_TOOLWINDOW;
        updated &= ~NativeMethods.WS_EX_APPWINDOW;
        updated &= ~NativeMethods.WS_EX_NOACTIVATE;
        updated &= ~NativeMethods.WS_EX_TRANSPARENT;
        if (updated == style)
            return;

        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(updated));
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED || msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            _regionWidth = 0;
            _regionHeight = 0;
            DisplayMetricsChanged?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && _lock && !_dragging)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            if ((pos.flags & NativeMethods.SWP_HIDEWINDOW) == 0)
            {
                pos.hwndInsertAfter = NativeMethods.HwndTopmost;
                pos.cx = _slot.PixelWidth;
                pos.cy = _slot.PixelHeight;
                if (_slot.Horizontal)
                {
                    pos.y = _slot.Y;
                    pos.x = _slot.X;
                }
                else
                {
                    pos.x = _slot.X;
                    pos.y = _slot.Y;
                }

                pos.flags &= ~NativeMethods.SWP_NOMOVE;
                pos.flags &= ~NativeMethods.SWP_NOSIZE;
                pos.flags &= ~NativeMethods.SWP_NOZORDER;
                pos.flags |= NativeMethods.SWP_NOACTIVATE;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).EnsureHandle();

        BeginDrag(_hwnd);
        _ = CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(_hwnd);
            return;
        }

        MoveToCursor();
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        EndDrag(_hwnd);
        e.Handled = true;
    }

    private void BeginDrag(IntPtr hwnd)
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT window))
            return;

        _grab = _slot.Horizontal ? cursor.X - window.Left : cursor.Y - window.Top;
        _dragStartOffset = _slot.Horizontal
            ? window.Left - _slot.AlongStart
            : window.Top - _slot.AlongStart;
        _dragging = true;
    }

    private void MoveToCursor()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) || _hwnd == IntPtr.Zero)
            return;

        int along = _slot.Horizontal ? cursor.X - _grab : cursor.Y - _grab;
        int length = _slot.Horizontal ? _slot.PixelWidth : _slot.PixelHeight;
        along = ClampAlong(along, length);
        int x = _slot.Horizontal ? along : _slot.X;
        int y = _slot.Horizontal ? _slot.Y : along;
        _slot = _slot with { X = x, Y = y };
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopmost,
            x,
            y,
            _slot.PixelWidth,
            _slot.PixelHeight,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void EndDrag(IntPtr hwnd)
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        int offset = _slot.Horizontal
            ? _slot.X - _slot.AlongStart
            : _slot.Y - _slot.AlongStart;
        if (offset != _dragStartOffset)
            DragCompleted?.Invoke(offset);
    }

    private static bool SamePlace(TaskbarSlot left, TaskbarSlot right)
    {
        return left.X == right.X
            && left.Y == right.Y
            && left.PixelWidth == right.PixelWidth
            && left.PixelHeight == right.PixelHeight;
    }

    private int ClampAlong(int position, int widgetLength)
    {
        int max = Math.Max(_slot.AlongStart, _slot.AlongStart + _slot.AlongLength - widgetLength);
        return Math.Clamp(position, _slot.AlongStart, max);
    }

    private void UpdateRegion()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT rc))
            return;

        int width = rc.Width;
        int height = rc.Height;
        if (width <= 1 || height <= 1 || (width == _regionWidth && height == _regionHeight))
            return;

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi < 96)
            dpi = 96;
        int radius = Math.Max(2, (int)Math.Round(4.0 * dpi / 96.0));
        IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero)
            return;

        if (NativeMethods.SetWindowRgn(_hwnd, region, true) == 0)
        {
            _ = NativeMethods.DeleteObject(region);
            return;
        }

        _regionWidth = width;
        _regionHeight = height;
    }
}
