using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Forms = System.Windows.Forms;

namespace WaveFix;

public partial class App : Application
{
    Mutex? _single;
    Forms.NotifyIcon? _tray;
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\WaveFix.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: tell it to show its window, then quit.
            EventWaitHandle.OpenExisting(@"Local\WaveFix.Show").Set();
            Shutdown();
            return;
        }
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WaveFix.Show");
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        var settings = AppSettings.Load();
        _window = new MainWindow(settings);

        _tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "WaveFix", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open WaveFix", null, (_, _) => ShowWindow());
        menu.Items.Add("Mute / unmute mic", null, (_, _) => _window.Mic.Muted = !_window.Mic.Muted);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowWindow(); };

        if (!e.Args.Contains("--tray")) _window.Show();
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_window != null) { _window.ReallyClose = true; _window.Close(); }
        Shutdown();
    }

    /// <summary>Draws the tray icon (a green mic capsule) so no image file is needed.</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var green = new SolidBrush(Color.FromArgb(0x3D, 0xDC, 0x97));
            using var pen = new Pen(Color.FromArgb(0x3D, 0xDC, 0x97), 3);
            using var body = new GraphicsPath();
            body.AddArc(10, 2, 12, 12, 180, 180);
            body.AddArc(10, 9, 12, 12, 0, 180);
            body.CloseFigure();
            g.FillPath(green, body);
            g.DrawArc(pen, 6, 8, 20, 18, 0, 180);
            g.DrawLine(pen, 16, 26, 16, 30);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
