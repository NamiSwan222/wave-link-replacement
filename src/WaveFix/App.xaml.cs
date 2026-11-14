using System.Drawing;
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

        // Headless helper roles used by one-click setup (no window, no tray, no single-instance check).
        if (e.Args.Length >= 3 && e.Args[0] == "--setup")
        {
            Setup.Migrate = e.Args.Contains("--migrate");
            var rest = e.Args.Skip(3).ToList();
            int pi = rest.IndexOf("--plugins");
            string? list = pi >= 0 && pi + 1 < rest.Count ? rest[pi + 1] : null;
            if (pi >= 0) rest.RemoveRange(pi, Math.Min(2, rest.Count - pi));
            var plugin = rest.FirstOrDefault(a => !a.StartsWith("--"));
            Shutdown(Setup.RunElevated(e.Args[1], e.Args[2], plugin, list));
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--remove-engine") { Shutdown(Setup.RunRemoveElevated(e.Args[1])); return; }
        if (e.Args.Length >= 1 && e.Args[0] == "--uninstall")
        {
            // Run by the uninstaller: take the engine off every mic and remove it (one UAC prompt).
            Shutdown(Setup.EngineLeftovers && !Setup.RemoveEngineAsync().GetAwaiter().GetResult() ? 1 : 0);
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--setup-undo") { Shutdown(Setup.RunUndoElevated(e.Args[1], e.Args[2])); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--plugin-install") { Shutdown(Setup.PluginInstall(e.Args[1], e.Args[2], e.Args[3])); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--edit-plugin") { PluginEditor.Run(this, e.Args[1], e.Args[2], e.Args[3]); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--system-attach") { var ri = Array.IndexOf(e.Args, "--result");
            Shutdown(Setup.SystemAttach(e.Args[1], e.Args[2], e.Args.Contains("--undo"), e.Args.Contains("--migrate"), ri > 0 && ri + 1 < e.Args.Length ? e.Args[ri + 1] : null)); return; }

        bool updated = e.Args.Contains("--updated");

        _single = new Mutex(true, @"Local\WaveFix.SingleInstance", out bool first);
        if (!first)
        {
            // After an update the old copy may still be closing: wait for it instead of quitting.
            bool got = false;
            if (updated)
            {
                try { got = _single.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { got = true; }
            }
            if (!got)
            {
                // Already running: tell it to show its window, then quit.
                try { EventWaitHandle.OpenExisting(@"Local\WaveFix.Show").Set(); } catch { }
                Shutdown();
                return;
            }
        }
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WaveFix.Show");
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        var settings = AppSettings.Load();
        _window = new MainWindow(settings);

        _tray = new Forms.NotifyIcon { Icon = LoadIcon(), Text = $"WaveFix {Updater.Current}", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open WaveFix", null, (_, _) => ShowWindow());
        menu.Items.Add("Mute / unmute mic", null, (_, _) => _window.Mic.Muted = !_window.Mic.Muted);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowWindow(); };

        if (updated)
        {
            Osd.Show(_window, "", $"WaveFix updated to {Updater.Current}");
            Updater.CleanupDownloads();
        }
        else if (!e.Args.Contains("--tray"))
            _window.Show();
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        if (_window != null) { _window.ReallyClose = true; _window.Close(); }
        try { _single?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    /// <summary>The app icon embedded in the exe (Assets\WaveFix.ico), at tray size.</summary>
    static Icon LoadIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/WaveFix.ico"));
            if (info != null) return new Icon(info.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }
}
