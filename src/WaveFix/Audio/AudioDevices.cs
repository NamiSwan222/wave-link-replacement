using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;

namespace WaveFix;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed record DeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The microphone: gain, mute and a peak meter, through the endpoint's own volume and meter
/// interfaces. WaveFix never opens an audio stream on the mic, so it never plays or records it.
/// </summary>
public sealed class MicController : Observable, IDisposable
{
    readonly MMDeviceEnumerator _enum = new();
    MMDevice? _device;
    bool _updating;

    public ObservableCollection<DeviceChoice> Devices { get; } = new();

    DeviceChoice? _selected;
    public DeviceChoice? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value?.Id != null) Open(value.Id); }
    }

    double _gain;
    public double Gain // 0..100
    {
        get => _gain;
        set
        {
            if (!Set(ref _gain, value) || _updating || _device == null) return;
            try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(value / 100.0); } catch { }
        }
    }

    bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _updating || _device == null) return;
            try { _device.AudioEndpointVolume.Mute = value; } catch { }
        }
    }

    double _level;
    public double Level { get => _level; private set => Set(ref _level, value); } // 0..1, perceptual

    public string? DeviceId => _device?.ID;
    public event Action? DeviceChanged;

    public void RefreshDevices(string? preferredId)
    {
        Devices.Clear();
        foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            Devices.Add(new DeviceChoice(d.ID, d.FriendlyName));

        var pick = Devices.FirstOrDefault(d => d.Id == preferredId)
                   ?? Devices.FirstOrDefault(d => d.Name.Contains("Wave:3", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                   ?? Devices.FirstOrDefault(d => !d.Name.Contains("Elgato Virtual", StringComparison.OrdinalIgnoreCase))
                   ?? Devices.FirstOrDefault();
        Selected = pick;
    }

    void Open(string id)
    {
        if (_device != null) { try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { } _device.Dispose(); }
        _device = _enum.GetDevice(id);
        _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        ReadVolume();
        DeviceChanged?.Invoke();
    }

    void OnVolume(AudioVolumeNotificationData _) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReadVolume);

    void ReadVolume()
    {
        if (_device == null) return;
        _updating = true;
        try
        {
            Gain = Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            Muted = _device.AudioEndpointVolume.Mute;
        }
        catch { }
        finally { _updating = false; }
    }

    /// <summary>Called on a UI timer. The meter only moves while some app is using the mic.</summary>
    public void PollMeter()
    {
        if (_device == null) { Level = 0; return; }
        float peak;
        try { peak = _device.AudioMeterInformation.MasterPeakValue; } catch { peak = 0; }
        // Map -60..0 dB onto 0..1 so quiet speech is visible.
        double db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        double target = Math.Clamp((db + 60) / 60, 0, 1);
        Level = target > Level ? target : Level * 0.85 + target * 0.15; // fast attack, slow release
    }

    public void Dispose() { _device?.Dispose(); _enum.Dispose(); }
}

/// <summary>
/// One app in the mixer: every process with the same app name (Discord runs several) and all
/// of their audio sessions across output devices.
/// </summary>
public sealed class AppChannel : Observable
{
    readonly AppMixer _mixer;
    bool _updating;
    public string Name { get; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    internal List<uint> Pids { get; } = new();
    internal List<AudioSessionControl> Sessions { get; } = new();

    public AppChannel(AppMixer mixer, string name) { _mixer = mixer; Name = name; }

    double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value) || _updating) return;
            foreach (var s in Sessions) try { s.SimpleAudioVolume.Volume = (float)(value / 100.0); } catch { }
        }
    }

    bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _updating) return;
            foreach (var s in Sessions) try { s.SimpleAudioVolume.Mute = value; } catch { }
        }
    }

    DeviceChoice? _output;
    public DeviceChoice? Output
    {
        get => _output;
        set
        {
            if (!Set(ref _output, value) || _updating || value == null) return;
            foreach (var pid in Pids) AudioPolicyConfig.SetAppOutput(pid, value.Id);
        }
    }

    public ObservableCollection<DeviceChoice> Outputs => _mixer.Outputs;

    internal void Sync()
    {
        _updating = true;
        try
        {
            var first = Sessions.FirstOrDefault();
            if (first != null)
            {
                Volume = Math.Round(first.SimpleAudioVolume.Volume * 100);
                Muted = first.SimpleAudioVolume.Mute;
            }
            var pinned = Pids.Select(AudioPolicyConfig.GetAppOutput).FirstOrDefault(p => p != null);
            Output = Outputs.FirstOrDefault(o => o.Id == pinned) ?? Outputs[0];
        }
        catch { }
        finally { _updating = false; }
    }
}

/// <summary>Per-app volume, mute and output device, like Wave Link's channels.</summary>
public sealed class AppMixer : IDisposable
{
    readonly MMDeviceEnumerator _enum = new();
    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
        { "WaveFix", "audiodg", "Elgato.WaveLink", "ShellExperienceHost", "SystemSettings" };

    public ObservableCollection<AppChannel> Apps { get; } = new();
    public ObservableCollection<DeviceChoice> Outputs { get; } = new();
    public bool RoutingAvailable => AudioPolicyConfig.IsAvailable;

    public void Refresh()
    {
        // Output device list ("Default" first).
        var devices = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var wanted = new List<DeviceChoice> { new(null, "Default") };
        wanted.AddRange(devices.Select(d => new DeviceChoice(d.ID, d.FriendlyName)));
        if (!wanted.SequenceEqual(Outputs))
        {
            Outputs.Clear();
            foreach (var w in wanted) Outputs.Add(w);
        }

        // Collect live sessions per app name (all of its processes) across every output device.
        var found = new Dictionary<string, (HashSet<uint> pids, List<AudioSessionControl> sessions)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in devices)
        {
            SessionCollection sessions;
            try { dev.AudioSessionManager.RefreshSessions(); sessions = dev.AudioSessionManager.Sessions; }
            catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    var pid = s.GetProcessID;
                    if (pid == 0) continue;
                    var name = NameOf(pid);
                    if (name == null) continue;
                    if (!found.TryGetValue(name, out var entry))
                    {
                        entry = (new HashSet<uint>(), new List<AudioSessionControl>());
                        found[name] = entry;
                    }
                    entry.pids.Add(pid);
                    entry.sessions.Add(s);
                }
                catch { }
            }
        }

        foreach (var gone in Apps.Where(a => !found.ContainsKey(a.Name)).ToList()) Apps.Remove(gone);
        foreach (var (name, (pids, sessions)) in found)
        {
            var app = Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                app = new AppChannel(this, name);
                int idx = 0;
                while (idx < Apps.Count && string.Compare(Apps[idx].Name, name, StringComparison.OrdinalIgnoreCase) < 0) idx++;
                Apps.Insert(idx, app);
            }
            app.Pids.Clear();
            app.Pids.AddRange(pids);
            app.Sessions.Clear();
            app.Sessions.AddRange(sessions);
            app.Sync();
        }
    }

    readonly Dictionary<uint, string?> _names = new();

    /// <summary>Friendly app name for a process, or null to hide it. Cached per process id.</summary>
    string? NameOf(uint pid)
    {
        if (_names.TryGetValue(pid, out var cached)) return cached;
        string? name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            if (Hidden.Contains(name)) name = null;
            else
            {
                try { var desc = p.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(desc)) name = desc; } catch { }
            }
        }
        catch { return null; } // process already gone: don't cache
        _names[pid] = name;
        return name;
    }

    public void Dispose() => _enum.Dispose();
}
