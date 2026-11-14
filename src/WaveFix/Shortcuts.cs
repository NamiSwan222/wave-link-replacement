using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WaveFix;

/// <summary>A key plus modifiers, stored as text like "Ctrl+Alt+M".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public string Display => ToString().Replace("+", "  +  ");

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
        Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.Oem2 => "/", Key.Oem1 => ";", Key.Oem7 => "'", Key.OemOpenBrackets => "[", Key.Oem6 => "]", Key.Oem5 => "\\",
        Key.Next => "PageDown", Key.Prior => "PageUp", Key.Capital => "CapsLock", Key.Return => "Enter",
        _ => k.ToString(),
    };

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    var conv = new KeyConverter();
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (raw.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw[4..], out var n)) key = Key.NumPad0 + n;
                    else try { key = (Key?)conv.ConvertFromInvariantString(raw); } catch { key = ReverseOem(raw); }
                    break;
            }
        }
        return key is { } k ? new Hotkey(mods, k) : null;
    }

    static Key? ReverseOem(string s) => s switch
    {
        "`" => Key.OemTilde, "-" => Key.OemMinus, "=" => Key.OemPlus, "," => Key.OemComma, "." => Key.OemPeriod,
        "/" => Key.Oem2, ";" => Key.Oem1, "'" => Key.Oem7, "[" => Key.OemOpenBrackets, "]" => Key.Oem6, "\\" => Key.Oem5,
        _ => null,
    };
}

/// <summary>What a shortcut does. Ids are stored in settings, so don't rename them.</summary>
public sealed record ShortcutAction(string Id, string Title, string Description, string Glyph)
{
    public static readonly ShortcutAction[] All =
    {
        new("toggle-mute", "Mute / unmute mic", "Press once to mute, press again to talk.", ""),
        new("push-to-talk", "Push to talk", "Hold the keys to talk. Your mic stays muted when you let go.", ""),
        new("toggle-noise", "Noise removal on / off", "Quickly switch the background-noise cleanup.", ""),
        new("boost-up", "Mic boost up", "Makes you 3 dB louder.", ""),
        new("boost-down", "Mic boost down", "Makes you 3 dB quieter.", ""),
        new("next-tone", "Next voice tone", "Cycles Natural → Warm → Clear → Broadcast → Radio.", ""),
        new("show-app", "Open WaveFix", "Brings this window to the front.", ""),
    };
}

/// <summary>Registers system-wide hotkeys (they work while games and other apps have focus).</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly HwndSource _source;
    readonly Dictionary<int, string> _byId = new();
    readonly Dictionary<string, (int id, Hotkey key)> _byAction = new();
    int _nextId = 0x5A00;

    public event Action<string>? Pressed;

    public HotkeyManager(Window owner)
    {
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (or replaces) the hotkey for an action. Returns an error message on failure.</summary>
    public string? Set(string actionId, Hotkey? key)
    {
        Clear(actionId);
        if (key is not { } k) return null;
        if (_byAction.Values.Any(v => v.key == k)) return "Another WaveFix shortcut already uses these keys.";

        uint mods = MOD_NOREPEAT;
        if (k.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (k.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (k.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (k.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods, (uint)KeyInterop.VirtualKeyFromKey(k.Key)))
            return "Another app is already using these keys. Try a different combination.";
        _byId[id] = actionId;
        _byAction[actionId] = (id, k);
        return null;
    }

    public void Clear(string actionId)
    {
        if (!_byAction.Remove(actionId, out var e)) return;
        UnregisterHotKey(_hwnd, e.id);
        _byId.Remove(e.id);
    }

    /// <summary>True while the main key of an action's hotkey is physically held down.</summary>
    public bool IsHeld(string actionId) =>
        _byAction.TryGetValue(actionId, out var e) && (GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(e.key.Key)) & 0x8000) != 0;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _byId.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _byId.Keys.ToList()) UnregisterHotKey(_hwnd, id);
        _byId.Clear(); _byAction.Clear();
        _source.RemoveHook(WndProc);
    }
}

/// <summary>One row on the Shortcuts tab.</summary>
public sealed class ShortcutRow : Observable
{
    public ShortcutAction Action { get; }
    public ShortcutRow(ShortcutAction action) => Action = action;

    Hotkey? _key;
    public Hotkey? Key { get => _key; set { if (Set(ref _key, value)) { Raise(nameof(KeyText)); Raise(nameof(HasKey)); } } }

    bool _recording;
    public bool Recording { get => _recording; set { if (Set(ref _recording, value)) Raise(nameof(KeyText)); } }

    string? _error;
    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }

    public bool HasKey => _key != null;
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string KeyText => _recording ? "Press your keys…" : _key?.Display ?? "Click to set";
}
