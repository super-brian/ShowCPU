using System.Windows;
using System.Windows.Media;

namespace ShowCPU;

public sealed class CpuGraph : FrameworkElement
{
    private const int SampleCount = 15;

    private const double ClockCenterMhz = 800.0;

    private readonly double?[] _samples = new double?[SampleCount];
    private readonly double?[] _clocks = new double?[SampleCount];
    private readonly double?[] _powers = new double?[SampleCount];
    // Top stays at 2x base. 0.8 GHz is the vertical center once base MHz is known.
    private double _clockScaleMax = 200.0;
    private double _clockCenterPercent = 100.0;
    private double _baseMhz;
    private double? _powerScaleMax;

    private static readonly (int Min, SolidColorBrush Brush)[] Bands =
    [
        (90, Frozen(0xFF, 0x3B, 0x30)), // red
        (80, Frozen(0xFF, 0x95, 0x00)), // orange
        (70, Frozen(0xFF, 0xD6, 0x0A)), // yellow
        (60, Frozen(0x34, 0xC7, 0x59)), // green
        (50, Frozen(0x0A, 0x84, 0xFF)), // blue
        (40, Frozen(0x5E, 0x5C, 0xE6)), // indigo
        (0, Frozen(0xBF, 0x5A, 0xF2)),  // violet
    ];

    private static readonly Pen CeilingPen = CreatePen(Color.FromArgb(160, 0x3D, 0xDC, 0x84), 1);
    private static readonly Pen ClockBluePen = CreatePen(Color.FromRgb(0x6E, 0xCB, 0xFF), 1.5);
    private static readonly Pen ClockRedPen = CreatePen(Color.FromRgb(0xFF, 0x3B, 0x30), 1.5);
    private static readonly Pen PowerOrangePen = CreatePen(Color.FromRgb(0xFF, 0x95, 0x00), 1.5);

    public CpuGraph()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public static SolidColorBrush BrushFor(double percent)
    {
        double rounded = Math.Round(percent);
        foreach ((int min, SolidColorBrush brush) in Bands)
        {
            if (rounded >= min)
                return brush;
        }

        return Bands[^1].Brush;
    }

    public void SetClockScaleMax(double? percentOfBase)
    {
        if (percentOfBase is not double percent || !double.IsFinite(percent) || percent < 200)
            return;

        // Top is 2x base. Mid-height is 0.8 GHz, not this cap.
        double next = Math.Clamp(percent, 200.0, 200.0);
        if (Math.Abs(next - _clockScaleMax) < 0.25)
            return;

        _clockScaleMax = next;
        InvalidateVisual();
    }

    public void SetClockBaseMhz(double? baseMhz)
    {
        if (baseMhz is not double mhz || !double.IsFinite(mhz) || mhz <= ClockCenterMhz)
            return;

        if (Math.Abs(mhz - _baseMhz) < 0.5)
            return;

        _baseMhz = mhz;
        double center = ClockCenterMhz * 100.0 / mhz;
        if (center <= 0 || center >= _clockScaleMax)
            center = _clockScaleMax * 0.5;

        _clockCenterPercent = center;
        InvalidateVisual();
    }

    public void SetPowerScaleMax(double? watts)
    {
        if (watts is not double value || !double.IsFinite(value) || value <= 0)
            return;

        double next = Math.Clamp(value, 10.0, 500.0);
        if (_powerScaleMax is double current && next <= current + 0.25)
            return;

        _powerScaleMax = next;
        InvalidateVisual();
    }

    public void Push(double? percent, double? clockPercent = null, double? powerWatts = null)
    {
        if (percent is double cpu)
        {
            if (cpu < 0)
                cpu = 0;
            else if (cpu > 100)
                cpu = 100;
            percent = cpu;
        }

        if (clockPercent is double clock)
        {
            if (clock < 0)
                clock = 0;
            clockPercent = clock;
            if (clock > _clockScaleMax)
                SetClockScaleMax(clock);
        }

        if (powerWatts is double power)
        {
            if (power < 0)
                power = 0;
            powerWatts = power;
            if (_powerScaleMax is not double powerScale || power > powerScale)
                SetPowerScaleMax(Math.Max(power * 1.25, 10.0));
        }

        for (int i = 0; i < _samples.Length - 1; i++)
        {
            _samples[i] = _samples[i + 1];
            _clocks[i] = _clocks[i + 1];
            _powers[i] = _powers[i + 1];
        }

        _samples[^1] = percent;
        _clocks[^1] = clockPercent;
        _powers[^1] = powerWatts;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double dpiY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        const double barWidth = 2;
        const double gap = 2;
        double total = (SampleCount * barWidth) + ((SampleCount - 1) * gap);
        double origin = 0;
        double lineY = Snap(0.5, dpiY);
        dc.DrawLine(CeilingPen, new Point(origin, lineY), new Point(Math.Min(width, origin + total), lineY));

        for (int i = 0; i < SampleCount; i++)
        {
            double x = Snap(origin + (i * (barWidth + gap)), dpiX);
            double bw = Math.Max(1.0 / dpiX, Snap(barWidth, dpiX));

            if (_samples[i] is not double value || value <= 0)
                continue;

            double barHeight = height * (Math.Clamp(value, 0, 100) / 100.0);
            if (barHeight < 2)
                barHeight = 2;
            if (barHeight > height)
                barHeight = height;

            double y = Snap(height - barHeight, dpiY);
            double bh = Math.Max(1.0 / dpiY, Snap(barHeight, dpiY));
            if (y + bh > height)
                y = Math.Max(0, height - bh);

            DrawBar(dc, BrushFor(value), new Rect(x, y, bw, bh));
        }

        DrawClockLine(dc, origin, barWidth, gap, height, dpiX, dpiY);
        DrawPowerLine(dc, origin, barWidth, gap, height, dpiX, dpiY);
    }

    private void DrawClockLine(
        DrawingContext dc,
        double origin,
        double barWidth,
        double gap,
        double height,
        double dpiX,
        double dpiY)
    {
        for (int i = 0; i < SampleCount - 1; i++)
        {
            if (_clocks[i] is not double a || _clocks[i + 1] is not double b)
                continue;

            double x0 = Snap(origin + (i * (barWidth + gap)) + (barWidth / 2.0), dpiX);
            double x1 = Snap(origin + ((i + 1) * (barWidth + gap)) + (barWidth / 2.0), dpiX);
            double y0 = Snap(ClockY(a, height), dpiY);
            double y1 = Snap(ClockY(b, height), dpiY);

            bool aHigh = a >= 100;
            bool bHigh = b >= 100;
            if (aHigh == bHigh)
            {
                dc.DrawLine(aHigh ? ClockRedPen : ClockBluePen, new Point(x0, y0), new Point(x1, y1));
                continue;
            }

            // Split the segment where it crosses 100% of base.
            double t = (100.0 - a) / (b - a);
            t = Math.Clamp(t, 0, 1);
            double xm = x0 + ((x1 - x0) * t);
            double ym = Snap(ClockY(100, height), dpiY);
            dc.DrawLine(aHigh ? ClockRedPen : ClockBluePen, new Point(x0, y0), new Point(xm, ym));
            dc.DrawLine(bHigh ? ClockRedPen : ClockBluePen, new Point(xm, ym), new Point(x1, y1));
        }
    }

    private void DrawPowerLine(
        DrawingContext dc,
        double origin,
        double barWidth,
        double gap,
        double height,
        double dpiX,
        double dpiY)
    {
        if (_powerScaleMax is null)
            return;

        for (int i = 0; i < SampleCount - 1; i++)
        {
            if (_powers[i] is not double a || _powers[i + 1] is not double b)
                continue;

            double x0 = Snap(origin + (i * (barWidth + gap)) + (barWidth / 2.0), dpiX);
            double x1 = Snap(origin + ((i + 1) * (barWidth + gap)) + (barWidth / 2.0), dpiX);
            double y0 = Snap(PowerY(a, height), dpiY);
            double y1 = Snap(PowerY(b, height), dpiY);
            dc.DrawLine(PowerOrangePen, new Point(x0, y0), new Point(x1, y1));
        }
    }

    private double ClockY(double percentOfBase, double height)
    {
        double clamped = Math.Clamp(percentOfBase, 0, _clockScaleMax);
        double center = _clockCenterPercent;
        if (center <= 0 || center >= _clockScaleMax)
            return height * (1.0 - (clamped / _clockScaleMax));

        // 0 at the bottom, 0.8 GHz at mid-height, scale max at the top.
        if (clamped <= center)
            return height * (1.0 - (0.5 * clamped / center));

        double t = (clamped - center) / (_clockScaleMax - center);
        return height * (0.5 * (1.0 - t));
    }

    private double PowerY(double watts, double height)
    {
        double scale = _powerScaleMax ?? 10.0;
        double clamped = Math.Clamp(watts, 0, scale);
        return height * (1.0 - (clamped / scale));
    }

    private static void DrawBar(DrawingContext dc, Brush brush, Rect rect)
    {
        double inset = Math.Min(1.25, rect.Height / 2.0);
        if (inset < 0.4 || rect.Width < 1)
        {
            dc.DrawRectangle(brush, null, rect);
            return;
        }

        double left = rect.X;
        double right = rect.Right;
        double top = rect.Y;
        double bottom = rect.Bottom;
        double mid = left + (rect.Width / 2.0);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(left, top + inset), true, true);
            context.LineTo(new Point(mid, top), true, false);
            context.LineTo(new Point(right, top + inset), true, false);
            context.LineTo(new Point(right, bottom - inset), true, false);
            context.LineTo(new Point(mid, bottom), true, false);
            context.LineTo(new Point(left, bottom - inset), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private static double Snap(double value, double dpiScale)
    {
        return Math.Round(value * dpiScale) / dpiScale;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Brush.Freeze();
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
