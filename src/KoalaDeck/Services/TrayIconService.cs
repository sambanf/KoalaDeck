using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KoalaDeck.Interop;

namespace KoalaDeck.Services;

/// <summary>System tray icon (next to the clock) with a dark context menu. Must be created on the UI thread.</summary>
public sealed class TrayIconService : IDisposable
{
    private const int MaxTooltipLength = 120; // NotifyIcon.Text throws above 127 characters

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly Icon _connectedIcon;
    private readonly Icon _searchingIcon;
    private readonly Icon _disconnectedIcon;
    private readonly Func<bool> _isAutoStartEnabled;

    public TrayIconService(Func<bool> isAutoStartEnabled)
    {
        _isAutoStartEnabled = isAutoStartEnabled;

        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        _connectedIcon = KoalaArt.CreateIcon(size, KoalaArt.ConnectedColor);
        _searchingIcon = KoalaArt.CreateIcon(size, KoalaArt.SearchingColor);
        _disconnectedIcon = KoalaArt.CreateIcon(size, KoalaArt.DisconnectedColor);

        var menuFont = new Font("Segoe UI", 9.5f);
        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        var openItem = new ToolStripMenuItem("Open Dashboard", null, (_, _) => Raise(OpenDashboardRequested))
        {
            Font = new Font(menuFont, FontStyle.Bold),
        };
        _autoStartItem = new ToolStripMenuItem("Auto-start on Windows Boot", null, (_, _) => Raise(AutoStartToggleRequested));
        var reconnectItem = new ToolStripMenuItem("Reconnect Serial", null, (_, _) => Raise(ReconnectRequested));
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Raise(ExitRequested));

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = menuFont,
            Padding = new Padding(2, 4, 2, 4),
        };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            openItem,
            _autoStartItem,
            reconnectItem,
            new ToolStripSeparator(),
            exitItem,
        });
        foreach (ToolStripItem item in _menu.Items)
        {
            item.Padding = new Padding(4, 5, 12, 5);
        }

        _menu.Opening += (_, _) => _autoStartItem.Checked = _isAutoStartEnabled();
        _menu.HandleCreated += (_, _) => NativeMethods.ApplyRoundedCorners(_menu.Handle);

        _notifyIcon = new NotifyIcon
        {
            Icon = _disconnectedIcon,
            Text = "Koala Deck — Disconnected",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Raise(OpenDashboardRequested);
            }
        };
    }

    public event EventHandler? OpenDashboardRequested;

    public event EventHandler? AutoStartToggleRequested;

    public event EventHandler? ReconnectRequested;

    public event EventHandler? ExitRequested;

    public void UpdateStatus(SerialConnectionState state, string? portName)
    {
        (Icon icon, string text) = state switch
        {
            SerialConnectionState.Connected => (_connectedIcon, $"Connected · {portName}"),
            SerialConnectionState.Searching => (_searchingIcon, portName is null ? "Searching for ESP32…" : $"Waiting for {portName}…"),
            _ => (_disconnectedIcon, "Disconnected"),
        };

        _notifyIcon.Icon = icon;
        string tooltip = $"Koala Deck — {text}";
        _notifyIcon.Text = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
        _statusItem.Text = text;
    }

    public void ShowNotification(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _connectedIcon.Dispose();
        _searchingIcon.Dispose();
        _disconnectedIcon.Dispose();
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error("Tray menu action failed", ex);
        }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color TextColor = Color.FromArgb(232, 236, 244);
        private static readonly Color MutedColor = Color.FromArgb(139, 148, 167);
        private static readonly Color AccentColor = Color.FromArgb(79, 140, 255);

        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextColor : MutedColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            int side = Math.Min(r.Width, r.Height) - 2;
            var box = new RectangleF(r.X + (r.Width - side) / 2f, r.Y + (r.Height - side) / 2f, side, side);

            using (var fill = new SolidBrush(AccentColor))
            using (GraphicsPath path = RoundedRectangle(box, side / 4f))
            {
                g.FillPath(fill, path);
            }

            using var pen = new Pen(Color.White, Math.Max(1.6f, side / 8f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(pen, new[]
            {
                new PointF(box.Left + side * 0.26f, box.Top + side * 0.52f),
                new PointF(box.Left + side * 0.44f, box.Top + side * 0.70f),
                new PointF(box.Left + side * 0.76f, box.Top + side * 0.33f),
            });
        }

        private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(20, 24, 33);
        private static readonly Color Hover = Color.FromArgb(35, 41, 56);
        private static readonly Color Border = Color.FromArgb(37, 43, 57);

        public override Color ToolStripDropDownBackground => Background;

        public override Color ImageMarginGradientBegin => Background;

        public override Color ImageMarginGradientMiddle => Background;

        public override Color ImageMarginGradientEnd => Background;

        public override Color MenuBorder => Border;

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color SeparatorDark => Border;

        public override Color SeparatorLight => Border;

        public override Color CheckBackground => Background;

        public override Color CheckSelectedBackground => Hover;

        public override Color CheckPressedBackground => Hover;
    }
}
