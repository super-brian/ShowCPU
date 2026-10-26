using System.Windows;
using System.Windows.Media;

namespace ShowCPU;

// One continuous white stroke. A Border pen sits on the rounded window
// region and its corner arcs drop out, so the four sides never meet.
public sealed class PillOutline : FrameworkElement
{
    public PillOutline()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 2 || height <= 2)
            return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        // Center the 1px stroke on the first pixel fully inside the region.
        double left = 1.5 / sx;
        double top = 1.5 / sy;
        double right = width - (1.5 / sx);
        double bottom = height - (1.5 / sy);
        double rectWidth = right - left;
        double rectHeight = bottom - top;
        if (rectWidth <= 1 || rectHeight <= 1)
            return;

        double radiusX = Math.Min(Math.Max(0, 4.0 - (1.5 / sx)), rectWidth / 2.0);
        double radiusY = Math.Min(Math.Max(0, 4.0 - (1.5 / sy)), rectHeight / 2.0);
        double thickness = 2.0 / (sx + sy);

        var pen = new Pen(Brushes.White, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, new Rect(left, top, rectWidth, rectHeight), radiusX, radiusY);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }
}
