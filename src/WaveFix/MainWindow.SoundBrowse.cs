using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveFix;

/// <summary>Sounds online: search openly licensed sounds, preview them, add them in one click.</summary>
public partial class MainWindow
{
    readonly ObservableCollection<OnlineSound> _onlineSounds = new();
    CancellationTokenSource? _soundSearch;
    WasapiOut? _preview;
    MediaFoundationReader? _previewReader;
    OnlineSound? _previewing;

    void SoundBrowse_Click(object sender, RoutedEventArgs e)
    {
        SoundBrowseResults.ItemsSource = _onlineSounds;
        SoundBrowseCard.Visibility = Visibility.Visible;
        if (_onlineSounds.Count == 0) SoundBrowseStatus.Text = "Type what you're looking for and press Enter.";
        SoundBrowseSearch.Focus();
    }

    void SoundBrowseClose_Click(object sender, RoutedEventArgs e)
    {
        _soundSearch?.Cancel();
        StopPreview();
        SoundBrowseCard.Visibility = Visibility.Collapsed;
    }

    void SoundBrowseSearch_Changed(object sender, TextChangedEventArgs e) =>
        SoundBrowseHint.Visibility = SoundBrowseSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void SoundBrowseSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var q = SoundBrowseSearch.Text.Trim();
        if (q.Length == 0) return;
        _soundSearch?.Cancel();
        var cts = _soundSearch = new CancellationTokenSource();
        SoundBrowseStatus.Text = $"Searching for \"{q}\"…";
        try
        {
            var found = await SoundCatalog.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            _onlineSounds.Clear();
            foreach (var s in found) _onlineSounds.Add(s);
            SoundBrowseStatus.Text = found.Count == 0 ? $"No sounds found for \"{q}\". Try another word." : $"{found.Count} sounds. Play to preview (only you hear it), Add to put it on your board.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SoundBrowseStatus.Text = $"Couldn't search: {ex.Message} Check your internet connection."; }
    }

    void SoundBrowsePlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OnlineSound s) return;
        bool same = _previewing == s && _preview?.PlaybackState == PlaybackState.Playing;
        StopPreview();
        if (same) return; // second click stops it
        try
        {
            _previewReader = new MediaFoundationReader(s.Url); // streams it; plays to your speakers only
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = dev.AudioClient.MixFormat;
            ISampleProvider sp = _previewReader.ToSampleProvider();
            if (sp.WaveFormat.SampleRate != mix.SampleRate) sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
            if (sp.WaveFormat.Channels == 1 && mix.Channels == 2) sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels == 2 && mix.Channels == 1) sp = new StereoToMonoSampleProvider(sp);
            _preview = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
            _preview.Init(sp);
            _preview.Play();
            _previewing = s;
        }
        catch (Exception ex) { SoundBrowseStatus.Text = $"Couldn't play the preview: {ex.Message}"; StopPreview(); }
    }

    void StopPreview()
    {
        try { _preview?.Stop(); _preview?.Dispose(); } catch { }
        try { _previewReader?.Dispose(); } catch { }
        _preview = null; _previewReader = null; _previewing = null;
    }

    async void SoundBrowseAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OnlineSound s) return;
        if (sender is Button b) b.IsEnabled = false;
        SoundBrowseStatus.Text = $"Adding \"{s.Title}\"…";
        string? path = null;
        try
        {
            path = await SoundCatalog.DownloadAsync(s, CancellationToken.None);
            int before = _sounds.Count;
            await AddSounds(new[] { path }, s.Credit);
            bool added = _sounds.Count > before;
            if (added) ChangeLog.Write($"Sound added from {s.Page}: {s.Credit}");
            SoundBrowseStatus.Text = added ? $"Added \"{s.Title}\" to your board." : $"\"{s.Title}\" couldn't be added (see the message above your sounds).";
        }
        catch (Exception ex) { SoundBrowseStatus.Text = $"Couldn't download \"{s.Title}\": {ex.Message}"; }
        finally
        {
            if (path != null) try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); } catch { }
            if (sender is Button b2) b2.IsEnabled = true;
        }
    }
}
