using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WaveFix;

public partial class MainWindow : Window
{
    public MicController Mic { get; } = new();
    public AppMixer Mixer { get; } = new();

    readonly AppSettings _settings;
    readonly EffectsEngine _engine = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _pttTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly HotkeyManager _hotkeys;
    readonly ObservableCollection<ShortcutRow> _shortcutRows = new();
    readonly ObservableCollection<PluginEntry> _plugins;
    ShortcutRow? _recording;
    bool _loading = true;

    public bool ReallyClose { get; set; }

    static readonly (string Id, string Label, string Desc)[] Tones =
    {
        (EqPresets.Flat, "Natural", "Your real voice"),
        (EqPresets.Warm, "Warm", "Fuller and cozy"),
        (EqPresets.Clear, "Clear", "Crisp, easy to hear"),
        (EqPresets.Broadcast, "Broadcast", "Radio-host polish"),
        (EqPresets.Radio, "Radio", "Old-school walkie"),
        (EqPresets.Custom, "Custom", "Your own sliders"),
    };

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _plugins = new ObservableCollection<PluginEntry>(_settings.Plugins);
        InitializeComponent();
        DataContext = this;

        // Microphone page
        foreach (var t in Tones)
        {
            var rb = new RadioButton { Content = t.Label, Tag = t.Desc, GroupName = "tone", Style = (Style)FindResource("ToneChoice"), IsChecked = t.Id == _settings.EqPreset };
            System.Windows.Automation.AutomationProperties.SetName(rb, $"Voice tone {t.Label}");
            var id = t.Id;
            rb.Checked += (_, _) => { if (_loading) return; _settings.EqPreset = id; ApplyEffects(); };
            PresetPanel.Children.Add(rb);
        }
        NoiseSwitch.IsChecked = _settings.NoiseRemoval;
        LowCutSwitch.IsChecked = _settings.LowCut;
        BoostSlider.Value = _settings.VoiceBoostDb;
        UpdateBoostLabel();
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
        AutoUpdateSwitch.IsChecked = _settings.AutoUpdate;
        InitVoiceAndPolish();
        VersionText.Text = $"Version {Updater.Current} · Check for updates";

        Mic.DeviceChanged += () =>
        {
            if (Mic.DeviceId != null && !_loading) { _settings.MicDeviceId = Mic.DeviceId; _settings.Save(); }
            ApplyEffects();
        };
        Mic.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MicController.Muted)) UpdateMuteLabel(); };
        Mic.RefreshDevices(_settings.MicDeviceId);
        UpdateMuteLabel();
        ChooseEngine();

        // Mixer page
        if (!Mixer.RoutingAvailable)
            MixerSubtitle.Text += " (Choosing where an app plays isn't available on this version of Windows.)";
        RefreshMixer();

        // Plugins page
        PluginList.ItemsSource = _plugins;
        _plugins.CollectionChanged += (_, _) => EmptyPlugins.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPlugins.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Shortcuts page
        _hotkeys = new HotkeyManager(this);
        _hotkeys.Pressed += OnHotkey;
        foreach (var a in ShortcutAction.All)
        {
            var row = new ShortcutRow(a);
            if (_settings.Shortcuts.TryGetValue(a.Id, out var text) && Hotkey.Parse(text) is { } hk)
            {
                row.Key = hk;
                row.Error = _hotkeys.Set(a.Id, hk);
            }
            _shortcutRows.Add(row);
        }
        ShortcutList.ItemsSource = _shortcutRows;
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); _settings.Save(); };
        InitSoundboard();

        _meterTimer.Tick += (_, _) =>
        {
            Mic.PollMeter();
            MeterFill.Width = Math.Max(0, MeterHost.ActualWidth * Mic.Level);
            MeterHint.Visibility = Mic.Level > 0.02 ? Visibility.Hidden : Visibility.Visible;
        };
        _slowTimer.Tick += (_, _) => { RefreshMixer(); UpdateStatus(); UpdateSoundboardStatus(); };
        _pttTimer.Tick += (_, _) =>
        {
            if (_hotkeys.IsHeld("push-to-talk")) return;
            _pttTimer.Stop();
            Mic.Muted = true;
            Osd.Show(this, "", "Mic muted");
        };
        _meterTimer.Start();
        _slowTimer.Start();

        // Updates: first check shortly after start, then every 6 hours.
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = TimeSpan.FromHours(6); _ = CheckForUpdates(userAsked: false); };
        _updateTimer.Start();

        // Setup page
        foreach (var t in Setup.StepTitles) _setupSteps.Add(new SetupStepRow(t));
        SetupSteps.ItemsSource = _setupSteps;
        RefreshSetupChecklist();
        Mic.DeviceChanged += () => { ChooseEngine(); RefreshSetupChecklist(); UpdateStatus(); };

        _loading = false;
        if (Mic.DeviceId != null) _settings.MicDeviceId = Mic.DeviceId;
        ApplyEffects();

        // First run on a PC that isn't set up yet: open on the Setup page.
        if (Mic.DeviceId != null && !Setup.IsComplete(Mic.DeviceId)) { NavSetup.Visibility = Visibility.Visible; NavSetup.IsChecked = true; }
    }

    // ---------- Navigation ----------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageMic == null || PageSetup == null) return; // fires during InitializeComponent
        PageSetup.Visibility = NavSetup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMic.Visibility = NavMic.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageVoice.Visibility = NavVoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSoundboard.Visibility = NavSoundboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMixer.Visibility = NavMixer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PagePlugins.Visibility = NavPlugins.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = NavShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StopRecording();
    }

    // ---------- Microphone ----------

    void UpdateMuteLabel()
    {
        MuteLabel.Text = Mic.Muted ? "Muted, click to unmute" : "Mic is on, click to mute";
        MuteIcon.Text = Mic.Muted ? "" : "";
    }

    void RefreshMixer()
    {
        try { Mixer.Refresh(); } catch { }
        EmptyMixer.Visibility = Mixer.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // These run only on a real click (Click, not Checked/Unchecked), and each switch only ever
    // writes its own setting, so nothing that changes a switch in code can overwrite a choice.
    void NoiseSwitch_Click(object sender, RoutedEventArgs e) => SetNoiseRemoval(NoiseSwitch.IsChecked == true, "switch clicked");
    void LowCutSwitch_Click(object sender, RoutedEventArgs e) => SetLowCut(LowCutSwitch.IsChecked == true, "switch clicked");

    void SetNoiseRemoval(bool on, string why)
    {
        NoiseSwitch.IsChecked = on;
        if (_settings.NoiseRemoval == on) return;
        _settings.NoiseRemoval = on;
        ChangeLog.Write($"Noise removal {(on ? "on" : "off")} ({why})");
        ApplyEffects();
    }

    void SetLowCut(bool on, string why)
    {
        LowCutSwitch.IsChecked = on;
        if (_settings.LowCut == on) return;
        _settings.LowCut = on;
        ChangeLog.Write($"Rumble filter {(on ? "on" : "off")} ({why})");
        ApplyEffects();
    }

    void Boost_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBoostLabel();
        if (_loading) return;
        _settings.VoiceBoostDb = BoostSlider.Value;
        ApplyEffects();
    }

    void UpdateBoostLabel()
    {
        var v = BoostSlider.Value;
        BoostValue.Text = v switch { 0 => "Off", > 0 => $"+{v:0.#} dB", _ => $"{v:0.#} dB" };
        BoostValue.Foreground = (Brush)FindResource(v > 12 ? "Warn" : "SubText");
    }

    void ApplyEffects()
    {
        if (_loading) return;
        _settings.Plugins = _plugins.ToList();
        _settings.Save();
        if (Mic.DeviceId == null) return;
        try { _engine.Apply(_settings, Mic.DeviceId); }
        catch (Exception ex) { SetStatus(StatusKind.Bad, "Couldn't save your effects", ex.Message, "Effects not saved"); return; }
        UpdateStatus();
    }

    enum StatusKind { Good, Warn, Bad }

    void UpdateStatus()
    {
        bool needsSetup = Mic.DeviceId != null && !Setup.IsComplete(Mic.DeviceId);
        BannerAction.Visibility = needsSetup && !_setupRunning ? Visibility.Visible : Visibility.Collapsed;
        // Always reachable: it also holds "Copy details for help" and "Take the engine off this mic".
        NavSetup.Visibility = Visibility.Visible;
        if (!_setupRunning) RefreshSetupChecklist();

        if (Mic.DeviceId == null)
        { SetStatus(StatusKind.Bad, "No microphone found", "Plug in your mic and it'll show up here.", "No mic"); return; }
        if (Setup.Migrate)
        { SetStatus(StatusKind.Warn, "Switch to WaveFix's own engine", "WaveFix doesn't need Equalizer APO any more. One click moves your mic, settings and plugins over and removes it.", "Switch needed"); return; }
        if (!_engine.IsInstalled)
        {
            SetStatus(StatusKind.Bad, "Let's finish setting up", "WaveFix needs to install its audio engine to run your effects. It does it for you in one click.", "Setup needed");
            return;
        }
        if (WaveFixEngine.NeedsUpdate)
        { SetStatus(StatusKind.Warn, "Update the audio engine", "A newer WaveFix audio engine is ready. One click installs it (Windows asks for permission once).", "Update ready"); return; }
        if (Process.GetProcessesByName("Elgato.WaveLink").Length > 0)
        { SetStatus(StatusKind.Warn, "Wave Link is still open", "Close Wave Link. While it runs, people watching your Discord stream can hear your mic.", "Close Wave Link"); return; }

        switch (_engine.IsConnected(Mic.DeviceId))
        {
            case false:
                SetStatus(StatusKind.Bad, "Effects aren't connected to this mic", "One click connects them, so every app, Discord included, hears your cleaned-up voice.", "Setup needed"); break;
            default:
                if (_settings.NoiseRemoval && !_engine.HasNoisePlugin)
                    SetStatus(StatusKind.Warn, "Noise removal is missing a file", "One click downloads and adds the noise removal plugin.", "Missing file");
                else if (Mic.Muted)
                    SetStatus(StatusKind.Warn, "Your mic is muted", "Nobody can hear you right now. Click the mute button or use your shortcut to talk.", "Mic muted");
                else
                    SetStatus(StatusKind.Good, "You're all set", "Your effects work in Discord, OBS and every other app, and your mic stays out of Discord streams.", "All good");
                break;
        }
    }

    void SetStatus(StatusKind kind, string title, string text, string pill)
    {
        var (fg, soft, glyph) = kind switch
        {
            StatusKind.Good => ("Good", "GoodSoft", ""),
            StatusKind.Warn => ("Warn", "WarnSoft", ""),
            _ => ("Bad", "BadSoft", ""),
        };
        BannerTitle.Text = title;
        BannerText.Text = text;
        BannerIcon.Text = glyph;
        BannerIconBg.Background = (Brush)FindResource(fg);
        Banner.Background = (Brush)FindResource(soft);
        StatusPill.Background = (Brush)FindResource(soft);
        StatusDot.Fill = (Brush)FindResource(fg);
        StatusPillText.Text = pill;
    }

    void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupRegistration.Set(StartupSwitch.IsChecked == true);
        _settings.StartWithWindows = StartupSwitch.IsChecked == true;
        _settings.Save();
    }

    // ---------- Engine ----------

    /// <summary>
    /// Setups from before WaveFix had its own engine ran on Equalizer APO: send those to the Setup
    /// page, where one run switches the mic over, moves the plugins and removes Equalizer APO.
    /// </summary>
    void ChooseEngine()
    {
        // Checked for the mic picked right now (and again whenever another is picked). "wavefix" is
        // only saved after a setup run succeeds, never from a guess at startup.
        bool onEapo = (Mic.DeviceId != null && EqualizerApo.IsOnMic(Mic.DeviceId))
            || (_settings.Engine != "wavefix" && (EqualizerApo.Installed || _settings.Engine == "eapo"))
            || _settings.Plugins.Any(p => !p.Path.StartsWith(WaveFixEngine.PluginDir, StringComparison.OrdinalIgnoreCase));
        bool was = Setup.Migrate;
        Setup.Migrate = onEapo && _settings.Existed;
        if (Setup.Migrate && !was) ChangeLog.Write($"Setup from before WaveFix's engine found on {Mic.DeviceId}: switching it over");
        if (Setup.Migrate != was && _setupSteps.Count > 0)
        {
            _setupSteps.Clear();
            foreach (var t in Setup.StepTitles) _setupSteps.Add(new SetupStepRow(t));
        }
        SetupNote.Text = Setup.Migrate
            ? "WaveFix now has its own audio engine, so it no longer needs Equalizer APO. Windows asks for permission once; your sound drops for a few seconds while Windows audio restarts. Your settings and plugins come along, your mic's original effects are put back, and Equalizer APO is uninstalled (unless another device still uses it)."
            : "Windows will ask for permission once. Your sound drops for a few seconds at the end while Windows audio restarts. WaveFix installs its own audio engine and adds itself after any effects your mic already has (they keep working). It downloads the RNNoise plugin from GitHub and checks it against a known fingerprint. Steps that are already done are skipped.";
    }

    /// <summary>After a successful switch: plugins now live in Program Files, the mic runs on the engine.</summary>
    void FinishSwitch()
    {
        foreach (var p in _plugins)
        {
            var moved = Path.Combine(WaveFixEngine.PluginDir, Path.GetFileName(p.Path));
            if (File.Exists(moved)) p.Path = moved;
            else p.Enabled = false; // couldn't be moved (gone, or not a usable plugin)
        }
        _settings.Engine = "wavefix";
        Setup.Migrate = false;
        ChangeLog.Write("Switched to WaveFix's engine");
        ChooseEngine();
    }

    async void UndoSetup_Click(object sender, RoutedEventArgs e)
    {
        if (Mic.DeviceId is not { } mic) return;
        if (MessageBox.Show(this, "Take WaveFix's engine off this mic? Its original Windows effect settings are put back, and your sound drops for a few seconds while Windows audio restarts.",
                "WaveFix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        UndoSetupButton.IsEnabled = false;
        bool ok = await Setup.UndoAsync(mic);
        UndoSetupButton.IsEnabled = true;
        ChangeLog.Write($"Engine taken off mic {mic}: {(ok ? "done" : "failed or declined")}");
        _setupShowsResult = false;
        RefreshSetupChecklist();
        UpdateStatus();
        ShowSetupResult(ok, ok ? "Done. This mic is back to its original Windows settings." : "That didn't finish (or permission was declined). Nothing was changed.", showHelp: !ok);
    }

    // ---------- Setup ----------

    readonly ObservableCollection<SetupStepRow> _setupSteps = new();
    bool _setupRunning, _setupShowsResult;

    void OpenSetup_Click(object sender, RoutedEventArgs e)
    {
        _setupShowsResult = false; // a new problem appeared: show a fresh checklist
        SetupResult.Visibility = Visibility.Collapsed;
        NavSetup.Visibility = Visibility.Visible;
        NavSetup.IsChecked = true;
    }

    void RefreshSetupChecklist()
    {
        // Keep a finished run's results on screen (Installed / Connected / Audio is back).
        if (_setupRunning || _setupShowsResult || Mic.DeviceId == null || _setupSteps.Count == 0) return;
        var done = Setup.DoneSteps(Mic.DeviceId);
        string[] todo =
        {
            "WaveFix's own engine that runs your effects inside Windows audio.",
            Setup.Migrate ? "Noise removal and your own plugins move to WaveFix's protected folder." : "AI noise removal (RNNoise), checked and copied into place.",
            Setup.Migrate ? "Your mic's original effects come back, WaveFix's engine joins them, Equalizer APO goes." : "Puts the effects on this mic so Discord, OBS and every app get them.",
            "Only if something changed. Sound drops for a few seconds.",
        };
        for (int i = 0; i < _setupSteps.Count; i++)
        {
            _setupSteps[i].State = done[i] ? "DONE" : "WAIT";
            _setupSteps[i].Message = done[i] ? "Already done" : todo[i];
        }
        bool complete = Setup.IsComplete(Mic.DeviceId);
        SetupButtonText.Text = complete ? "Everything's set up" : "Set it up for me";
        SetupButton.IsEnabled = !complete;
        UndoSetupButton.Visibility = !Setup.Migrate && WaveFixEngine.IsAttached(Mic.DeviceId) ? Visibility.Visible : Visibility.Collapsed;
    }

    void RunSetup_Click(object sender, RoutedEventArgs e) => _ = RunSetup(null);

    async Task RunSetup(string? localPlugin)
    {
        if (_setupRunning || Mic.DeviceId == null) return;
        var micId = Mic.DeviceId;
        _setupRunning = true;
        SetupButton.IsEnabled = false;
        SetupButtonText.Text = "Setting up…";
        SetupResult.Visibility = Visibility.Collapsed;
        foreach (var s in _setupSteps) if (s.State != "DONE") { s.State = "WAIT"; }
        int failedStep = -1;

        var progress = new Progress<(int step, string state, string message)>(p =>
        {
            if (p.step >= 0 && p.step < _setupSteps.Count)
            {
                _setupSteps[p.step].State = p.state;
                _setupSteps[p.step].Message = p.state == "SKIP" ? "Already done" : p.message;
                if (p.state == "FAIL") failedStep = p.step;
            }
            else if (p.state == "FAIL")
            {
                foreach (var s in _setupSteps.Where(s => s.State == "RUN"))
                {
                    failedStep = _setupSteps.IndexOf(s);
                    s.State = "FAIL"; s.Message = "Didn't finish";
                }
                ShowSetupResult(false, p.message, showHelp: true, pluginHelp: failedStep == 1);
            }
        });

        bool ok;
        // When switching from Equalizer APO, plugins in the old shared folder move to Program Files.
        var toMove = Setup.Migrate ? _plugins.Select(p => p.Path).Where(File.Exists).ToList() : null;
        try { ok = await Setup.RunAsync(micId, progress, localPlugin, toMove); }
        catch (Exception ex) { ok = false; ShowSetupResult(false, ex.Message); }
        _setupRunning = false;
        _setupShowsResult = ok;

        if (ok)
        {
            if (Setup.Migrate) FinishSwitch();
            else if (_settings.Engine != "wavefix") { _settings.Engine = "wavefix"; _settings.Save(); }
            _settings.MicDeviceId = micId;
            ApplyEffects(); // write the effect chain for this mic now that the engine is in place
            ShowSetupResult(true, "All done! Your effects now work in Discord, OBS and every other app. Head to Microphone to tune your sound.");
            SetupButtonText.Text = "Everything's set up";
            SetupButton.IsEnabled = false;
        }
        else
        {
            if (SetupResult.Visibility != Visibility.Visible)
                ShowSetupResult(false, "Setup didn't finish. Nothing harmful was left half-done; you can try again.", showHelp: true, pluginHelp: false);
            SetupButtonText.Text = "Try again";
            SetupButton.IsEnabled = true;
        }
        UpdateStatus();
    }

    void PickPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick win-rnnoise.zip (or rnnoise_mono.dll) that you downloaded",
            Filter = "RNNoise download (*.zip;*.dll)|*.zip;*.dll|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = RunSetup(dlg.FileName);
    }

    void OpenPluginPage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Setup.RnnoiseReleasePage) { UseShellExecute = true });

    void CopySetupLog_Click(object sender, RoutedEventArgs e) =>
        CopyLogText.Text = ShareDetails() ? "Copied!" : "Saved to your Desktop";

    void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ShareDetails()) Osd.Show(this, "", "Details copied: paste them in a message");
    }

    /// <summary>
    /// Copies the details for help. Another program can hold the clipboard for a moment (clipboard
    /// managers, remote desktop, overlays): keep trying for about a second, and if it's still busy,
    /// save them as a file on the Desktop and open it. True if they went to the clipboard.
    /// </summary>
    bool ShareDetails()
    {
        var text = Diagnostics.Collect(Mic.DeviceId);
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(100); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(100); }
        }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var file = Path.Combine(Directory.Exists(desktop) ? desktop : Path.GetTempPath(), "WaveFix details.txt");
            File.WriteAllText(file, text);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
            MessageBox.Show(this, $"Another program is using the clipboard, so the details were saved to a file instead:\n\n{file}\n\nSend that file (it's open in Notepad), or copy its text from there.",
                "WaveFix", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't copy or save the details: {ex.Message}", "WaveFix"); }
        return false;
    }

    void ShowSetupResult(bool good, string text, bool showHelp = false, bool pluginHelp = false)
    {
        SetupHelp.Visibility = showHelp ? Visibility.Visible : Visibility.Collapsed;
        PickPluginButton.Visibility = pluginHelp ? Visibility.Visible : Visibility.Collapsed;
        OpenPluginPageButton.Visibility = pluginHelp ? Visibility.Visible : Visibility.Collapsed;
        CopyLogText.Text = "Copy details";
        if (pluginHelp)
            text += "\n\nYou can also get it yourself: open the download page, download win-rnnoise.zip, then click Use a file I downloaded and pick it.";
        SetupResult.Visibility = Visibility.Visible;
        SetupResult.Background = (Brush)FindResource(good ? "GoodSoft" : "BadSoft");
        SetupResultText.Text = text;
    }

    // ---------- Updates ----------

    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    Updater.Release? _pendingUpdate;
    bool _updateBusy;

    void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
        _settings.Save();
    }

    void CheckUpdates_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdates(userAsked: true);

    async Task CheckForUpdates(bool userAsked)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        if (userAsked) VersionText.Text = "Checking for updates…";
        try
        {
            var r = await Updater.CheckAsync();
            if (r == null)
            {
                if (userAsked) VersionText.Text = $"Version {Updater.Current} · You're up to date";
                return;
            }
            _pendingUpdate = r;
            VersionText.Text = $"Version {Updater.Current} · {r.Version} available";
            if (_settings.AutoUpdate && !userAsked) { _updateBusy = false; await InstallUpdate(); return; }
            ShowUpdateCard($"Version {r.Version} is ready. Your mic keeps working while it updates.", "Update now", enabled: true);
        }
        catch (Exception ex)
        {
            if (userAsked) VersionText.Text = "Couldn't check for updates. Are you online?";
            Debug.WriteLine(ex);
        }
        finally { _updateBusy = false; }
    }

    void UpdateNow_Click(object sender, RoutedEventArgs e) => _ = InstallUpdate();

    async Task InstallUpdate()
    {
        if (_pendingUpdate is not { } r || _updateBusy) return;
        _updateBusy = true;
        ShowUpdateCard($"Downloading version {r.Version}…", "Downloading…", enabled: false);
        try
        {
            var progress = new Progress<double>(p => UpdateButtonText.Text = $"Downloading… {p:P0}");
            var file = await Updater.DownloadAsync(r, progress);
            ShowUpdateCard($"Installing version {r.Version}. WaveFix will be back in a few seconds.", "Installing…", enabled: false);
            Updater.StartInstaller(file);
            ((App)Application.Current).Quit();
        }
        catch (Exception ex)
        {
            ShowUpdateCard($"The update didn't install: {ex.Message}", "Try again", enabled: true);
        }
        finally { _updateBusy = false; }
    }

    void ShowUpdateCard(string text, string button, bool enabled)
    {
        UpdateCard.Visibility = Visibility.Visible;
        UpdateTitle.Text = _pendingUpdate != null ? $"Update to {_pendingUpdate.Version}" : "Update available";
        UpdateText.Text = text;
        UpdateButtonText.Text = button;
        UpdateButton.IsEnabled = enabled;
    }

    // ---------- Plugin settings windows ----------

    readonly Dictionary<string, Process> _openEditors = new(StringComparer.OrdinalIgnoreCase);

    void NoiseSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.HasNoisePlugin) { NavSetup.Visibility = Visibility.Visible; NavSetup.IsChecked = true; return; }
        OpenPluginSettings(_engine.NoisePlugin, "Noise removal", () => _settings.NoiseState, s => _settings.NoiseState = s);
    }

    void NoiseReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.NoiseState = null;
        ApplyEffects();
        Osd.Show(this, "", "Noise removal reset to default");
    }

    void PluginSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginEntry p) return;
        OpenPluginSettings(p.Path, p.Name, () => p.State, s => p.State = s);
    }

    void PluginReset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginEntry p) return;
        p.State = null;
        ApplyEffects();
        Osd.Show(this, "", $"{p.Name} reset to defaults");
    }

    /// <summary>
    /// Opens a plugin's settings window in its own WaveFix process and applies every change to the
    /// audio engine while it's open (so you can hear it, e.g. in Discord's mic test).
    /// </summary>
    void OpenPluginSettings(string dll, string title, Func<PluginState?> get, Action<PluginState?> set)
    {
        if (_openEditors.TryGetValue(dll, out var running) && !running.HasExited)
        {
            Osd.Show(this, "", $"{title} settings are already open");
            return;
        }
        var stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wavefix-plugin-{Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(stateFile, System.Text.Json.JsonSerializer.Serialize(get() ?? new PluginState()));
        var stamp = System.IO.File.GetLastWriteTimeUtc(stateFile);

        Process? proc;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--edit-plugin"); psi.ArgumentList.Add(dll); psi.ArgumentList.Add(stateFile); psi.ArgumentList.Add(title);
            proc = Process.Start(psi);
        }
        catch (Exception ex) { ShowPluginError($"Couldn't open the settings: {ex.Message}"); return; }
        if (proc == null) return;
        _openEditors[dll] = proc;

        void Pick()
        {
            try
            {
                var t = System.IO.File.GetLastWriteTimeUtc(stateFile);
                if (t == stamp) return;
                stamp = t;
                var s = System.Text.Json.JsonSerializer.Deserialize<PluginState>(System.IO.File.ReadAllText(stateFile));
                if (s == null || s.IsEmpty) return;
                set(s);
                ApplyEffects(); // the engine reloads the chain with the new settings
            }
            catch { /* file mid-write: next tick */ }
        }

        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        poll.Tick += (_, _) =>
        {
            Pick();
            if (!proc.HasExited) return;
            poll.Stop();
            Pick();
            _openEditors.Remove(dll);
            if (proc.ExitCode != 0 && proc.ExitCode != 1)
                Osd.Show(this, "", $"{title}'s window closed unexpectedly. Your last changes were kept.");
            try { System.IO.File.Delete(stateFile); } catch { }
        };
        poll.Start();
    }

    // ---------- Plugins ----------

    void AddPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick a VST plugin (64-bit, .dll)",
            Filter = "VST2 plugins (*.dll)|*.dll|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = AddPluginFile(dlg.FileName);
    }

    async Task<bool> AddPluginFile(string file)
    {
        var problem = PluginStore.Validate(file);
        if (problem != null) { ShowPluginError(problem); return false; }
        try
        {
            var entry = await PluginStore.InstallAsync(file);
            if (entry == null) { ShowPluginError("Windows permission was declined, so the plugin wasn't added."); return false; }
            if (_plugins.Any(p => string.Equals(p.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
            { ShowPluginError($"{entry.Name} is already in your list."); return false; }
            _plugins.Add(entry);
            ShowPluginError(null);
            ApplyEffects();
            return true;
        }
        catch (Exception ex) { ShowPluginError($"Couldn't add the plugin: {ex.Message}"); return false; }
    }

    // ----- Scan -----

    readonly ObservableCollection<PluginStore.Found> _found = new();
    CancellationTokenSource? _scanCts;

    void ScanPlugins_Click(object sender, RoutedEventArgs e) => _ = RunScan(PluginStore.DefaultScanFolders(), "the usual plugin folders");

    void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick a folder that has VST plugins in it" };
        if (dlg.ShowDialog(this) != true) return;
        _ = RunScan(new[] { dlg.FolderName }, $"\"{dlg.FolderName}\"");
    }

    async Task RunScan(IReadOnlyList<string> folders, string where)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        ScanResults.ItemsSource = _found;
        _found.Clear();
        ScanCard.Visibility = Visibility.Visible;
        ShowPluginError(null);
        ScanButton.IsEnabled = false;
        ScanButtonText.Text = "Scanning…";
        ScanStatus.Text = $"Looking through {where}…";

        // Skip the built-in noise removal and plugins already in the list (by name: we keep copies).
        var have = new HashSet<string>(_plugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var skip = new[] { _engine.NoisePlugin, _engine.NoisePlugin.Replace("_mono", "_stereo") };
        try
        {
            var found = await Task.Run(() => PluginStore.Scan(folders, skip, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var f in found.Where(f => !have.Contains(f.Name))) _found.Add(f);
            int already = found.Count - _found.Count;
            ScanStatus.Text = folders.Count == 0
                ? "No standard plugin folders exist on this PC. Use Scan a folder… to pick where your plugins are."
                : _found.Count == 0
                    ? (already > 0 ? "Every plugin found is already in your list." : $"No 64-bit VST2 plugins found in {where}. If yours are somewhere else, use Scan a folder.")
                    : $"Found {_found.Count} plugin{(_found.Count == 1 ? "" : "s")} you can add" + (already > 0 ? $" ({already} already added)." : ".");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ScanStatus.Text = $"The scan stopped: {ex.Message}"; }
        finally
        {
            if (_scanCts == cts) { ScanButton.IsEnabled = true; ScanButtonText.Text = "Scan for plugins"; }
        }
    }

    async void AddFound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginStore.Found f) return;
        if (await AddPluginFile(f.Path)) _found.Remove(f);
        if (_found.Count == 0) ScanStatus.Text = "All added. Turn plugins on or off and reorder them below.";
    }

    void CloseScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        ScanCard.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        ScanButtonText.Text = "Scan for plugins";
    }

    void ShowPluginError(string? text)
    {
        PluginError.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        PluginErrorText.Text = text ?? "";
    }

    void PluginToggle_Click(object sender, RoutedEventArgs e) => ApplyEffects();

    void PluginUp_Click(object sender, RoutedEventArgs e) => MovePlugin(sender, -1);
    void PluginDown_Click(object sender, RoutedEventArgs e) => MovePlugin(sender, +1);

    void MovePlugin(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginEntry p) return;
        int i = _plugins.IndexOf(p), j = i + delta;
        if (i < 0 || j < 0 || j >= _plugins.Count) return;
        _plugins.Move(i, j);
        ApplyEffects();
    }

    void PluginRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PluginEntry p) return;
        _plugins.Remove(p);
        ApplyEffects(); // take it out of the chain first so the engine releases it
    }

    // ---------- Shortcuts ----------

    void ShortcutKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShortcutRow row) return;
        StopRecording();
        _recording = row;
        row.Error = null;
        row.Recording = true;
    }

    void ShortcutClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShortcutRow row) return;
        StopRecording();
        SaveShortcut(row, null);
    }

    void StopRecording()
    {
        if (_recording != null) _recording.Recording = false;
        _recording = null;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // wait for the real key
        var row = _recording;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { StopRecording(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { StopRecording(); SaveShortcut(row, null); return; }

        var mods = Keyboard.Modifiers;
        // F-keys and numpad keys work on their own: nobody types text with them.
        bool standalone = key is >= Key.F1 and <= Key.F24 || Hotkey.IsNumpad(key);
        if (mods == ModifierKeys.None && !standalone)
        {
            row.Error = "Add Ctrl, Alt or Shift so this key still works normally when you type. (F-keys and numpad keys work on their own.)";
            return; // keep listening
        }
        StopRecording();
        SaveShortcut(row, new Hotkey(mods, key));
    }

    void SaveShortcut(ShortcutRow row, Hotkey? key)
    {
        var error = _hotkeys.Set(row.Action.Id, key);
        if (error != null)
        {
            row.Error = error;
            // Keep the previous working shortcut if there was one.
            if (row.Key is { } old && _hotkeys.Set(row.Action.Id, old) == null) return;
            row.Key = null;
            _settings.Shortcuts.Remove(row.Action.Id);
            _settings.Save();
            return;
        }
        row.Error = null;
        row.Key = key;
        if (key is { } k) _settings.Shortcuts[row.Action.Id] = k.ToString();
        else _settings.Shortcuts.Remove(row.Action.Id);
        _settings.Save();
    }

    void OnHotkey(string action)
    {
        if (OnSoundHotkey(action)) return;
        switch (action)
        {
            case "toggle-mute":
                Mic.Muted = !Mic.Muted;
                Osd.Show(this, Mic.Muted ? "" : "", Mic.Muted ? "Mic muted" : "Mic on");
                break;
            case "push-to-talk":
                Mic.Muted = false;
                Osd.Show(this, "", "Talking…");
                _pttTimer.Start(); // re-mutes when the key is released
                break;
            case "toggle-noise":
                SetNoiseRemoval(!_settings.NoiseRemoval, "shortcut");
                Osd.Show(this, "", _settings.NoiseRemoval ? "Noise removal on" : "Noise removal off");
                break;
            case "boost-up":
                BoostSlider.Value = Math.Min(BoostSlider.Maximum, BoostSlider.Value + 3);
                Osd.Show(this, "", $"Mic boost {BoostValue.Text}");
                break;
            case "boost-down":
                BoostSlider.Value = Math.Max(BoostSlider.Minimum, BoostSlider.Value - 3);
                Osd.Show(this, "", $"Mic boost {BoostValue.Text}");
                break;
            case "next-tone":
                var buttons = PresetPanel.Children.OfType<RadioButton>().ToList();
                int i = buttons.FindIndex(b => b.IsChecked == true);
                var next = buttons[(i + 1) % buttons.Count];
                next.IsChecked = true;
                Osd.Show(this, "", $"Voice tone: {next.Content}");
                break;
            case "toggle-voice":
                SetVoiceChanger(!_settings.Voice.Enabled, "shortcut");
                Osd.Show(this, "", _settings.Voice.Enabled ? $"Voice changer on: {VoiceLabel}" : "Voice changer off");
                break;
            case "next-voice":
                NextVoice();
                break;
            case "show-app":
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                break;
        }
    }

    // ---------- Lifetime ----------

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        StopRecording();
        if (!ReallyClose) { e.Cancel = true; Hide(); } // keep running in the tray
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop(); _slowTimer.Stop(); _applySoon.Stop(); _saveSoon.Stop();
        _settings.Save();
        _soundboard.Dispose(); _pttTimer.Stop(); _updateTimer.Stop();
        _hotkeys.Dispose();
        Mic.Dispose(); Mixer.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>A small rounded popup near the bottom of the screen confirming what a shortcut did.</summary>
public static class Osd
{
    static Window? _win;
    static TextBlock? _icon, _text;
    static readonly DispatcherTimer Hide = new() { Interval = TimeSpan.FromMilliseconds(1300) };

    public static void Show(Window owner, string glyph, string message)
    {
        if (_win == null)
        {
            _icon = new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 18, Margin = new Thickness(0, 0, 12, 0) };
            _text = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            _win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, Focusable = false,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1B, 0x30)),
                    BorderBrush = (Brush)owner.FindResource("Border"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 14, 22, 14),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _text } },
                },
            };
            Hide.Tick += (_, _) => { Hide.Stop(); _win.Hide(); };
        }
        _icon!.Text = glyph;
        _text!.Text = message;
        _win.Show();
        _win.UpdateLayout();
        var area = SystemParameters.WorkArea;
        _win.Left = area.Left + (area.Width - _win.ActualWidth) / 2;
        _win.Top = area.Bottom - _win.ActualHeight - 60;
        Hide.Stop(); Hide.Start();
    }
}

/// <summary>"Start with Windows": a value under HKCU\...\Run pointing at this exe.</summary>
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "WaveFix";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k == null) return;
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
