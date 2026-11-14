using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    bool _loading = true;

    public bool ReallyClose { get; set; }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = this;

        foreach (var p in EqPresets.All)
        {
            var rb = new RadioButton { Content = p, GroupName = "eq", Style = (Style)FindResource("Segment"), IsChecked = p == _settings.EqPreset };
            rb.Checked += (_, _) => { _settings.EqPreset = p; ApplyEffects(); };
            PresetPanel.Children.Add(rb);
        }
        NoiseSwitch.IsChecked = _settings.NoiseRemoval;
        LowCutSwitch.IsChecked = _settings.LowCut;
        BoostSlider.Value = _settings.VoiceBoostDb;
        UpdateBoostLabel();
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;

        Mic.DeviceChanged += () =>
        {
            if (Mic.DeviceId != null && !_loading) { _settings.MicDeviceId = Mic.DeviceId; _settings.Save(); }
            ApplyEffects();
        };
        Mic.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MicController.Muted)) MuteLabel.Text = Mic.Muted ? "Mic muted" : "Mute mic"; };
        Mic.RefreshDevices(_settings.MicDeviceId);
        MuteLabel.Text = Mic.Muted ? "Mic muted" : "Mute mic";

        RoutingNote.Text = Mixer.RoutingAvailable ? "" : "Output switching unavailable on this Windows version";
        RefreshMixer();

        _meterTimer.Tick += (_, _) =>
        {
            Mic.PollMeter();
            MeterFill.Width = Math.Max(0, MeterHost.ActualWidth * Mic.Level);
        };
        _slowTimer.Tick += (_, _) => { RefreshMixer(); UpdateStatus(); };
        _meterTimer.Start();
        _slowTimer.Start();

        _loading = false;
        if (Mic.DeviceId != null) _settings.MicDeviceId = Mic.DeviceId;
        ApplyEffects();
    }

    void RefreshMixer()
    {
        try { Mixer.Refresh(); } catch { }
        EmptyMixer.Visibility = Mixer.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Effect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.NoiseRemoval = NoiseSwitch.IsChecked == true;
        _settings.LowCut = LowCutSwitch.IsChecked == true;
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
        BoostValue.Text = v switch { 0 => "off", > 0 => $"+{v:0.#} dB", _ => $"{v:0.#} dB" };
        // Boost is applied in the audio engine after the input level; big boosts can clip.
        BoostValue.Foreground = (System.Windows.Media.Brush)FindResource(v > 12 ? "Warn" : "Muted");
    }

    void ApplyEffects()
    {
        if (_loading) return;
        _settings.Save();
        if (Mic.DeviceId == null) return;
        try { _engine.Apply(_settings, Mic.DeviceId); }
        catch (Exception ex) { SetStatus($"Couldn't write Equalizer APO config: {ex.Message}", bad: true); return; }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (!_engine.IsInstalled)
        { SetStatus("Equalizer APO isn't installed. Effects need it.", bad: true); return; }
        if (Mic.DeviceId == null)
        { SetStatus("No microphone found.", bad: true); return; }
        if (Process.GetProcessesByName("Elgato.WaveLink").Length > 0)
        { SetStatus("Wave Link is running. Close it: it leaks your mic into Discord streams.", warn: true); return; }

        switch (EffectsEngine.GetSlot(Mic.DeviceId))
        {
            case EffectsEngine.SlotState.None:
                SetStatus("Effects aren't attached to this mic. Attach it in Equalizer APO's Device Selector.", bad: true); break;
            case EffectsEngine.SlotState.StreamOnly:
                SetStatus("Effects are attached, but Discord's default mode skips them. Run tools\\run-as-system.ps1 to move them to the endpoint slot.", warn: true); break;
            default:
                SetStatus(_settings.NoiseRemoval && !_engine.HasNoisePlugin
                    ? "Active. Noise removal plugin is missing from Equalizer APO's VSTPlugins\\rnnoise folder."
                    : "Active for every app on this mic, Discord included.", warn: _settings.NoiseRemoval && !_engine.HasNoisePlugin);
                break;
        }
    }

    void SetStatus(string text, bool warn = false, bool bad = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(bad ? "Bad" : warn ? "Warn" : "Accent");
        StatusBox.Background = new SolidColorBrush(bad ? Color.FromRgb(0x3A, 0x22, 0x27) : warn ? Color.FromRgb(0x3A, 0x30, 0x1E) : Color.FromRgb(0x1E, 0x33, 0x2A));
    }

    void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupRegistration.Set(StartupSwitch.IsChecked == true);
        _settings.StartWithWindows = StartupSwitch.IsChecked == true;
        _settings.Save();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ReallyClose) { e.Cancel = true; Hide(); } // keep running in the tray
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop(); _slowTimer.Stop();
        Mic.Dispose(); Mixer.Dispose();
        base.OnClosed(e);
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
