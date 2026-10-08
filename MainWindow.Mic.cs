using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNotch;

// System-wide mic mute: mutes every microphone at the Windows level, so it works in every app
// (Teams, Discord, Zoom, games...). Button in the open notch, plus a global shortcut.
public partial class MainWindow
{
    private const int MicHotkeyId = 0x4D49;
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _micHooked, _micHotkeyRegistered, _micTimerReady;
    private bool? _micMuted; // null = no microphone found

    private void ApplyMic()
    {
        if (!_micTimerReady)
        {
            _micTimerReady = true;
            _micTimer.Tick += (_, _) => PollMic();
        }

        MicToolButton.Visibility = Vis(_settings.MicButton);

        // The shortcut works even when the button is hidden
        UnregisterMicHotkey();
        if (_hwnd != IntPtr.Zero && !string.IsNullOrWhiteSpace(_settings.MicHotkey)
            && KeySender.TryParseHotkey(_settings.MicHotkey, out uint mods, out uint key))
        {
            if (!_micHooked)
            {
                HwndSource.FromHwnd(_hwnd)?.AddHook(MicWndProc);
                _micHooked = true;
            }
            const uint MOD_NOREPEAT = 0x4000;
            _micHotkeyRegistered = RegisterHotKey(_hwnd, MicHotkeyId, mods | MOD_NOREPEAT, key);
        }

        bool needed = _settings.MicButton || _settings.MicShowDot || _micHotkeyRegistered;
        if (needed) _micTimer.Start(); else _micTimer.Stop();
        PollMic();
    }

    private void UnregisterMicHotkey()
    {
        if (!_micHotkeyRegistered) return;
        UnregisterHotKey(_hwnd, MicHotkeyId);
        _micHotkeyRegistered = false;
    }

    private IntPtr MicWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == MicHotkeyId)
        {
            handled = true;
            Dispatcher.InvokeAsync(ToggleMic);
        }
        return IntPtr.Zero;
    }

    private void MicToolButton_Click(object sender, RoutedEventArgs e) => ToggleMic();

    private void ToggleMic()
    {
        bool? current = ReadMicMute();
        if (current == null)
        {
            ShowTopMessage("No microphone found", LowRed, 3);
            return;
        }

        bool mute = !current.Value;
        int changed = 0;
        foreach (var mic in ActiveMics())
        {
            var ctx = Guid.Empty;
            if (mic.SetMute(mute, ref ctx) == 0) changed++;
        }
        if (changed == 0)
        {
            ShowTopMessage("Couldn't change the microphone", LowRed, 3);
            return;
        }

        UpdateMicVisuals(mute);
        if (_settings.MicPopup)
        {
            if (_expanded)
                ShowTopMessage(mute ? "Microphone muted" : "Microphone on", mute ? LowRed : ChargingGreen, 2);
            else
                ShowHud(mute ? "" : "", null, mute ? "Microphone muted" : "Microphone on", "",
                        iconBrush: mute ? LowRed : ChargingGreen);
        }
    }

    private void PollMic()
    {
        try
        {
            UpdateMicVisuals(ReadMicMute());
        }
        catch
        {
            UpdateMicVisuals(null);
        }
    }

    private void UpdateMicVisuals(bool? muted)
    {
        _micMuted = muted;
        bool isMuted = muted == true;
        MicMutedIcon.Visibility = Vis(isMuted && _settings.MicShowDot);
        MicToolButton.Content = isMuted ? "" : "";
        MicToolButton.Foreground = isMuted ? LowRed : System.Windows.Media.Brushes.White;
        MicToolButton.ToolTip = muted == null ? "No microphone found"
            : isMuted ? "Microphone muted (every app) — click to unmute"
            : "Mute your microphone (every app)";
    }

    // The default mic decides whether we count as muted
    private static bool? ReadMicMute()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(1 /* capture */, 0 /* console */, out IMMDevice device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out object obj) != 0) return null;
            ((IAudioEndpointVolume)obj).GetMute(out bool mute);
            return mute;
        }
        catch
        {
            return null;
        }
    }

    private static List<IAudioEndpointVolume> ActiveMics()
    {
        var list = new List<IAudioEndpointVolume>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(1 /* capture */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection devices) != 0) return list;
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out IMMDevice device) != 0) continue;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0)
                    list.Add((IAudioEndpointVolume)obj);
            }
        }
        catch { }
        return list;
    }
}
