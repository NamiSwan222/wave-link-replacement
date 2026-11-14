using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WaveFix;

/// <summary>A plain child window the plugin draws its own interface into.</summary>
sealed class PluginFrame : HwndHost
{
    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern bool DestroyWindow(IntPtr h);
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;

    public IntPtr Hwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        Hwnd = CreateWindowExW(0, "STATIC", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 400, 300, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
}

/// <summary>
/// Runs as "WaveFix.exe --edit-plugin &lt;dll&gt; &lt;state.json&gt; &lt;title&gt;" in its own process, so a
/// misbehaving plugin window can't take WaveFix down. Every change is written to state.json,
/// which the main window picks up and sends to the audio engine.
/// </summary>
public static class PluginEditor
{
    /// <summary>
    /// Feeds the plugin the live audio entering its stage in WaveFix's engine, so its window can
    /// show meters, graphs and readings. Keeps the mic open while the window is up (the engine
    /// only runs while something records), on one dedicated audio thread as plugins expect.
    /// </summary>
    sealed class LiveFeed : IDisposable
    {
        readonly VstPlugin _plugin;
        readonly string _mic;
        readonly int _stage;
        readonly Thread _thread;
        volatile bool _stop;
        public volatile string State = "Starting…";
        public volatile bool Live;

        public LiveFeed(VstPlugin plugin, string mic, int stage)
        {
            _plugin = plugin; _mic = mic; _stage = stage;
            _thread = new Thread(Run) { IsBackground = true, Name = "Plugin live audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        void Run()
        {
            AudioBridge? bridge = null;
            NAudio.CoreAudioApi.WasapiCapture? capture = null;
            try
            {
                bridge = AudioBridge.Create();
                bridge.TapStage = _stage;
                bridge.TapBeat();
                try
                {
                    // Something must record the mic for Windows to run its effects (and our tap).
                    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    capture = new NAudio.CoreAudioApi.WasapiCapture(en.GetDevice(_mic));
                    capture.StartRecording();
                }
                catch { /* another app may be recording it anyway */ }

                var buf = new float[2048];
                long pos = -1; int rate = 0;
                var lastData = Environment.TickCount64;
                while (!_stop)
                {
                    bridge.TapBeat();
                    long written = bridge.TapWritten;
                    int r = bridge.TapRate;
                    if (r > 0 && r != rate) { rate = r; _plugin.StartProcessing(rate); pos = -1; }
                    if (rate > 0 && written > 0)
                    {
                        // Start (or catch up) about 20 ms behind the engine.
                        if (pos < 0 || written - pos > AudioBridge.RingLen / 2 || pos > written) pos = Math.Max(0, written - rate / 50);
                        while (pos < written)
                        {
                            int n = (int)Math.Min(buf.Length, written - pos);
                            bridge.ReadTap(pos, buf.AsSpan(0, n));
                            _plugin.Process(buf.AsSpan(0, n));
                            pos += n;
                            lastData = Environment.TickCount64;
                        }
                    }
                    Live = Environment.TickCount64 - lastData < 1000 && rate > 0;
                    State = Live ? "Live: showing your mic as it reaches this plugin." : "Waiting for your mic… (talk, or check WaveFix is set up on this mic)";
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex) { State = $"Live audio isn't available ({ex.Message})."; }
            finally
            {
                try { if (bridge != null) bridge.TapStage = 0; } catch { }
                try { capture?.StopRecording(); capture?.Dispose(); } catch { }
                try { _plugin.StopProcessing(); } catch { }
                bridge?.Dispose();
            }
        }

        public void Dispose() { _stop = true; _thread.Join(2000); }
    }

    public static void Run(Application app, string dll, string statePath, string title, string? mic = null, int stage = 0)
    {
        VstPlugin plugin;
        try { plugin = VstPlugin.Load(dll); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {title}: {ex.Message}", "WaveFix", MessageBoxButton.OK, MessageBoxImage.Warning);
            app.Shutdown(1);
            return;
        }

        PluginState? saved = null;
        try { if (File.Exists(statePath)) saved = JsonSerializer.Deserialize<PluginState>(File.ReadAllText(statePath)); } catch { }
        try { plugin.WriteState(saved); } catch { }
        var last = SafeRead(plugin) ?? new PluginState();

        var win = new Window
        {
            Title = $"{title} settings",
            Background = (Brush)app.FindResource("Bg"),
            FontFamily = (FontFamily)app.FindResource("AppFont"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.CanMinimize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            UseLayoutRounding = true,
        };
        try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WaveFix.ico")); } catch { }

        var root = new DockPanel { Margin = new Thickness(18), MinWidth = 380 };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = plugin.Name.Length > 0 ? plugin.Name : title, FontSize = 18, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock
        {
            Text = (plugin.Vendor.Length > 0 ? plugin.Vendor + " · " : "") + "Changes apply to your mic as you make them.",
            Style = (Style)app.FindResource("Hint"), Margin = new Thickness(0, 3, 0, 0),
        });
        var liveText = new TextBlock { Style = (Style)app.FindResource("Hint"), Margin = new Thickness(0, 3, 0, 0) };
        if (mic != null && stage > 0) header.Children.Add(liveText);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var done = new Button { Style = (Style)app.FindResource("PrimaryButton"), Height = 40, Padding = new Thickness(22, 0, 22, 0), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        done.Content = new TextBlock { Text = "Done", FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        done.Click += (_, _) => win.Close();
        DockPanel.SetDock(done, Dock.Bottom);
        root.Children.Add(done);

        void Save()
        {
            var now = SafeRead(plugin);
            if (now == null || now.SameAs(last)) return;
            last = now;
            try
            {
                var tmp = statePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(now));
                File.Move(tmp, statePath, overwrite: true);
            }
            catch { /* the next tick tries again */ }
        }

        var idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var saver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saver.Tick += (_, _) => Save();

        if (plugin.HasEditor)
        {
            var frame = new PluginFrame { Width = 400, Height = 300 };
            var holder = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = frame };
            root.Children.Add(holder);

            void SizeFrame(int pw, int ph)
            {
                var scale = VisualTreeHelper.GetDpi(win).DpiScaleX;
                frame.Width = Math.Max(120, pw / scale);
                frame.Height = Math.Max(80, ph / scale);
            }
            VstPlugin.SizeWindowRequested = (w, h) => win.Dispatcher.BeginInvoke(() => SizeFrame(w, h));
            win.ContentRendered += (_, _) =>
            {
                if (win.Tag != null) return;
                win.Tag = "opened";
                try
                {
                    var (w, h) = plugin.OpenEditor(frame.Hwnd);
                    SizeFrame(w, h);
                    idle.Tick += (_, _) => { try { plugin.EditorIdle(); } catch { } };
                    idle.Start();
                }
                catch (Exception ex) { MessageBox.Show($"The plugin's window couldn't open: {ex.Message}", "WaveFix"); }
            };
        }
        else
        {
            root.Children.Add(GenericEditor(app, plugin));
        }

        win.Content = root;
        LiveFeed? feed = null;
        var liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        liveTimer.Tick += (_, _) =>
        {
            if (feed == null) return;
            liveText.Text = feed.State;
            liveText.Foreground = (Brush)app.FindResource(feed.Live ? "Good" : "SubText");
        };
        win.ContentRendered += (_, _) =>
        {
            // After the plugin's own window is open: start feeding it live audio.
            if (feed != null || mic == null || stage <= 0) return;
            feed = new LiveFeed(plugin, mic, stage);
            liveTimer.Start();
        };
        win.Closing += (_, _) =>
        {
            liveTimer.Stop();
            feed?.Dispose(); // stops the audio thread before the plugin goes away
            idle.Stop(); saver.Stop();
            Save();
            try { if (plugin.HasEditor) plugin.CloseEditor(); } catch { }
        };
        win.Closed += (_, _) => { plugin.Dispose(); app.Shutdown(0); };
        saver.Start();
        win.Show();
    }

    static PluginState? SafeRead(VstPlugin p) { try { return p.ReadState(); } catch { return null; } }

    /// <summary>Sliders for plugins that don't have their own window.</summary>
    static FrameworkElement GenericEditor(Application app, VstPlugin plugin)
    {
        var list = new StackPanel { Width = 460 };
        if (plugin.NumParams == 0)
            list.Children.Add(new TextBlock { Text = "This plugin has no settings to change.", Style = (Style)app.FindResource("Hint") });
        for (int i = 0; i < plugin.NumParams; i++)
        {
            int idx = i;
            var name = plugin.ParamName(i);
            var value = new TextBlock { Foreground = (Brush)app.FindResource("SubText"), FontWeight = FontWeights.SemiBold, Text = plugin.ParamDisplay(i) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = name.Length > 0 ? name : $"Setting {i + 1}", Style = (Style)app.FindResource("Label") });
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = plugin.GetParam(i), Margin = new Thickness(0, 0, 0, 14), SmallChange = 0.01, LargeChange = 0.1 };
            System.Windows.Automation.AutomationProperties.SetName(slider, name);
            slider.ValueChanged += (_, e) => { plugin.SetParam(idx, (float)e.NewValue); value.Text = plugin.ParamDisplay(idx); };
            list.Children.Add(row);
            list.Children.Add(slider);
        }
        var card = new Border { Style = (Style)app.FindResource("Card"), Child = list };
        return new ScrollViewer { Content = card, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
