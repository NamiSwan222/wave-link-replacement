using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WaveFix;

/// <summary>The Soundboard page: sounds mixed into the mic inside the audio engine.</summary>
public partial class MainWindow
{
    readonly SoundboardEngine _soundboard = new();
    ObservableCollection<SoundEntry> _sounds = new();

    static float Curve(double percent) => (float)Math.Pow(Math.Clamp(percent, 0, 100) / 100.0, 2); // feels even across the slider

    void InitSoundboard()
    {
        var sb = _settings.Soundboard;
        _sounds = new ObservableCollection<SoundEntry>(sb.Sounds.Where(s => File.Exists(s.File)));
        SoundList.ItemsSource = _sounds;
        _sounds.CollectionChanged += (_, _) => { EmptySounds.Visibility = _sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed; };
        EmptySounds.Visibility = _sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SoundboardSwitch.IsChecked = sb.Enabled;
        SoundVolumeSlider.Value = sb.Volume;
        HearMyselfSwitch.IsChecked = sb.HearMyself;
        MyVolumeSlider.Value = sb.MyVolume;
        MyVolumeSlider.IsEnabled = sb.HearMyself;
        ApplySoundboardVolumes();

        foreach (var s in _sounds) AddSoundShortcutRow(s);
        if (sb.Enabled) _soundboard.Start();
        UpdateSoundboardStatus();
    }

    void ApplySoundboardVolumes()
    {
        var sb = _settings.Soundboard;
        _soundboard.MicVolume = Curve(sb.Volume);
        _soundboard.MonitorVolume = Curve(sb.MyVolume);
        _soundboard.Monitor = sb.HearMyself;
        SoundVolumeValue.Text = $"{sb.Volume:0}%";
    }

    void UpdateSoundboardStatus()
    {
        var sb = _settings.Soundboard;
        string? notice = null;
        if (!VoiceFx.Available) notice = "This copy of WaveFix was built without its audio engine plugin, so sounds can't reach your mic. Install the latest release.";
        else if (sb.Enabled && _soundboard.Error is { } err) notice = $"The soundboard couldn't start: {err}";
        else if (sb.Enabled && !_engine.IsInstalled) notice = "Finish setup first (Microphone page): sounds are mixed in by the same audio add-on as your effects.";
        else if (sb.Enabled && !_soundboard.Connected) notice = "Waiting for Windows audio to pick up the soundboard. If this stays, make sure setup is finished, then turn the soundboard off and on again.";
        SoundboardNotice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;
        SoundboardNoticeText.Text = notice ?? "";

        SoundboardStateText.Text = !sb.Enabled ? "Off: sounds won't play into your mic."
            : _soundboard.Connected ? "On: click a sound or press its key." : "On, connecting…";
        SoundboardSwitch.IsEnabled = VoiceFx.Available;
    }

    void SoundboardSwitch_Click(object sender, RoutedEventArgs e) => SetSoundboard(SoundboardSwitch.IsChecked == true, "switch clicked");

    void SetSoundboard(bool on, string why)
    {
        SoundboardSwitch.IsChecked = on;
        if (_settings.Soundboard.Enabled == on) return;
        _settings.Soundboard.Enabled = on;
        ChangeLog.Write($"Soundboard {(on ? "on" : "off")} ({why})");
        if (on) _soundboard.Start(); else _soundboard.Stop();
        ApplyEffects(); // puts the plugin's soundboard input in (or takes it out of) the chain
        UpdateSoundboardStatus();
    }

    void HearMyself_Click(object sender, RoutedEventArgs e)
    {
        _settings.Soundboard.HearMyself = HearMyselfSwitch.IsChecked == true;
        MyVolumeSlider.IsEnabled = _settings.Soundboard.HearMyself;
        ApplySoundboardVolumes();
        _settings.Save();
    }

    void SoundVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SoundVolumeValue == null || _loading) return;
        _settings.Soundboard.Volume = SoundVolumeSlider.Value;
        _settings.Soundboard.MyVolume = MyVolumeSlider.Value;
        ApplySoundboardVolumes();
        _saveSoon.Stop(); _saveSoon.Start();
    }

    readonly System.Windows.Threading.DispatcherTimer _saveSoon = CreateSaveTimer();
    static System.Windows.Threading.DispatcherTimer CreateSaveTimer() => new() { Interval = TimeSpan.FromMilliseconds(400) };

    void AddSounds_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add sounds",
            Filter = "Sounds|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = AddSounds(dlg.FileNames);
    }

    static readonly string[] SoundExtensions = { ".mp3", ".wav", ".m4a", ".wma", ".aac", ".flac" };

    void Soundboard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Soundboard_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            // Folders add every sound file inside them.
            var files = paths.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => SoundExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : new[] { p }).ToArray();
            _ = AddSounds(files);
        }
        e.Handled = true;
    }

    void CloseSoundProblems_Click(object sender, RoutedEventArgs e) => SoundAddProblems.Visibility = Visibility.Collapsed;

    async Task AddSounds(string[] files)
    {
        AddSoundsButton.IsEnabled = false;
        SoundAddProblems.Visibility = Visibility.Collapsed;
        var problems = new List<string>();
        try
        {
            Directory.CreateDirectory(SoundboardSettings.Folder);
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    // Check it decodes (and isn't too long) before adding it.
                    await Task.Run(() => SoundboardEngine.Check(file));
                    var entry = new SoundEntry { Name = name };
                    var dest = Path.Combine(SoundboardSettings.Folder, entry.Id + Path.GetExtension(file));
                    File.Copy(file, dest, overwrite: true);
                    _soundboard.Forget(file);
                    entry.File = dest;
                    _sounds.Add(entry);
                    AddSoundShortcutRow(entry);
                }
                catch (Exception ex)
                {
                    var why = ex is InvalidOperationException ? ex.Message
                        : ex is System.Runtime.InteropServices.COMException ? "Windows can't decode this file (an unusual format, or it's damaged)."
                        : ex.Message;
                    problems.Add($"{name}: {why}");
                    ChangeLog.Write($"Sound not added: {file}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally { AddSoundsButton.IsEnabled = true; }
        SaveSounds();
        if (problems.Count > 0)
        {
            // Stays on the page until dismissed, so it can't be missed.
            SoundAddProblemsText.Text = (problems.Count == 1 ? "This sound couldn't be added:\n" : "These sounds couldn't be added:\n") + string.Join("\n", problems);
            SoundAddProblems.Visibility = Visibility.Visible;
        }
    }

    void SaveSounds()
    {
        _settings.Soundboard.Sounds = _sounds.ToList();
        _settings.Save();
    }

    void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SoundEntry s) PlaySound(s);
    }

    void PlaySound(SoundEntry s)
    {
        if (!_settings.Soundboard.Enabled)
        {
            Osd.Show(this, "", "Soundboard is off");
            return;
        }
        _soundboard.Play(s.File, Curve(s.Volume)).ContinueWith(t =>
        {
            if (t.Exception != null) Dispatcher.BeginInvoke(() => Osd.Show(this, "", $"Couldn't play {s.Name}"));
        });
    }

    void StopSounds_Click(object sender, RoutedEventArgs e) => _soundboard.StopAll();

    void RemoveSound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SoundEntry s) return;
        _sounds.Remove(s);
        if (_shortcutRows.FirstOrDefault(r => r.Action.Id == s.ShortcutId) is { } row)
        {
            SaveShortcut(row, null);
            _shortcutRows.Remove(row);
        }
        _soundboard.Forget(s.File);
        try { File.Delete(s.File); } catch { /* playing right now; harmless leftover */ }
        SaveSounds();
    }

    /// <summary>Each sound gets a row on the Shortcuts page; its key shows on the sound's tile.</summary>
    void AddSoundShortcutRow(SoundEntry s)
    {
        var row = new ShortcutRow(new ShortcutAction(s.ShortcutId, $"Play \"{s.Name}\"", "Soundboard sound", ""));
        if (_settings.Shortcuts.TryGetValue(s.ShortcutId, out var text) && Hotkey.Parse(text) is { } hk)
        {
            row.Key = hk;
            row.Error = _hotkeys.Set(s.ShortcutId, hk);
        }
        s.KeyText = row.Key?.ToString() ?? "No key";
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShortcutRow.Key)) s.KeyText = row.Key?.ToString() ?? "No key"; };
        _shortcutRows.Add(row);
    }

    /// <summary>Shortcut handler for sound keys; returns true if it was one.</summary>
    bool OnSoundHotkey(string action)
    {
        if (action == "stop-sounds") { _soundboard.StopAll(); Osd.Show(this, "", "Sounds stopped"); return true; }
        if (!action.StartsWith("sound:")) return false;
        if (_sounds.FirstOrDefault(s => s.ShortcutId == action) is { } sound) PlaySound(sound);
        return true;
    }
}
