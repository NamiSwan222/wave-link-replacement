using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveFix.Rvc;

namespace WaveFix;

/// <summary>Voices online: search Hugging Face for RVC voices and add them in one click.</summary>
public partial class MainWindow
{
    readonly ObservableCollection<OnlineVoice> _onlineVoices = new();
    CancellationTokenSource? _voiceSearch;
    bool _voiceInstalling;

    async void AiBrowse_Click(object sender, RoutedEventArgs e)
    {
        AiBrowseResults.ItemsSource = _onlineVoices;
        AiBrowseCard.Visibility = Visibility.Visible;
        AiBrowseSearch.Focus();
        if (_onlineVoices.Count == 0) await SearchVoices();
    }

    void AiBrowseClose_Click(object sender, RoutedEventArgs e)
    {
        _voiceSearch?.Cancel();
        AiBrowseCard.Visibility = Visibility.Collapsed;
    }

    void AiBrowseSearch_Changed(object sender, TextChangedEventArgs e) =>
        AiBrowseHint.Visibility = AiBrowseSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void AiBrowseSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchVoices();
    }

    async Task SearchVoices()
    {
        _voiceSearch?.Cancel();
        var cts = _voiceSearch = new CancellationTokenSource();
        var q = AiBrowseSearch.Text.Trim();
        AiBrowseStatus.Text = q.Length == 0 ? "Loading the most popular voices…" : $"Searching for \"{q}\"…";
        try
        {
            var found = await VoiceCatalog.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            _onlineVoices.Clear();
            foreach (var v in found) _onlineVoices.Add(v);
            AiBrowseStatus.Text = found.Count == 0
                ? (q.Length == 0 ? "No voices found." : $"No voices found for \"{q}\". Try a shorter or different name.")
                : $"{found.Count} voice{(found.Count == 1 ? "" : "s")}{(q.Length == 0 ? ", most downloaded first" : "")}. Add downloads the model (usually 50-60 MB) and its index if it has one.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AiBrowseStatus.Text = $"Couldn't search: {ex.Message} Check your internet connection."; }
    }

    void AiBrowsePage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OnlineVoice v)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(v.Page) { UseShellExecute = true }); } catch { }
    }

    async void AiBrowseAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OnlineVoice v || _voiceInstalling) return;
        if (Ai.Voices.Any(x => x.Source == v.Page)) { AiBrowseStatus.Text = $"{v.Name} is already in your voices."; return; }
        _voiceInstalling = true;
        var cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(t => AiBrowseStatus.Text = $"{v.Name}: {t}");
            var voice = await VoiceCatalog.InstallAsync(v, progress, cts.Token);
            Ai.Voices.Add(voice);
            if (Ai.Current == null || Ai.Voices.Count == 1) Ai.Selected = voice.Id;
            _settings.Save();
            ChangeLog.Write($"AI voice added from {v.Page} ({voice.Version}, {voice.SampleRate} Hz{(voice.IndexFile != null ? ", with index" : "")})");
            AiBrowseStatus.Text = $"Added {v.Name}" + (voice.IndexFile != null ? " with its index." : ".") + " Pick it in your voices above.";
            RefreshAiVoices();
            if (Ai.Enabled && !RvcEngine.Active) StartAi();
            UpdateAiState();
        }
        catch (Exception ex)
        {
            ChangeLog.Write($"Voice browser: {v.Id} couldn't be added: {ex.Message}");
            AiBrowseStatus.Text = $"Couldn't add {v.Name}: {ex.Message}";
        }
        finally { _voiceInstalling = false; }
    }
}
