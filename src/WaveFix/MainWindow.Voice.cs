using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WaveFix;

/// <summary>Voice polish, the Custom voice tone and the Voice changer page.</summary>
public partial class MainWindow
{
    /// <summary>Sliders rewrite the effect chain at most a few times a second while dragging.</summary>
    readonly DispatcherTimer _applySoon = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly List<(Slider Slider, TextBlock Value, Func<VoiceSettings, double> Get, Action<VoiceSettings, double> Set, Func<double, string> Format)> _voiceSliders = new();
    bool _syncing;

    void ApplySoon()
    {
        if (_loading) return;
        _applySoon.Stop();
        _applySoon.Start();
    }

    void InitVoiceAndPolish()
    {
        _applySoon.Tick += (_, _) => { _applySoon.Stop(); ApplyEffects(); };

        bool fx = VoiceFx.Available;
        FxMissing.Visibility = fx ? Visibility.Collapsed : Visibility.Visible;

        // ---- Voice polish ----
        var p = _settings.Polish;
        LevelerSwitch.IsChecked = p.Leveler; LevelerSlider.Value = p.LevelerAmount;
        DeEsserSwitch.IsChecked = p.DeEsser; DeEsserSlider.Value = p.DeEsserAmount;
        SilencerSwitch.IsChecked = p.Silencer; SilencerSlider.Value = p.SilencerSensitivity;
        LimiterSwitch.IsChecked = p.Limiter; LimiterSlider.Value = p.LimiterCeilingDb;
        foreach (var c in new Control[] { LevelerSwitch, LevelerSlider, DeEsserSwitch, DeEsserSlider, SilencerSwitch, SilencerSlider, LimiterSwitch, LimiterSlider })
            c.IsEnabled = fx;
        UpdatePolishLabels();

        // ---- Custom voice tone ----
        for (int i = 0; i < CustomEq.Bands.Length; i++)
        {
            int band = i;
            var (label, hint, _, _) = CustomEq.Bands[i];
            var value = new TextBlock { Foreground = (Brush)FindResource("SubText"), FontWeight = FontWeights.SemiBold };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            var name = new TextBlock { Style = (Style)FindResource("Label") };
            name.Inlines.Add(label);
            name.Inlines.Add(new System.Windows.Documents.Run("  " + hint) { Foreground = (Brush)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 12 });
            row.Children.Add(name);
            var slider = new Slider { Minimum = -12, Maximum = 12, TickFrequency = 0.5, IsSnapToTickEnabled = true, SmallChange = 0.5, LargeChange = 2, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetName(slider, $"Custom tone {label}");
            if (_settings.CustomEq.Gains.Length != CustomEq.Bands.Length) _settings.CustomEq.Gains = new double[CustomEq.Bands.Length];
            slider.Value = _settings.CustomEq.Gains[band];
            void Show() => value.Text = slider.Value == 0 ? "0 dB" : $"{slider.Value:+0.#;-0.#} dB";
            Show();
            slider.ValueChanged += (_, _) =>
            {
                Show();
                if (_loading || _syncing) return;
                _settings.CustomEq.Gains[band] = slider.Value;
                ApplySoon();
            };
            CustomEqBands.Children.Add(row);
            CustomEqBands.Children.Add(slider);
        }
        foreach (var rb in PresetPanel.Children.OfType<RadioButton>())
            rb.Checked += (_, _) => UpdateCustomEqVisibility();
        UpdateCustomEqVisibility();

        // ---- Voice changer ----
        var v = _settings.Voice;
        var custom = new VoicePreset(VoicePreset.CustomId, "Custom", "Your own mix");
        foreach (var (category, ids) in VoiceCategories)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            VoicePresetPanel.Children.Add(new TextBlock { Text = category, Style = (Style)FindResource("NavGroup"), Margin = new Thickness(2, 0, 0, 8) });
            VoicePresetPanel.Children.Add(wrap);
            foreach (var id in ids)
            {
                var preset = id == VoicePreset.CustomId ? custom : VoicePreset.Find(id);
                if (preset == null) continue;
                var rb = new RadioButton
                {
                    Content = preset.Label, Tag = preset.Desc, GroupName = "voice", Style = (Style)FindResource("ToneChoice"),
                    IsChecked = preset.Id == v.Preset, IsEnabled = fx, DataContext = preset,
                };
                AutomationProperties.SetName(rb, $"Voice {preset.Label}");
                rb.Checked += (_, _) =>
                {
                    if (_loading || _syncing) return;
                    SelectVoice(preset, "voice picked");
                };
                wrap.Children.Add(rb);
            }
        }

        int vRow = 0, vCol = 0;
        void Next() { if (vCol == 0) vCol = 2; else { vCol = 0; vRow++; } }
        void Header(string text) { if (vCol != 0) { vRow++; vCol = 0; } AddVoiceHeader(vRow++, text); }
        void Slide(string label, string hint, double min, double max, double step, Func<VoiceSettings, double> get, Action<VoiceSettings, double> set,
            Func<double, string> format, bool resync = false)
        { AddVoiceSlider(vRow, vCol, label, hint, min, max, step, get, set, format, resync); Next(); }
        void Pick(string label, string hint, string[] options, Func<VoiceSettings, int> get, Action<VoiceSettings, int> set)
        { AddVoiceChoice(vRow, vCol, label, hint, options, get, set); Next(); }
        static string Signed(double x, string neg, string pos) => Math.Abs(x) < 0.5 ? "Normal" : x < 0 ? $"{neg} {-x:0}%" : $"{pos} {x:0}%";

        Header("Voice shape");
        // Gender moves pitch and formant together (and shows where they are).
        Slide("Gender", "Deeper and bigger, or lighter and smaller", -100, 100, 5,
            s => Math.Clamp(s.Formant / 0.04, -100, 100), (s, x) => { s.Pitch = Math.Round(x * 0.06 * 2) / 2; s.Formant = Math.Round(x * 0.04 * 2) / 2; },
            x => Signed(x, "Deeper", "Lighter"), resync: true);
        Slide("Pitch", "Your note, in semitones (keeps your voice)", -12, 12, 0.5, s => s.Pitch, (s, x) => s.Pitch = x,
            x => x == 0 ? "Normal" : $"{x:+0.#;-0.#} st");
        Slide("Formant", "Voice size without changing the note", -12, 12, 0.5, s => s.Formant, (s, x) => s.Formant = x,
            x => x == 0 ? "Normal" : x < 0 ? $"Bigger {-x:0.#}" : $"Smaller {x:0.#}");
        Slide("Harmonics", "Darker or brighter, buzzier tone", -100, 100, 5, s => s.Harmonics, (s, x) => s.Harmonics = x,
            x => Signed(x, "Darker", "Brighter"));
        Slide("Octave layer", "A second voice an octave below or above", -100, 100, 5, s => s.Octave, (s, x) => s.Octave = x,
            x => Signed(x, "Below", "Above"));
        Slide("Detune", "Pulls overtones off-key: metallic, alien", 0, 100, 1, s => s.Detune, (s, x) => s.Detune = x, Percent);
        Slide("Breath", "Airy, whispery noise", 0, 100, 1, s => s.Breath, (s, x) => s.Breath = x, Percent);

        Header("Pitch effects");
        Slide("Auto-tune", "Snaps you to the nearest note (100% = robotic)", 0, 100, 1, s => s.Tune, (s, x) => s.Tune = x, Percent);
        Slide("Vocoder", "Your words played by a synth chord", 0, 100, 1, s => s.Vocoder, (s, x) => s.Vocoder = x, Percent);

        Header("Effects");
        Slide("Ring mod", "Metallic, robotic buzz", 0, 100, 1, s => s.Robot, (s, x) => s.Robot = x, Percent);
        Slide("Ring tone", "Low = growly robot, high = bell-like", 20, 400, 5, s => s.RobotFreq, (s, x) => s.RobotFreq = x, x => $"{x:0} Hz");
        Slide("Distortion", "Crunch and grit", 0, 100, 1, s => s.Grit, (s, x) => s.Grit = x, Percent);
        Pick("Distortion type", "Kind of crunch", new[] { "Overdrive", "Fuzz", "Megaphone" }, s => s.DriveType, (s, i) => s.DriveType = i);
        Slide("Bitcrusher", "Retro, 8-bit sound", 0, 100, 1, s => s.Crush, (s, x) => s.Crush = x, Percent);
        Slide("Wobble", "Wavy, unsteady pitch", 0, 100, 1, s => s.Wobble, (s, x) => s.Wobble = x, Percent);
        Pick("Modulation", "Chorus, flanger or phaser", new[] { "Off", "Chorus", "Flanger", "Phaser" }, s => s.ModType, (s, i) => s.ModType = i);
        Slide("Modulation amount", "How strong the swirl is", 0, 100, 1, s => s.ModMix, (s, x) => s.ModMix = x, Percent);
        Slide("Modulation speed", "How fast it sweeps", 0.05, 8, 0.05, s => s.ModRate, (s, x) => s.ModRate = x, x => $"{x:0.##} Hz");
        Pick("Radio", "Phone and radio sounds", new[] { "Off", "Old phone", "Walkie-talkie", "Police radio", "AM radio" }, s => s.Radio, (s, i) => s.Radio = i);
        Slide("Echo", "Repeats after you talk", 0, 100, 1, s => s.Echo, (s, x) => s.Echo = x, Percent);
        Pick("Echo style", "How it repeats", new[] { "Echo", "Slapback", "Canyon", "Rhythm" }, s => s.EchoStyle, (s, i) => s.EchoStyle = i);
        Slide("Echo time", "Gap between repeats", 20, 1000, 10, s => s.EchoTime, (s, x) => s.EchoTime = x, x => $"{x:0} ms");
        Slide("Room", "Reverb, like a big hall", 0, 100, 1, s => s.Room, (s, x) => s.Room = x, Percent);

        VoiceSwitch.IsChecked = v.Enabled;
        VoiceSwitch.IsEnabled = fx;
        UpdateVoiceText();
    }

    static string Percent(double x) => x < 0.5 ? "Off" : $"{x:0}%";

    /// <summary>Preset groups on the page (Custom last).</summary>
    static readonly (string Category, string[] Ids)[] VoiceCategories =
    {
        ("Size and age", new[] { "deep", "giant", "swap-down", "swap-up", "kid", "chipmunk" }),
        ("Characters", new[] { "monster", "whisper", "ghost", "alien", "bell", "twins" }),
        ("Robots and music", new[] { "robot", "android", "autotune", "synth", "retro" }),
        ("Radio and phone", new[] { "phone", "walkie", "police", "amradio", "megaphone" }),
        ("Spaces and effects", new[] { "cave", "canyon", "underwater", "jet", "dream", "space", VoicePreset.CustomId }),
    };

    readonly List<(ComboBox Box, Func<VoiceSettings, int> Get)> _voiceChoices = new();

    void AddVoiceHeader(int row, string text)
    {
        while (VoiceSliders.RowDefinitions.Count <= row) VoiceSliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var t = new TextBlock { Text = text, Style = (Style)FindResource("CardTitle"), FontSize = 14.5, Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 12) };
        Grid.SetRow(t, row);
        Grid.SetColumnSpan(t, 3);
        VoiceSliders.Children.Add(t);
    }

    void AddVoiceChoice(int row, int col, string label, string hint, string[] options, Func<VoiceSettings, int> get, Action<VoiceSettings, int> set)
    {
        while (VoiceSliders.RowDefinitions.Count <= row) VoiceSliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        panel.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 6) });
        var box = new ComboBox { ItemsSource = options, SelectedIndex = Math.Clamp(get(_settings.Voice), 0, options.Length - 1), IsEnabled = VoiceFx.Available };
        AutomationProperties.SetName(box, $"Voice {label}");
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || _syncing || box.SelectedIndex < 0) return;
            set(_settings.Voice, box.SelectedIndex);
            if (_settings.Voice.Preset != VoicePreset.CustomId)
            {
                _settings.Voice.Preset = VoicePreset.CustomId;
                CheckVoiceButton(VoicePreset.CustomId);
            }
            UpdateVoiceText();
            ApplySoon();
        };
        panel.Children.Add(box);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, col);
        VoiceSliders.Children.Add(panel);
        _voiceChoices.Add((box, get));
    }

    void AddVoiceSlider(int row, int col, string label, string hint, double min, double max, double step,
        Func<VoiceSettings, double> get, Action<VoiceSettings, double> set, Func<double, string> format, bool resync = false)
    {
        while (VoiceSliders.RowDefinitions.Count <= row) VoiceSliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var value = new TextBlock { Foreground = (Brush)FindResource("SubText"), FontWeight = FontWeights.SemiBold };
        var head = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        head.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 4) });
        var slider = new Slider
        {
            Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true,
            SmallChange = step, LargeChange = step * 4, Value = get(_settings.Voice), IsEnabled = VoiceFx.Available,
        };
        AutomationProperties.SetName(slider, $"Voice {label}");
        value.Text = format(slider.Value);
        slider.ValueChanged += (_, _) =>
        {
            value.Text = format(slider.Value);
            if (_loading || _syncing) return;
            set(_settings.Voice, slider.Value);
            if (resync) SyncVoiceSliders(); // e.g. Gender moved Pitch and Formant
            if (_settings.Voice.Preset != VoicePreset.CustomId)
            {
                _settings.Voice.Preset = VoicePreset.CustomId;
                CheckVoiceButton(VoicePreset.CustomId);
            }
            UpdateVoiceText();
            ApplySoon();
        };
        panel.Children.Add(slider);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, col);
        VoiceSliders.Children.Add(panel);
        _voiceSliders.Add((slider, value, get, set, format));
    }

    /// <summary>Picks a voice and turns the voice changer on (picking a voice means you want to hear it).</summary>
    void SelectVoice(VoicePreset preset, string why)
    {
        var v = _settings.Voice;
        if (preset.Id != VoicePreset.CustomId) v.CopyFrom(preset);
        else v.Preset = VoicePreset.CustomId;
        CheckVoiceButton(v.Preset);
        SyncVoiceSliders();
        if (!v.Enabled) ChangeLog.Write($"Voice changer on ({why})");
        v.Enabled = true;
        VoiceSwitch.IsChecked = true;
        UpdateVoiceText();
        ApplyEffects();
    }

    void CheckVoiceButton(string id)
    {
        _syncing = true;
        try
        {
            foreach (var rb in VoicePresetPanel.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<RadioButton>()))
                rb.IsChecked = ((VoicePreset)rb.DataContext).Id == id;
        }
        finally { _syncing = false; }
    }

    void SyncVoiceSliders()
    {
        _syncing = true;
        try
        {
            foreach (var s in _voiceSliders) s.Slider.Value = s.Get(_settings.Voice);
            foreach (var c in _voiceChoices) c.Box.SelectedIndex = Math.Max(0, c.Get(_settings.Voice));
        }
        finally { _syncing = false; }
    }

    string VoiceLabel => VoicePreset.Find(_settings.Voice.Preset)?.Label ?? "your Custom voice";

    void UpdateVoiceText()
    {
        var v = _settings.Voice;
        VoiceStateText.Text = !v.Enabled ? "Off: everyone hears your real voice."
            : v.IsNeutral ? "On, but every slider is at normal, so you sound like yourself."
            : $"On: you sound like {VoiceLabel}.";
        VoiceResetButton.Visibility = v.Preset == VoicePreset.CustomId ? Visibility.Collapsed : Visibility.Visible;
    }

    void VoiceSwitch_Click(object sender, RoutedEventArgs e) => SetVoiceChanger(VoiceSwitch.IsChecked == true, "switch clicked");

    void SetVoiceChanger(bool on, string why)
    {
        VoiceSwitch.IsChecked = on;
        if (_settings.Voice.Enabled == on) return;
        _settings.Voice.Enabled = on;
        ChangeLog.Write($"Voice changer {(on ? "on" : "off")} ({why})");
        UpdateVoiceText();
        ApplyEffects();
    }

    void VoiceReset_Click(object sender, RoutedEventArgs e)
    {
        if (VoicePreset.Find(_settings.Voice.Preset) is { } p) SelectVoice(p, "reset to preset");
    }

    /// <summary>Shortcut: the next preset voice (turns the voice changer on).</summary>
    void NextVoice()
    {
        var all = VoicePreset.All;
        int i = Array.FindIndex(all, p => p.Id == _settings.Voice.Preset);
        var next = all[(i + 1) % all.Length];
        SelectVoice(next, "shortcut");
        Osd.Show(this, "", $"Voice: {next.Label}");
    }

    // ---- Voice polish ----

    void Polish_Click(object sender, RoutedEventArgs e)
    {
        var p = _settings.Polish;
        void Set(bool now, bool before, string name) { if (now != before) ChangeLog.Write($"{name} {(now ? "on" : "off")} (switch clicked)"); }
        Set(LevelerSwitch.IsChecked == true, p.Leveler, "Leveler");
        Set(DeEsserSwitch.IsChecked == true, p.DeEsser, "De-esser");
        Set(SilencerSwitch.IsChecked == true, p.Silencer, "Background silencer");
        Set(LimiterSwitch.IsChecked == true, p.Limiter, "Peak protection");
        p.Leveler = LevelerSwitch.IsChecked == true;
        p.DeEsser = DeEsserSwitch.IsChecked == true;
        p.Silencer = SilencerSwitch.IsChecked == true;
        p.Limiter = LimiterSwitch.IsChecked == true;
        ApplyEffects();
    }

    void Polish_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LimiterValue == null) return; // fires during InitializeComponent
        UpdatePolishLabels();
        if (_loading) return;
        var p = _settings.Polish;
        p.LevelerAmount = LevelerSlider.Value;
        p.DeEsserAmount = DeEsserSlider.Value;
        p.SilencerSensitivity = SilencerSlider.Value;
        p.LimiterCeilingDb = LimiterSlider.Value;
        // Only rewrite the chain if the effect is on; the value is saved either way.
        if (sender == LevelerSlider && !p.Leveler || sender == DeEsserSlider && !p.DeEsser ||
            sender == SilencerSlider && !p.Silencer || sender == LimiterSlider && !p.Limiter) { _settings.Save(); return; }
        ApplySoon();
    }

    void UpdatePolishLabels()
    {
        static string Strength(double x) => x < 34 ? "Gentle" : x < 67 ? "Medium" : "Strong";
        LevelerValue.Text = Strength(LevelerSlider.Value);
        DeEsserValue.Text = Strength(DeEsserSlider.Value);
        SilencerValue.Text = SilencerSlider.Value < 34 ? "Low" : SilencerSlider.Value < 67 ? "Medium" : "High";
        LimiterValue.Text = $"{LimiterSlider.Value:0.0} dB";
    }

    // ---- Custom voice tone ----

    void UpdateCustomEqVisibility() =>
        CustomEqPanel.Visibility = _settings.EqPreset == EqPresets.Custom ? Visibility.Visible : Visibility.Collapsed;

    void CustomEqReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.CustomEq.Gains = new double[CustomEq.Bands.Length];
        _syncing = true;
        try { foreach (var s in CustomEqBands.Children.OfType<Slider>()) s.Value = 0; }
        finally { _syncing = false; }
        ApplyEffects();
    }
}
