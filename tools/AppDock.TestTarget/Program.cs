using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AppDock.TestTarget;

/// <summary>
/// A stand-in for a real target app (e.g. Claude Desktop) used for development, QA and integration tests.
/// Arguments: --frameless (Electron-style custom title bar), --min-width N, --title "text", --hidden.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TargetForm(args));
    }
}

internal sealed class TargetForm : Form
{
    private const int WmNcHitTest = 0x84;
    private const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13,
        HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    private readonly bool _frameless;
    private readonly Label _info = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 11f), Padding = new Padding(16) };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 250 };
    private bool _startHidden;

    public TargetForm(string[] args)
    {
        _frameless = args.Contains("--frameless");
        _startHidden = args.Contains("--hidden");
        Text = Arg(args, "--title") ?? "AppDock Test Target";
        if (int.TryParse(Arg(args, "--min-width"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minWidth))
        {
            MinimumSize = new Size(minWidth, 200);
        }

        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(200, 150, 900, 640);
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.Gainsboro;
        if (_frameless)
        {
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(0, 36, 0, 0);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8) };
        buttons.Controls.Add(MakeButton("Hide 3 s (self)", async (_, _) =>
        {
            Hide();
            await Task.Delay(3000);
            Show();
        }));
        buttons.Controls.Add(MakeButton("Min width 700", (_, _) =>
            MinimumSize = MinimumSize.Width == 700 ? Size.Empty : new Size(700, 200)));
        buttons.Controls.Add(MakeButton("Minimize", (_, _) => WindowState = FormWindowState.Minimized));
        buttons.Controls.Add(MakeButton("Close", (_, _) => Close()));

        Controls.Add(_info);
        Controls.Add(buttons);
        _refresh.Tick += (_, _) => UpdateInfo();
        _refresh.Start();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_frameless)
        {
            using var bar = new SolidBrush(Color.FromArgb(48, 48, 56));
            e.Graphics.FillRectangle(bar, 0, 0, ClientSize.Width, 36);
            TextRenderer.DrawText(e.Graphics, Text + "  (frameless)", Font, new Point(12, 10), ForeColor);
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (_frameless && m.Msg == WmNcHitTest && (int)m.Result == HtClient)
        {
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            const int grip = 6;
            var left = p.X < grip;
            var right = p.X >= ClientSize.Width - grip;
            var top = p.Y < grip;
            var bottom = p.Y >= ClientSize.Height - grip;
            m.Result = (top, bottom, left, right) switch
            {
                (true, _, true, _) => HtTopLeft,
                (true, _, _, true) => HtTopRight,
                (_, true, true, _) => HtBottomLeft,
                (_, true, _, true) => HtBottomRight,
                (true, _, _, _) => HtTop,
                (_, true, _, _) => HtBottom,
                (_, _, true, _) => HtLeft,
                (_, _, _, true) => HtRight,
                _ when p.Y < 36 => HtCaption,
                _ => HtClient,
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _info.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateInfo()
    {
        var screen = Screen.FromControl(this);
        _info.Text =
            $"{Text}\n\n" +
            $"Bounds      {Bounds}\n" +
            $"Client      {ClientSize.Width}x{ClientSize.Height}\n" +
            $"State       {WindowState}\n" +
            $"DPI         {DeviceDpi}\n" +
            $"Screen      {screen.DeviceName} {screen.Bounds}\n" +
            $"Work area   {screen.WorkingArea}\n" +
            $"Min size    {MinimumSize}\n" +
            $"Top-most    {TopMost}\n\n" +
            "Drag me to a screen edge to dock, away to undock.\nDouble-tap Ctrl to hide/show.";
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, Margin = new Padding(0, 0, 8, 0) };
        b.Click += onClick;
        return b;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
