using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace WinNotch;

// Quick toggles on the default page: mute mic, do not disturb, dark mode, Bluetooth, keep awake, lock.
// Each lights up in the accent colour while it's on.
public partial class MainWindow
{
    private sealed record QuickToggle(string Key, string GlyphOn, string GlyphOff, string NameOn, string NameOff, Func<bool?> IsOn, Func<Task> Toggle);

    private readonly List<(QuickToggle Toggle, Button Button)> _toggles = new();
    private bool _keepAwake;
    private bool? _bluetoothOn;
    private Radio? _bluetooth;

    private void BuildQuickToggles()
    {
        QuickToggleRow.Children.Clear();
        _toggles.Clear();
        if (!_settings.QuickToggles)
        {
            QuickToggleRow.Visibility = Visibility.Collapsed;
            return;
        }

        var all = new List<QuickToggle>();
        if (_settings.ToggleMic)
            all.Add(new("mic", "", "", "Microphone muted - click to unmute", "Mute microphone", MicMuted, () => { SetMicMuted(MicMuted() != true); return Task.CompletedTask; }));
        if (_settings.ToggleDnd)
            all.Add(new("dnd", "", "", "Do not disturb is on - click to turn off", "Do not disturb", DndOn, ToggleDndAsync));
        if (_settings.ToggleTheme)
            all.Add(new("theme", "\uE790", "\uE790", "Dark mode - click for light", "Light mode - click for dark", DarkMode, () => { SetDarkMode(DarkMode() != true); return Task.CompletedTask; }));
        if (_settings.ToggleBluetooth)
            all.Add(new("bt", "", "", "Bluetooth is on - click to turn off", "Bluetooth is off - click to turn on", () => _bluetoothOn, ToggleBluetoothAsync));
        if (_settings.ToggleAwake)
            all.Add(new("awake", "", "", "Keeping the PC awake - click to stop", "Keep the PC and screen awake", () => _keepAwake, () => { SetKeepAwake(!_keepAwake); return Task.CompletedTask; }));
        if (_settings.ToggleLock)
            all.Add(new("lock", "", "", "Lock the PC", "Lock the PC", () => false, () => { _hovering = false; SetExpanded(false); LockWorkStation(); return Task.CompletedTask; }));

        foreach (var t in all)
        {
            var b = new Button { Style = (Style)FindResource("QuickToggle") };
            var toggle = t;
            b.Click += async (_, _) =>
            {
                try { await toggle.Toggle(); }
                catch (Exception ex) { DebugLog.Write($"toggle {toggle.Key} failed: {ex.Message}"); }
                RefreshToggles();
            };
            QuickToggleRow.Children.Add(b);
            _toggles.Add((t, b));
        }
        QuickToggleRow.Visibility = Vis(_toggles.Count > 0);
        RefreshToggles();
        _ = ReadBluetoothAsync();
    }

    /// <summary>Re-reads each toggle's state and lights them up (on opening, after a click, and every few seconds).</summary>
    private void RefreshToggles()
    {
        foreach (var (t, b) in _toggles)
        {
            bool? on = null;
            try { on = t.IsOn(); } catch { }
            b.Content = on == true ? t.GlyphOn : t.GlyphOff;
            b.ToolTip = on == true ? t.NameOn : t.NameOff;
            if (on == true)
            {
                b.SetResourceReference(BackgroundProperty, "Accent");
                b.Foreground = Brushes.Black;
            }
            else
            {
                b.SetResourceReference(BackgroundProperty, "Surface");
                b.Foreground = on == null && t.Key != "lock" ? new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)) : Brushes.White;
            }
        }
    }

    // ---------- Microphone ----------

    private static IEnumerable<IAudioEndpointVolume> Microphones()
    {
        var list = new List<IAudioEndpointVolume>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var iid = typeof(IAudioEndpointVolume).GUID;
            var seen = new HashSet<string>();
            // The default mic for apps, and the default for calls (often the same one)
            foreach (int role in new[] { 0, 2 })
            {
                if (enumerator.GetDefaultAudioEndpoint(1 /* capture */, role, out IMMDevice device) != 0) continue;
                device.GetId(out string id);
                if (!seen.Add(id ?? "")) continue;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out object ep) == 0 && ep is IAudioEndpointVolume v) list.Add(v);
            }
        }
        catch { }
        return list;
    }

    private static bool? MicMuted()
    {
        var mics = Microphones().ToList();
        if (mics.Count == 0) return null;
        return mics.All(m => m.GetMute(out bool muted) == 0 && muted);
    }

    private static void SetMicMuted(bool mute)
    {
        var context = Guid.Empty;
        foreach (var m in Microphones()) m.SetMute(mute, ref context);
    }

    // ---------- Do not disturb ----------
    // Windows has no public switch for this. The notch reads Windows' own "quiet hours" state and tries
    // to flip it; if Windows doesn't take it, it opens the notification settings instead.

    private const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    private static bool? DndOn()
    {
        try
        {
            ulong state = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            var buffer = new byte[4];
            uint size = 4;
            if (NtQueryWnfStateData(ref state, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size) != 0) return null;
            return BitConverter.ToInt32(buffer, 0) != 0;
        }
        catch
        {
            return null;
        }
    }

    private async Task ToggleDndAsync()
    {
        bool? before = DndOn();
        bool want = before != true;
        try
        {
            ulong state = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            var buffer = BitConverter.GetBytes(want ? 1 : 0);
            NtUpdateWnfStateData(ref state, buffer, 4, IntPtr.Zero, IntPtr.Zero, 0, 0);
        }
        catch { }
        await Task.Delay(500);
        if (DndOn() != want)
        {
            DebugLog.Write("do not disturb: Windows didn't take the change, opening notification settings");
            Run(() => WebTools.Open("ms-settings:notifications"));
        }
    }

    // ---------- Dark / light mode ----------

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool? DarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
    }

    private static void SetDarkMode(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        // Tell open apps and the taskbar to switch
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 200, out _);
    }

    // ---------- Bluetooth ----------

    private async Task ReadBluetoothAsync()
    {
        if (!_settings.QuickToggles || !_settings.ToggleBluetooth) return;
        try
        {
            if (_bluetooth == null)
            {
                await Radio.RequestAccessAsync();
                var radios = await Radio.GetRadiosAsync();
                _bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (_bluetooth != null)
                    _bluetooth.StateChanged += (_, _) => Dispatcher.InvokeAsync(() => { _ = ReadBluetoothAsync(); });
            }
            _bluetoothOn = _bluetooth == null ? null : _bluetooth.State == RadioState.On;
        }
        catch
        {
            _bluetoothOn = null;
        }
        RefreshToggles();
    }

    private async Task ToggleBluetoothAsync()
    {
        await ReadBluetoothAsync();
        if (_bluetooth == null)
        {
            Run(() => WebTools.Open("ms-settings:bluetooth"));
            return;
        }
        var result = await _bluetooth.SetStateAsync(_bluetooth.State == RadioState.On ? RadioState.Off : RadioState.On);
        if (result != RadioAccessStatus.Allowed) Run(() => WebTools.Open("ms-settings:bluetooth"));
        await ReadBluetoothAsync();
    }

    // ---------- Keep awake ----------

    private void SetKeepAwake(bool on)
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        _keepAwake = on;
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope, out uint changeStamp, byte[] buffer, ref uint bufferSize);
    [DllImport("ntdll.dll")]
    private static extern int NtUpdateWnfStateData(ref ulong stateName, byte[] buffer, uint length, IntPtr typeId, IntPtr explicitScope, uint matchingChangeStamp, uint checkStamp);
}
