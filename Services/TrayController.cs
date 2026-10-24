namespace ShowCPU.Services;

internal sealed class TrayController : IDisposable
{
    private readonly Action _openAbout;
    private readonly Action _quit;
    private readonly Action _quitForever;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _trayIcon;
    private bool _disposed;

    public TrayController(Action openAbout, Action quit, Action quitForever)
    {
        _openAbout = openAbout;
        _quit = quit;
        _quitForever = quitForever;
        _trayIcon = CreateIcon();

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new System.Windows.Forms.Padding(4, 6, 4, 6),
            Font = new System.Drawing.Font("Segoe UI", 10f),
            BackColor = System.Drawing.Color.FromArgb(28, 28, 28),
            ForeColor = System.Drawing.Color.FromArgb(244, 244, 245)
        };
        menu.Items.Add(Item("About", _openAbout));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quit", _quit));
        menu.Items.Add(Item("Quit Forever", _quitForever));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ShowCPU",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(_openAbout);
        };
    }

    public void SetCpu(int? percent, string? clockGhz = null, int? powerWatts = null)
    {
        if (_disposed)
            return;

        var parts = new System.Collections.Generic.List<string>(3);
        if (percent is int cpu)
            parts.Add("CPU " + cpu.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%");
        if (!string.IsNullOrWhiteSpace(clockGhz))
            parts.Add("CLK " + clockGhz);
        if (powerWatts is int watts)
            parts.Add("PWR " + watts.ToString(System.Globalization.CultureInfo.InvariantCulture) + "W");

        string text = parts.Count > 0 ? string.Join("  ", parts) : "ShowCPU";
        if (text.Length > 63)
            text = text.Substring(0, 63);
        _icon.Text = text;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        System.Windows.Forms.ContextMenuStrip? menu = _icon.ContextMenuStrip;
        _icon.Visible = false;
        _icon.ContextMenuStrip = null;
        _icon.Icon = null;
        _icon.Dispose();
        menu?.Dispose();
        _trayIcon.Dispose();
    }

    private static System.Windows.Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text)
        {
            ForeColor = System.Drawing.Color.FromArgb(244, 244, 245),
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new System.Windows.Forms.Padding(12, 6, 20, 6)
        };
        item.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(onClick);
        return item;
    }

    private static System.Drawing.Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/ShowCPU.ico"))
            ?? throw new InvalidOperationException("ShowCPU icon resource is missing.");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }

    private sealed class DarkMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColors())
        {
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = System.Drawing.Color.FromArgb(244, 244, 245);
            // ProfessionalRenderer's default text rect sits high; draw into the
            // full content row so the label is vertically centered (esp. at high DPI).
            var rect = new System.Drawing.Rectangle(
                e.TextRectangle.X,
                e.Item.ContentRectangle.Y,
                e.TextRectangle.Width,
                e.Item.ContentRectangle.Height);
            System.Windows.Forms.TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont ?? e.Item.Font,
                rect,
                e.TextColor,
                System.Windows.Forms.TextFormatFlags.Left
                    | System.Windows.Forms.TextFormatFlags.VerticalCenter
                    | System.Windows.Forms.TextFormatFlags.SingleLine
                    | System.Windows.Forms.TextFormatFlags.NoPrefix
                    | System.Windows.Forms.TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(System.Drawing.Color.FromArgb(28, 28, 28));
        }

        protected override void OnRenderToolStripBorder(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            var rect = new System.Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(58, 58, 58));
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(46, 46, 46));
            e.Graphics.DrawLine(pen, e.Item.ContentRectangle.Left + 8, y, e.Item.ContentRectangle.Right - 8, y);
        }
    }

    private sealed class DarkColors : System.Windows.Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Menu = System.Drawing.Color.FromArgb(28, 28, 28);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(42, 58, 48);
        private static readonly System.Drawing.Color Edge = System.Drawing.Color.FromArgb(61, 220, 132);

        public override System.Drawing.Color ToolStripDropDownBackground => Menu;
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(58, 58, 58);
        public override System.Drawing.Color MenuItemBorder => Edge;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override System.Drawing.Color ImageMarginGradientBegin => Menu;
        public override System.Drawing.Color ImageMarginGradientMiddle => Menu;
        public override System.Drawing.Color ImageMarginGradientEnd => Menu;
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(46, 46, 46);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(46, 46, 46);
    }
}
