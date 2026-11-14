using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WaveFix.Rvc;

namespace WaveFix;

/// <summary>The AI voices page (RVC voice models).</summary>
public partial class MainWindow
{
    readonly RvcEngine _ai = new();
    CancellationTokenSource? _aiDownload;
    bool _aiSyncing;

    AiVoiceSettings Ai => _settings.AiVoice;

    void InitAiVoices()
    {
        AiGpuSwitch.IsChecked = Ai.UseGraphicsCard;
        AiSilentSwitch.IsChecked = Ai.SilentWhenUnavailable;
        AiSwitch.IsChecked = Ai.Enabled;
        _ai.PreferGpu = Ai.UseGraphicsCard;
        _ai.GateDb = (float)Ai.GateDb;
        _ai.AutoGate = Ai.AutoGate;
        _aiSyncing = true;
        AiAutoGateSwitch.IsChecked = Ai.AutoGate;
        AiGateSlider.Value = Math.Clamp(Ai.GateDb, -70, -20);
        _aiSyncing = false;
        UpdateAiGateText();
        InitHearMyself();
        _ai.StatusChanged += () => Dispatcher.BeginInvoke(UpdateAiState);
        _ai.Failed += () => Dispatcher.BeginInvoke(() => { ApplyEffects(); UpdateAiState(); });
        // Voices whose converted model is gone (e.g. AppData cleaned) can't run; drop them.
        Ai.Voices.RemoveAll(v => !File.Exists(v.File));
        RefreshAiVoices();
        UpdateAiDownload();
        if (Ai.Enabled) StartAi(apply: false); // the window applies the effects once loading finishes
        UpdateAiState();
    }

    bool AiCanRun => RvcHelpers.Ready && Ai.Current != null;

    void StartAi(bool apply = true)
    {
        if (!AiCanRun) { UpdateAiState(); return; }
        _ai.Start(Ai.Current!);
        if (apply) ApplyEffects();
        UpdateAiState();
    }

    void StopAi()
    {
        _ai.Stop();
        ApplyEffects();
        UpdateAiState();
    }

    void UpdateAiState()
    {
        string text;
        if (!Ai.Enabled) text = "Off";
        else if (!RvcHelpers.Ready) text = "Finish the one-time download below to use AI voices.";
        else if (Ai.Current == null) text = "Add a voice model below.";
        else
        {
            var status = _ai.Status;
            text = $"{Ai.Current.Name}: {(string.IsNullOrEmpty(status) ? "starting…" : status)}";
        }
        AiStateText.Text = text;
    }

    void AiSwitch_Click(object sender, RoutedEventArgs e)
    {
        Ai.Enabled = AiSwitch.IsChecked == true;
        ChangeLog.Write($"AI voice {(Ai.Enabled ? "on" : "off")} (switch clicked)");
        _settings.Save();
        if (Ai.Enabled) StartAi(); else StopAi();
    }

    // ---------------- Voices ----------------

    void RefreshAiVoices()
    {
        AiVoiceList.Children.Clear();
        var current = Ai.Current;
        foreach (var v in Ai.Voices)
        {
            var voice = v;
            var grid = new Grid { Margin = new Thickness(0, 0, 8, 8) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(VoicePicture(voice, 44));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = voice.Name, FontWeight = FontWeights.Bold, FontSize = 14, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
            string extra = (voice.IndexFile != null ? " · index" : "") + (voice.Speakers > 1 ? $" · {voice.Speakers} speakers" : "");
            text.Children.Add(new TextBlock { Text = $"RVC {voice.Version} · {voice.SampleRate / 1000} kHz{extra}", FontSize = 12, Foreground = (Brush)FindResource("SubText"), Margin = new Thickness(0, 2, 0, 0) });
            content.Children.Add(text);
            var choice = new RadioButton
            {
                Style = (Style)FindResource("VoiceChoice"), GroupName = "aivoice", Content = content,
                IsChecked = voice == current, MinWidth = 220,
            };
            AutomationProperties.SetName(choice, $"AI voice {voice.Name}");
            choice.Checked += (_, _) => SelectAiVoice(voice);
            var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 10, 0) };
            buttons.Children.Add(TileButton("\uE711", "Remove", $"Remove {voice.Name}", () => RemoveAiVoice(voice)));
            buttons.Children.Add(TileButton("\uEB9F", voice.Image == null ? "Add a picture" : "Change or remove the picture", $"Picture for {voice.Name}", () => PickAiVoicePicture(voice)));
            grid.Children.Add(choice);
            grid.Children.Add(buttons);
            AiVoiceList.Children.Add(grid);
        }
        AiEmpty.Visibility = Ai.Voices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _aiSyncing = true;
        AiPitchSlider.Value = current?.Pitch ?? 0;
        _aiSyncing = false;
        AiPitchSlider.IsEnabled = current != null;
        UpdateAiPitchLabel();
        RefreshAiAdvanced();
    }

    Button TileButton(string glyph, string tip, string name, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("LinkButton"), ToolTip = tip, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 11, Foreground = (Brush)FindResource("SubText") },
        };
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The voice's picture, or its initial on a coloured square.</summary>
    FrameworkElement VoicePicture(AiVoice v, double size)
    {
        var border = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("PrimarySoft"), ClipToBounds = true };
        if (v.Image != null && File.Exists(v.Image))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file open
                bmp.DecodePixelWidth = (int)(size * 2);
                bmp.UriSource = new Uri(v.Image);
                bmp.EndInit();
                bmp.Freeze();
                border.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                return border;
            }
            catch { /* unreadable picture: fall back to the initial */ }
        }
        border.Child = new TextBlock
        {
            Text = v.Name.Length > 0 ? v.Name[..1].ToUpperInvariant() : "?", FontWeight = FontWeights.Bold, FontSize = size * 0.42,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xFE)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }

    void PickAiVoicePicture(AiVoice v)
    {
        if (v.Image != null)
        {
            var r = MessageBox.Show(this, $"Change {v.Name}'s picture?\n\nYes: pick a new one.   No: remove the picture.", "WaveFix", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.No) { RvcModelImport.RemoveImage(v); _settings.Save(); RefreshAiVoices(); return; }
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Pick a picture for {v.Name}",
            Filter = "Pictures (*.png, *.jpg, *.bmp, *.gif, *.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { RvcModelImport.SetImage(v, dlg.FileName); _settings.Save(); }
        catch (Exception ex) { ShowAiNotice($"Couldn't use that picture: {ex.Message}"); }
        RefreshAiVoices();
    }

    // ---------------- Advanced ----------------

    void AiAdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = AiAdvancedPanel.Visibility != Visibility.Visible;
        AiAdvancedPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AiAdvancedChevron.Text = open ? "\uE70E" : "\uE70D";
    }

    /// <summary>Shows the current voice's advanced settings.</summary>
    void RefreshAiAdvanced()
    {
        var v = Ai.Current;
        _aiSyncing = true;
        AiLoudnessSlider.Value = Math.Round((v?.LoudnessMix ?? 0) * 100);
        AiIndexRateSlider.Value = Math.Round((v?.IndexRate ?? 0.5) * 100);
        AiIndexName.Text = v == null ? "Pick a voice first." : v.IndexFile == null ? "None (optional)" : $"Added for {v.Name}";
        AiIndexAddButton.IsEnabled = v != null;
        AiIndexRemoveButton.Visibility = v?.IndexFile != null ? Visibility.Visible : Visibility.Collapsed;
        AiIndexRatePanel.Visibility = v?.IndexFile != null ? Visibility.Visible : Visibility.Collapsed;
        bool multi = v != null && v.Speakers > 1;
        AiSpeakerPanel.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (multi) { AiSpeakerSlider.Maximum = v!.Speakers - 1; AiSpeakerSlider.Value = Math.Clamp(v.SpeakerId, 0, v.Speakers - 1); }
        AiLoudnessSlider.IsEnabled = v != null;
        _aiSyncing = false;
        UpdateAiAdvancedLabels();
    }

    void UpdateAiAdvancedLabels()
    {
        double l = AiLoudnessSlider.Value;
        AiLoudnessValue.Text = l == 0 ? "Follows you" : l == 100 ? "Model's own" : $"{l:0}% model";
        AiIndexRateValue.Text = $"{AiIndexRateSlider.Value:0}%";
        AiSpeakerValue.Text = $"{AiSpeakerSlider.Value + 1:0} of {AiSpeakerSlider.Maximum + 1:0}";
    }

    void AiVoiceAdvanced_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AiLoudnessValue == null || AiIndexRateValue == null || AiSpeakerValue == null) return; // still loading
        UpdateAiAdvancedLabels();
        if (_aiSyncing || _loading || Ai.Current is not { } v) return;
        v.LoudnessMix = AiLoudnessSlider.Value / 100;
        v.IndexRate = AiIndexRateSlider.Value / 100;
        if (v.Speakers > 1) v.SpeakerId = (int)AiSpeakerSlider.Value;
        _settings.Save();
        if (RvcEngine.Active) _ai.SetVoice(v);
    }

    void AiAdvancedOption_Click(object sender, RoutedEventArgs e)
    {
        Ai.AutoGate = AiAutoGateSwitch.IsChecked == true;
        _ai.AutoGate = Ai.AutoGate;
        ChangeLog.Write($"AI voice: ignore background noise automatically {(Ai.AutoGate ? "on" : "off")}");
        _settings.Save();
        UpdateAiGateText();
    }

    void AiGate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AiGateValue == null || AiGateHint == null) return;
        UpdateAiGateText();
        if (_aiSyncing || _loading) return;
        Ai.GateDb = AiGateSlider.Value;
        _ai.GateDb = (float)Ai.GateDb;
        _settings.Save();
    }

    void UpdateAiGateText()
    {
        if (AiGateValue == null || AiGateHint == null) return;
        AiGateValue.Text = $"{AiGateSlider.Value:0} dB";
        AiGateHint.Text = AiAutoGateSwitch.IsChecked == true
            ? "Sounds quieter than this always count as silence; automatic raises it above your background noise when needed."
            : "Sounds quieter than this count as silence. Raise it if the AI babbles in pauses; lower it if quiet words get cut.";
    }

    async void AiIndexAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Ai.Current is not { } v) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Pick the .index file for {v.Name}", Filter = "RVC index (*.index)|*.index|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        AiIndexName.Text = "Checking the index…";
        try
        {
            await Task.Run(() => RvcModelImport.AddIndex(v, dlg.FileName));
            ChangeLog.Write($"AI voice: index added to {v.Name} ({dlg.FileName})");
            _settings.Save();
            if (RvcEngine.Active) _ai.SetVoice(v);
        }
        catch (Exception ex) { ShowAiNotice($"Couldn't use that index: {ex.Message}"); }
        RefreshAiVoices();
    }

    void AiIndexRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Ai.Current is not { } v) return;
        RvcModelImport.RemoveIndex(v);
        _settings.Save();
        if (RvcEngine.Active) _ai.SetVoice(v);
        RefreshAiVoices();
    }

    void AiAdvancedReset_Click(object sender, RoutedEventArgs e)
    {
        Ai.AutoGate = true; Ai.GateDb = -50;
        _ai.AutoGate = true; _ai.GateDb = -50;
        if (Ai.Current is { } v) { v.LoudnessMix = 0; v.IndexRate = 0.5; v.SpeakerId = 0; if (RvcEngine.Active) _ai.SetVoice(v); }
        _settings.Save();
        _aiSyncing = true;
        AiAutoGateSwitch.IsChecked = true;
        AiGateSlider.Value = -50;
        _aiSyncing = false;
        UpdateAiGateText();
        RefreshAiAdvanced();
    }

    // ---------------- Hear myself ----------------

    readonly MicMonitor _monitor = new();

    void InitHearMyself()
    {
        HearMyselfMicSwitch.IsChecked = HearMyselfAiSwitch.IsChecked = _settings.HearMyself;
        HearMyselfSlider.Value = Math.Clamp(_settings.HearMyselfVolume, 0, 100);
        HearMyselfPanel.Visibility = _settings.HearMyself ? Visibility.Visible : Visibility.Collapsed;
        _monitor.Volume = (float)Math.Pow(HearMyselfSlider.Value / 100, 2) * 1.5f;
        Mic.DeviceChanged += () => { if (_settings.HearMyself) StartMonitor(); };
        if (_settings.HearMyself) StartMonitor();
    }

    void HearMyselfSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as CheckBox)?.IsChecked == true;
        _settings.HearMyself = on;
        HearMyselfMicSwitch.IsChecked = HearMyselfAiSwitch.IsChecked = on;
        HearMyselfPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ChangeLog.Write($"Hear myself {(on ? "on" : "off")} (switch clicked)");
        _settings.Save();
        if (on) StartMonitor(); else { _monitor.Stop(); HearMyselfError.Visibility = Visibility.Collapsed; }
    }

    void StartMonitor()
    {
        if (Mic.DeviceId is not { } id) return;
        _monitor.Start(id);
        HearMyselfError.Text = _monitor.Error == null ? "" : $"Couldn't play your mic: {_monitor.Error}";
        HearMyselfError.Visibility = _monitor.Error == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void HearMyselfVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HearMyselfValue == null) return;
        HearMyselfValue.Text = $"{HearMyselfSlider.Value:0}%";
        // Curved like the soundboard's volume: fine control at low levels.
        _monitor.Volume = (float)Math.Pow(HearMyselfSlider.Value / 100, 2) * 1.5f;
        if (_loading) return;
        _settings.HearMyselfVolume = HearMyselfSlider.Value;
        _saveSoon.Stop(); _saveSoon.Start();
    }

    void SelectAiVoice(AiVoice v)
    {
        if (Ai.Selected == v.Id && Ai.Current == v) return;
        Ai.Selected = v.Id;
        ChangeLog.Write($"AI voice: {v.Name} selected");
        _settings.Save();
        _aiSyncing = true;
        AiPitchSlider.Value = v.Pitch;
        _aiSyncing = false;
        UpdateAiPitchLabel();
        if (RvcEngine.Active) _ai.SetVoice(v);
        RefreshAiAdvanced();
        UpdateAiState();
    }

    void RemoveAiVoice(AiVoice v)
    {
        if (MessageBox.Show(this, $"Remove the AI voice \"{v.Name}\"? You can add it again from its .pth file.", "WaveFix",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        bool wasCurrent = Ai.Current == v;
        Ai.Voices.Remove(v);
        if (wasCurrent) Ai.Selected = Ai.Voices.FirstOrDefault()?.Id;
        _settings.Save();
        if (wasCurrent && RvcEngine.Active)
        {
            if (Ai.Current != null) _ai.SetVoice(Ai.Current); else StopAi();
        }
        RvcModelImport.Delete(v);
        RefreshAiVoices();
        UpdateAiState();
    }

    void UpdateAiPitchLabel()
    {
        double p = AiPitchSlider.Value;
        AiPitchValue.Text = p == 0 ? "No change" : $"{p:+0;-0} semitones";
    }

    void AiPitch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AiPitchValue == null) return;
        UpdateAiPitchLabel();
        if (_aiSyncing || _loading || Ai.Current is not { } v) return;
        v.Pitch = AiPitchSlider.Value;
        _settings.Save();
        if (RvcEngine.Active) _ai.SetVoice(v);
    }

    async void AiAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add RVC voice models",
            Filter = "RVC voice models (*.pth, *.zip)|*.pth;*.zip|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await ImportAiVoices(dlg.FileNames);
    }

    void AiVoices_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                  files.Any(f => f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void AiVoices_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportAiVoices(files);
    }

    async Task ImportAiVoices(IEnumerable<string> files)
    {
        AiAddButton.IsEnabled = false;
        AiImportText.Visibility = Visibility.Visible;
        var problems = new List<string>();
        try
        {
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                AiImportText.Text = $"Adding {name}…";
                try
                {
                    var progress = new Progress<string>(t => AiImportText.Text = $"{name}: {t}");
                    var voice = await Task.Run(() => ImportOne(file, progress));
                    Ai.Voices.Add(voice);
                    ChangeLog.Write($"AI voice added: {voice.Name} ({voice.Version}, {voice.SampleRate} Hz) from {file}");
                    if (Ai.Voices.Count == 1 || Ai.Current == null) Ai.Selected = voice.Id;
                }
                catch (Exception ex)
                {
                    ChangeLog.Write($"AI voice import of {file} failed: {ex.Message}");
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            AiAddButton.IsEnabled = true;
            AiImportText.Visibility = Visibility.Collapsed;
        }
        _settings.Save();
        RefreshAiVoices();
        if (problems.Count > 0) ShowAiNotice("Couldn't add " + string.Join("\n", problems));
        if (Ai.Enabled && !RvcEngine.Active) StartAi();
        UpdateAiState();
    }

    /// <summary>A .pth, or a .zip as voice models are often shared (the .pth plus an .index, sometimes a picture).</summary>
    static AiVoice ImportOne(string file, IProgress<string> progress)
    {
        if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return RvcModelImport.Import(file, progress);
        using var zip = ZipFile.OpenRead(file);
        var pth = zip.Entries.Where(en => en.FullName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(en => en.Length).FirstOrDefault()
                  ?? throw new InvalidDataException("There's no .pth voice model in this zip.");
        var tmpDir = Path.Combine(Path.GetTempPath(), "WaveFix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var tmp = Path.Combine(tmpDir, "model.pth");
            pth.ExtractToFile(tmp);
            var v = RvcModelImport.Import(tmp, progress);
            v.Name = Path.GetFileNameWithoutExtension(file);
            v.Source = file;
            // The feature index RVC makes for use is the "added_..." one ("trained_..." isn't usable).
            var index = zip.Entries.Where(en => en.FullName.EndsWith(".index", StringComparison.OrdinalIgnoreCase)
                                                && !Path.GetFileName(en.FullName).StartsWith("trained", StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(en => Path.GetFileName(en.FullName).StartsWith("added", StringComparison.OrdinalIgnoreCase))
                                   .ThenByDescending(en => en.Length).FirstOrDefault();
            if (index != null)
            {
                progress.Report("Adding its index…");
                var ti = Path.Combine(tmpDir, "model.index");
                index.ExtractToFile(ti);
                try { RvcModelImport.AddIndex(v, ti); }
                catch (Exception ex) { ChangeLog.Write($"AI voice: the index in {file} wasn't used: {ex.Message}"); }
            }
            var pic = zip.Entries.Where(en => RvcModelImport.ImageExtensions.Contains(Path.GetExtension(en.FullName).ToLowerInvariant()) && en.Length is > 0 and < 20_000_000)
                                 .OrderByDescending(en => en.Length).FirstOrDefault();
            if (pic != null)
            {
                var tp = Path.Combine(tmpDir, "picture" + Path.GetExtension(pic.FullName).ToLowerInvariant());
                pic.ExtractToFile(tp);
                try { RvcModelImport.SetImage(v, tp); } catch { }
            }
            return v;
        }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }
    }

    // ---------------- One-time download ----------------

    void UpdateAiDownload()
    {
        bool ready = RvcHelpers.Ready;
        AiDownloadCard.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready && _aiDownload == null)
            AiDownloadText.Text = $"AI voices need two shared models: one that hears what you say and one that hears your pitch. " +
                                  $"They're {RvcHelpers.TotalSize / 1e6:0} MB together and download once.";
    }

    async void AiDownload_Click(object sender, RoutedEventArgs e)
    {
        _aiDownload = new CancellationTokenSource();
        AiDownloadButton.IsEnabled = false;
        AiCancelButton.Visibility = Visibility.Visible;
        AiDownloadProgress.Visibility = Visibility.Visible;
        AiDownloadProgress.Value = 0;
        ChangeLog.Write("AI voices: downloading the shared models");
        try
        {
            var progress = new Progress<(double Fraction, string Text)>(p => { AiDownloadProgress.Value = p.Fraction; AiDownloadText.Text = p.Text; });
            await RvcHelpers.DownloadAsync(progress, _aiDownload.Token);
            ChangeLog.Write("AI voices: shared models downloaded");
        }
        catch (OperationCanceledException) { AiDownloadText.Text = "Download cancelled. It continues where it stopped next time."; }
        catch (Exception ex) { AiDownloadText.Text = ex.Message + " Check your internet connection and try again."; }
        finally
        {
            _aiDownload = null;
            AiDownloadButton.IsEnabled = true;
            AiCancelButton.Visibility = Visibility.Collapsed;
            AiDownloadProgress.Visibility = Visibility.Collapsed;
        }
        UpdateAiDownload();
        if (Ai.Enabled && !RvcEngine.Active) StartAi();
        UpdateAiState();
    }

    void AiCancel_Click(object sender, RoutedEventArgs e) => _aiDownload?.Cancel();

    // ---------------- Options ----------------

    void AiOptions_Click(object sender, RoutedEventArgs e)
    {
        bool gpu = AiGpuSwitch.IsChecked == true, silent = AiSilentSwitch.IsChecked == true;
        bool gpuChanged = gpu != Ai.UseGraphicsCard;
        Ai.UseGraphicsCard = gpu;
        Ai.SilentWhenUnavailable = silent;
        _settings.Save();
        _ai.PreferGpu = gpu;
        if (gpuChanged && RvcEngine.Active) { _ai.Stop(); StartAi(); }
        else ApplyEffects();
    }

    void ShowAiNotice(string text)
    {
        AiNoticeText.Text = text;
        AiNotice.Visibility = Visibility.Visible;
    }

    void CloseAiNotice_Click(object sender, RoutedEventArgs e) => AiNotice.Visibility = Visibility.Collapsed;

    /// <summary>On exit: stop converting and put the mic back to the normal voice (or silence, if chosen).</summary>
    void StopAiVoiceOnExit()
    {
        _monitor.Dispose();
        _aiDownload?.Cancel();
        if (!RvcEngine.Active) return;
        _ai.Stop();
        try { if (Mic.DeviceId != null) _engine.Apply(_settings, Mic.DeviceId); } catch { }
    }
}
