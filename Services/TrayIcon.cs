using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace SysPulse.Services;

/// <summary>
/// Notification-area icon. Shows a live CPU mini-graph, a tooltip with the key numbers,
/// and a right-click menu for the main settings.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int HistoryLength = 16;

    private readonly WinForms.NotifyIcon _notify;
    private readonly WinForms.ToolStripMenuItem _topmost, _closeToTray;
    private readonly List<(WinForms.ToolStripMenuItem Item, int Ms)> _speeds = [];
    private readonly Queue<double> _history = new();
    private readonly Icon _staticIcon;
    private IntPtr _liveHandle;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<bool>? TopmostChanged;
    public event Action<bool>? CloseToTrayChanged;
    public event Action<int>? IntervalChanged;

    public TrayIcon(AppSettings settings)
    {
        var size = WinForms.SystemInformation.SmallIconSize;
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SysPulse.ico")).Stream)
            _staticIcon = new Icon(stream, size);

        var menu = new WinForms.ContextMenuStrip();
        var open = new WinForms.ToolStripMenuItem("Open SysPulse", null, (_, _) => ShowRequested?.Invoke());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _topmost = new WinForms.ToolStripMenuItem("Always on top", null, (_, _) => TopmostChanged?.Invoke(!_topmost!.Checked));
        _closeToTray = new WinForms.ToolStripMenuItem("Keep running in tray when closed", null, (_, _) => CloseToTrayChanged?.Invoke(!_closeToTray!.Checked));
        var speed = new WinForms.ToolStripMenuItem("Update speed");
        foreach (int ms in AppSettings.Intervals)
        {
            var item = new WinForms.ToolStripMenuItem(ms < 1000 ? $"{ms / 1000.0:0.#} seconds" : ms == 1000 ? "1 second" : $"{ms / 1000} seconds",
                null, (_, _) => IntervalChanged?.Invoke(ms));
            speed.DropDownItems.Add(item);
            _speeds.Add((item, ms));
        }
        menu.Items.AddRange([_topmost, speed, _closeToTray, new WinForms.ToolStripSeparator()]);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _notify = new WinForms.NotifyIcon
        {
            Icon = _staticIcon,
            Text = "SysPulse",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notify.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowRequested?.Invoke(); };

        Sync(settings);
    }

    /// <summary>Refresh menu check marks from the current settings.</summary>
    public void Sync(AppSettings s)
    {
        _topmost.Checked = s.Topmost;
        _closeToTray.Checked = s.CloseToTray;
        foreach (var (item, ms) in _speeds) item.Checked = ms == s.IntervalMs;
    }

    public void ShowHint(string title, string text) =>
        _notify.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);

    /// <summary>Push a CPU sample: redraws the icon graph and updates the tooltip.</summary>
    public void Update(double cpuPercent, string tooltip)
    {
        _history.Enqueue(cpuPercent);
        while (_history.Count > HistoryLength) _history.Dequeue();

        // Tooltip limit is 127 characters
        _notify.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        var newHandle = DrawGraph(WinForms.SystemInformation.SmallIconSize, _history.ToArray());
        if (newHandle == IntPtr.Zero) return;
        _notify.Icon = Icon.FromHandle(newHandle);
        if (_liveHandle != IntPtr.Zero) DestroyIcon(_liveHandle); // free the previous frame
        _liveHandle = newHandle;
    }

    /// <summary>Dark rounded tile with CPU bars, colored by load (cyan / amber / red).</summary>
    private static IntPtr DrawGraph(Size size, double[] values)
    {
        int w = size.Width, h = size.Height;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var tile = RoundRect(0, 0, w - 1, h - 1, Math.Max(2, w / 5f)))
            using (var bg = new SolidBrush(Color.FromArgb(255, 0x16, 0x19, 0x21)))
            using (var border = new Pen(Color.FromArgb(255, 0x34, 0x3B, 0x4D)))
            {
                g.FillPath(bg, tile);
                g.DrawPath(border, tile);
            }

            g.SmoothingMode = SmoothingMode.None;
            int inset = Math.Max(2, w / 8);
            int innerW = w - inset * 2, innerH = h - inset * 2;
            int barW = Math.Max(2, innerW / 6); // ~6 chunky bars: legible at 16 px
            int bars = innerW / barW;
            var recent = values.Skip(Math.Max(0, values.Length - bars)).ToArray();

            // One color for the whole icon, from the current load: calm cyan, busy amber, maxed red
            double now = recent.Length > 0 ? recent[^1] : 0;
            var color = now >= 85 ? Color.FromArgb(0xF0, 0x71, 0x78) : now >= 60 ? Color.FromArgb(0xFF, 0xD5, 0x4F) : Color.FromArgb(0x4F, 0xC3, 0xF7);
            using var brush = new SolidBrush(color);

            int x = inset + innerW - recent.Length * barW; // newest on the right
            foreach (double v in recent)
            {
                int bh = Math.Max(1, (int)Math.Round(Math.Clamp(v, 0, 100) / 100 * innerH));
                g.FillRectangle(brush, x, inset + innerH - bh, barW, bh);
                x += barW;
            }
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notify.Visible = false; // removes the icon immediately instead of leaving a ghost
        _notify.Dispose();
        _staticIcon.Dispose();
        if (_liveHandle != IntPtr.Zero) DestroyIcon(_liveHandle);
    }
}
