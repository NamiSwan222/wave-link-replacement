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
            var choice = new RadioButton
            {
                Style = (Style)FindResource("ToneChoice"), GroupName = "aivoice", Margin = new Thickness(0),
                Content = voice.Name, Tag = $"RVC {voice.Version} · {voice.SampleRate / 1000} kHz      ",
                IsChecked = voice == current, MinWidth = 170,
            };
            AutomationProperties.SetName(choice, $"AI voice {voice.Name}");
            choice.Checked += (_, _) => SelectAiVoice(voice);
            var remove = new Button
            {
                Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 10, 0), ToolTip = "Remove",
                Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 10, Foreground = (Brush)FindResource("SubText") },
            };
            AutomationProperties.SetName(remove, $"Remove {voice.Name}");
            remove.Click += (_, _) => RemoveAiVoice(voice);
            grid.Children.Add(choice);
            grid.Children.Add(remove);
            AiVoiceList.Children.Add(grid);
        }
        AiEmpty.Visibility = Ai.Voices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _aiSyncing = true;
        AiPitchSlider.Value = current?.Pitch ?? 0;
        _aiSyncing = false;
        AiPitchSlider.IsEnabled = current != null;
        UpdateAiPitchLabel();
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

    /// <summary>A .pth, or a .zip as voice models are often shared (the .pth plus an .index).</summary>
    static AiVoice ImportOne(string file, IProgress<string> progress)
    {
        if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return RvcModelImport.Import(file, progress);
        using var zip = ZipFile.OpenRead(file);
        var pth = zip.Entries.Where(en => en.FullName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(en => en.Length).FirstOrDefault()
                  ?? throw new InvalidDataException("There's no .pth voice model in this zip.");
        var tmp = Path.Combine(Path.GetTempPath(), "WaveFix-" + Guid.NewGuid().ToString("N") + ".pth");
        try
        {
            pth.ExtractToFile(tmp);
            var v = RvcModelImport.Import(tmp, progress);
            v.Name = Path.GetFileNameWithoutExtension(file);
            v.Source = file;
            return v;
        }
        finally { try { File.Delete(tmp); } catch { } }
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
        _aiDownload?.Cancel();
        if (!RvcEngine.Active) return;
        _ai.Stop();
        try { if (Mic.DeviceId != null) _engine.Apply(_settings, Mic.DeviceId); } catch { }
    }
}
