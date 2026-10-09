using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Quit protection: Alt+F4 and Ctrl+Q (and optionally Ctrl+W) only work if you hold them for a moment.
// A quick press is ignored and the notch shows "Hold to close" with a filling bar.
public partial class MainWindow
{
    private sealed class QuitHold
    {
        public uint Vk;
        public string App = "";
        public DateTime Started;
        public bool Done;
    }

    private IntPtr _keyHook;
    private LowLevelMouseProc? _keyProc; // same callback shape as the mouse hook; kept so it isn't garbage-collected
    private QuitHold? _quitHold;
    private readonly DispatcherTimer _quitTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _quitTimerReady;
    private IntPtr _lastFgWindow;
    private string _lastFgApp = "";

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_F4 = 0x73, VK_Q = 0x51, VK_W = 0x57;
    private const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    private void ApplyQuitGuard()
    {
        if (!_quitTimerReady)
        {
            _quitTimerReady = true;
            _quitTimer.Tick += (_, _) => TickQuitHold();
        }

        if (_settings.QuitProtection && _keyHook == IntPtr.Zero)
        {
            _keyProc = KeyboardHook;
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, GetModuleHandle(null), 0);
        }
        else if (!_settings.QuitProtection) StopQuitGuard();
    }

    private void StopQuitGuard()
    {
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _keyHook = IntPtr.Zero;
        _quitHold = null;
        _quitTimer.Stop();
    }

    // Runs for every key press in Windows, so it has to be quick
    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                // A protected key is being held: swallow its repeats, and its release
                if (_quitHold is { } hold && k.vkCode == hold.Vk)
                {
                    if (up)
                    {
                        _quitHold = null;
                        if (!hold.Done) Dispatcher.BeginInvoke(new Action(CancelQuitHold));
                    }
                    return (IntPtr)1;
                }

                if (down && IsQuitShortcut(k) && ProtectedApp() is { } app)
                {
                    _quitHold = new QuitHold { Vk = k.vkCode, App = app, Started = DateTime.Now };
                    Dispatcher.BeginInvoke(new Action(() => _quitTimer.Start()));
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private bool IsQuitShortcut(KBDLLHOOKSTRUCT k)
    {
        bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        return k.vkCode switch
        {
            VK_F4 => alt && !ctrl,
            VK_Q => ctrl && !alt && !shift,
            VK_W => ctrl && !alt && !shift && _settings.QuitProtectCtrlW,
            _ => false,
        };
    }

    /// <summary>The app in front, if it should be protected (null = let the shortcut through).</summary>
    private string? ProtectedApp()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        if (fg != _lastFgWindow)
        {
            _lastFgWindow = fg;
            _lastFgApp = "";
            try
            {
                GetWindowThreadProcessId(fg, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                _lastFgApp = p.ProcessName;
            }
            catch { }
        }
        string app = _lastFgApp;
        if (app.Length == 0 || app.Equals("WinNotch", StringComparison.OrdinalIgnoreCase)) return null;

        static List<string> Names(string list) =>
            list.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToList();

        var only = Names(_settings.QuitOnlyApps);
        if (only.Count > 0 && !only.Contains(app, StringComparer.OrdinalIgnoreCase)) return null;
        if (Names(_settings.QuitIgnoreApps).Contains(app, StringComparer.OrdinalIgnoreCase)) return null;
        return app;
    }

    private void TickQuitHold()
    {
        if (_quitHold is not { } hold || hold.Done)
        {
            _quitTimer.Stop();
            return;
        }

        double needed = Math.Clamp(_settings.QuitHoldMs, 200, 3000);
        double progress = (DateTime.Now - hold.Started).TotalMilliseconds / needed;
        if (progress < 1)
        {
            ShowHud("", progress, null, "Hold to close", TimeSpan.FromSeconds(1), LowRed, overGames: true);
            return;
        }

        // Held long enough: press it for real (the modifier is still held down)
        hold.Done = true;
        _quitTimer.Stop();
        EndHud(immediate: true);
        KeySender.Send(hold.Vk switch { VK_F4 => "F4", VK_Q => "Q", _ => "W" });
    }

    private void CancelQuitHold()
    {
        _quitTimer.Stop();
        ShowHud("", null, "Hold the keys to close", "", TimeSpan.FromSeconds(1.4), null, overGames: true);
    }
}
