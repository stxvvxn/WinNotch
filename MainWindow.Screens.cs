using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WinNotch;

// Which screen the notch sits on, and hiding it while something is fullscreen
public partial class MainWindow
{
    private readonly DispatcherTimer _screenTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private IntPtr _currentMonitor;
    private bool _hiddenForFullscreen;

    private void InitScreens()
    {
        // Re-centre when the resolution, monitor layout or scaling changes
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow);
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);

        _screenTimer.Tick += (_, _) =>
        {
            FollowMouseIfNeeded();
            CheckFullscreen();
            KeepOnTop();
        };
        _screenTimer.Start();
        PositionWindow();

        // Whenever another app comes to the front, put the notch straight back on top of it
        _foregroundHook = WinEventHandler;
        _foregroundHookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                                                _foregroundHook, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Closed += (_, _) => { if (_foregroundHookHandle != IntPtr.Zero) UnhookWinEvent(_foregroundHookHandle); };
    }

    // ---------- Always on top ----------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private WinEventDelegate? _foregroundHook; // kept in a field so it isn't garbage-collected
    private IntPtr _foregroundHookHandle;

    private void WinEventHandler(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // A new app came to the front: re-check full screen first, then reclaim the top spot
        CheckFullscreen();
        KeepOnTop();
    }

    // Other "always on top" windows (and some apps) can push the notch down; this puts it back above them.
    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || Pill.Visibility != Visibility.Visible) return;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
                                                 uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    // ---------- Placement ----------

    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;

        IntPtr monitor = PickMonitor();
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        _currentMonitor = monitor;

        // Work in real pixels so mixed-scaling setups (e.g. 150% laptop + 100% monitor) line up
        uint dpi = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;
        int widthPx = (int)Math.Round(Width * dpi / 96.0);
        int screenWidth = info.rcMonitor.Right - info.rcMonitor.Left;
        int x = info.rcMonitor.Left + (screenWidth - widthPx) / 2;

        const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, info.rcMonitor.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private IntPtr PickMonitor()
    {
        const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

        switch (_settings.Monitor)
        {
            case "mouse":
                GetCursorPos(out POINT p);
                return MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            case "primary":
                return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            default:
                foreach (var m in EnumerateMonitors())
                    if (string.Equals(m.Device, _settings.Monitor, StringComparison.OrdinalIgnoreCase))
                        return m.Handle;
                return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        }
    }

    // When set to follow the mouse, hop to whichever screen the mouse is on (but not while open)
    private void FollowMouseIfNeeded()
    {
        if (_settings.Monitor != "mouse" || _expanded || _hovering) return;
        GetCursorPos(out POINT p);
        if (MonitorFromPoint(p, 2) != _currentMonitor) PositionWindow();
    }

    /// <summary>Displays for the Settings window, e.g. ("\\.\DISPLAY2", "Display 2 · 2560×1440").</summary>
    public List<(string Device, string Label)> GetMonitorChoices()
    {
        var list = new List<(string, string)>();
        int n = 1;
        foreach (var m in EnumerateMonitors())
        {
            int w = m.Bounds.Right - m.Bounds.Left, h = m.Bounds.Bottom - m.Bounds.Top;
            list.Add((m.Device, $"Display {n++} · {w}×{h}{(m.Primary ? " (main)" : "")}"));
        }
        return list;
    }

    private static List<(IntPtr Handle, string Device, RECT Bounds, bool Primary)> EnumerateMonitors()
    {
        var result = new List<(IntPtr, string, RECT, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
                result.Add((hMonitor, info.szDevice, info.rcMonitor, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---------- Hide while fullscreen ----------

    private void CheckFullscreen()
    {
        bool fullscreen = _settings.HideInFullscreen && ForegroundIsFullscreen();
        if (fullscreen == _hiddenForFullscreen) return;

        _hiddenForFullscreen = fullscreen;
        ApplyNotchVisibility();
    }

    // A window is "fullscreen" if it exactly covers the screen the notch is on
    // (games, F11 browsers, fullscreen videos, slideshows). Maximised windows don't count.
    private bool ForegroundIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        string name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false; // the desktop

        // A normal maximised window (with a title bar) isn't full screen, even when the taskbar is set to auto-hide
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        if (IsZoomed(fg) && (GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;

        if (MonitorFromWindow(fg, 2) != _currentMonitor) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(_currentMonitor, ref info)) return false;
        var m = info.rcMonitor;
        return r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
    }

    // ---------- Win32 ----------

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
