using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WaveFix;

/// <summary>Auto mic setup: a short guided recording, then recommended settings to apply.</summary>
public partial class MainWindow
{
    void MicSetup_Click(object sender, RoutedEventArgs e)
    {
        if (Mic.DeviceId is not { } mic) { Osd.Show(this, "\uE720", "No microphone found"); return; }
        var win = new Window
        {
            Title = "Set up my mic", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 520, ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("Bg"), FontFamily = (FontFamily)FindResource("AppFont"), Foreground = (Brush)FindResource("Text"),
        };
        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        var title = new TextBlock { Text = "Set up my mic", FontSize = 20, FontWeight = FontWeights.Bold };
        var say = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14), FontSize = 13.5,
            Text = $"WaveFix listens for {MicSetup.QuietSeconds + MicSetup.TalkSeconds:0} seconds: first stay quiet (so it can hear your room), then talk normally. Your effects are paused while it listens. Nothing changes until you click Apply." };
        var sentence = new Border { CornerRadius = new CornerRadius(12), Background = (Brush)FindResource("Surface"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 14), Visibility = Visibility.Collapsed,
            Child = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = FontWeights.SemiBold,
                Text = "\"Testing my microphone, one, two, three. I'm talking the way I usually do in a game or a call, not louder and not quieter.\"" } };
        var meter = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 6), Foreground = (Brush)FindResource("Good"), Background = (Brush)FindResource("Surface"), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 14), Foreground = (Brush)FindResource("PrimaryLine"), Background = (Brush)FindResource("Surface"), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
        var results = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Style = (Style)FindResource("SoftButton"), Height = 40, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, 0, 10, 0),
            Content = new TextBlock { Text = "Cancel", FontWeight = FontWeights.SemiBold } };
        var main = new Button { Style = (Style)FindResource("PrimaryButton"), Height = 40, Padding = new Thickness(22, 0, 22, 0),
            Content = new TextBlock { Text = "Start", FontWeight = FontWeights.Bold, Foreground = Brushes.White } };
        buttons.Children.Add(cancel); buttons.Children.Add(main);
        foreach (var c in new UIElement[] { title, say, sentence, meter, progress, results, buttons }) root.Children.Add(c);
        win.Content = root;

        var cts = new CancellationTokenSource();
        MicSetupResult? result = null;
        bool recording = false;
        cancel.Click += (_, _) => win.Close();
        win.Closed += (_, _) => cts.Cancel();

        main.Click += async (_, _) =>
        {
            if (result is { Problem: null })
            {
                ApplyMicSetup(result);
                win.Close();
                Osd.Show(this, "\uE9F5", "Mic set up");
                return;
            }
            if (recording) return;
            recording = true;
            main.IsEnabled = false;
            results.Children.Clear();
            meter.Visibility = progress.Visibility = Visibility.Visible;
            ChangeLog.Write("Auto mic setup: listening");
            WaveFixEngine.Bypass = true;
            ApplyEffects();
            try
            {
                await Task.Delay(500, cts.Token); // the engine picks up the empty chain
                var (quiet, talk, rate) = await MicSetup.RecordAsync(mic,
                    (phase, frac) => Dispatcher.BeginInvoke(() =>
                    {
                        progress.Value = phase == "quiet" ? frac * MicSetup.QuietSeconds / (MicSetup.QuietSeconds + MicSetup.TalkSeconds)
                                                          : (MicSetup.QuietSeconds + frac * MicSetup.TalkSeconds) / (MicSetup.QuietSeconds + MicSetup.TalkSeconds);
                        if (phase == "quiet") { say.Text = $"Stay quiet for a moment… ({Math.Ceiling(MicSetup.QuietSeconds * (1 - frac)):0})"; sentence.Visibility = Visibility.Collapsed; }
                        else { say.Text = $"Now read this out loud, at your normal volume ({Math.Ceiling(MicSetup.TalkSeconds * (1 - frac)):0}):"; sentence.Visibility = Visibility.Visible; }
                    }),
                    peak => Dispatcher.BeginInvoke(() => meter.Value = Math.Clamp((20 * Math.Log10(peak + 1e-9) + 60) / 60, 0, 1)),
                    cts.Token);
                var lv = Mic.LevelDb ?? (0, -10, 30);
                result = await Task.Run(() => MicSetup.Analyze(quiet, talk, rate, lv.Db, lv.Min, lv.Max, _settings.VoiceBoostDb, !WaveFixEngine.NeedsUpdate));
                ChangeLog.Write($"Auto mic setup: noise {result.NoiseDb:0.0} dB, speech {result.SpeechDb:0.0} dB, peak {result.PeakDb:0.0} dB, spread {result.SpreadDb:0} dB, tone centre {result.CentroidHz:0} Hz{(result.Problem != null ? ", problem: " + result.Problem : "")}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { result = new MicSetupResult { Problem = $"Couldn't record the mic: {ex.Message}" }; }
            finally
            {
                WaveFixEngine.Bypass = false;
                ApplyEffects();
                recording = false;
            }
            if (!win.IsLoaded) return;
            meter.Visibility = progress.Visibility = Visibility.Collapsed;
            sentence.Visibility = Visibility.Collapsed;
            main.IsEnabled = true;
            if (result.Problem != null)
            {
                say.Text = result.Problem;
                ((TextBlock)main.Content).Text = "Try again";
                return;
            }
            say.Text = "Here's what WaveFix recommends for your mic and room:";
            foreach (var (what, why) in result.Changes)
            {
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                item.Children.Add(new TextBlock { Text = what, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                item.Children.Add(new TextBlock { Text = why, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                results.Children.Add(item);
            }
            if (_settings.EqPreset == EqPresets.Custom)
                results.Children.Add(new TextBlock { Text = "Your Custom voice tone is kept.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8) });
            ((TextBlock)main.Content).Text = "Apply";
        };
        win.ShowDialog();
    }

    void ApplyMicSetup(MicSetupResult r)
    {
        _loading = true; // apply everything, then rebuild the chain once
        try
        {
            if (Math.Abs(r.LevelChangeDb) >= 0.5 && Mic.LevelDb is { } lv) Mic.SetLevelDb(lv.Db + r.LevelChangeDb);
            BoostSlider.Value = r.BoostDb;
            _settings.VoiceBoostDb = r.BoostDb;
            _settings.NoiseRemoval = r.NoiseRemoval;
            NoiseSwitch.IsChecked = r.NoiseRemoval;
            if (r.NoiseRemoval)
            {
                _settings.NoiseModel = r.Strong ? "strong" : "standard";
                (r.Strong ? NoiseStrong : NoiseStandard).IsChecked = true;
                _settings.NoiseStrengthDb = r.NoiseStrengthDb;
                NoiseStrengthSlider.Value = r.NoiseStrengthDb;
            }
            _settings.LowCut = r.LowCut;
            LowCutSwitch.IsChecked = r.LowCut;
            if (r.Leveler) { _settings.Polish.Leveler = true; LevelerSwitch.IsChecked = true; }
            if (r.Limiter) { _settings.Polish.Limiter = true; LimiterSwitch.IsChecked = true; }
            if (_settings.EqPreset != EqPresets.Custom)
            {
                _settings.EqPreset = r.Tone;
                var label = Tones.First(t => t.Id == r.Tone).Label;
                foreach (var rb in PresetPanel.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Content == label;
            }
        }
        finally { _loading = false; }
        UpdateNoiseModel();
        UpdatePolishLabels();
        ChangeLog.Write("Auto mic setup applied: " + string.Join("; ", r.Changes.Select(c => c.What)));
        ApplyEffects();
    }
}
