using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ShowCPU.Services;

internal static class WindowEffects
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.Loaded += (_, _) => PlaceAboveTaskbar(window);
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            window.Close();
        };
    }

    public static void Drag(Window window)
    {
        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove is only valid while the primary button is down.
        }
    }

    private static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int corner = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var margins = new NativeMethods.MARGINS
        {
            Left = 1,
            Right = 1,
            Top = 1,
            Bottom = 1
        };
        _ = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static void PlaceAboveTaskbar(Window window)
    {
        Rect area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left + 12, area.Right - window.ActualWidth - 18);
        window.Top = Math.Max(area.Top + 12, area.Bottom - window.ActualHeight - 18);
    }
}
