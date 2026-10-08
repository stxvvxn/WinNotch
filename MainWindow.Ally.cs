using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinNotch;

// ROG Ally mode: touch, the open-notch button, in-game glance, battery details,
// low battery warnings, refresh rate, power mode, brightness/volume sliders and the on-screen keyboard.
public partial class MainWindow
{
    private const int AllyHotkeyId = 0x4E4F;
    private const int WM_HOTKEY = 0x0312;

    private readonly DispatcherTimer _allyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _batteryStatsTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _allyTimersHooked, _allyWndProcHooked, _hotkeyRegistered;
    private int _allyTicks;

    private bool _touchOpen;               // opened with a tap: stays open until you tap elsewhere
    private POINT _lastCursor;
    private DateTime _lastAllyInteraction = DateTime.Now;
    private bool _overlayActive;           // notch temporarily shown over a fullscreen game

    private bool KeepOpen => _settings.Handheld && (_touchOpen || _navActive);

    // ---------- Switching the mode on / off ----------

    private void ApplyAllyMode()
    {
        if (!_allyTimersHooked)
        {
            _allyTimersHooked = true;
            _allyTimer.Tick += (_, _) => AllyTick();
            _batteryStatsTimer.Tick += async (_, _) => await RefreshBatteryStatsAsync();
            _brightnessDebounce.Tick += (_, _) =>
            {
                _brightnessDebounce.Stop();
                int value = (int)Math.Round(BrightnessSlider.Value);
                Task.Run(() => WriteBrightness(value));
            };
        }

        if (_settings.Handheld)
        {
            if (!_allyWndProcHooked && _hwnd != IntPtr.Zero)
            {
                HwndSource.FromHwnd(_hwnd)?.AddHook(AllyWndProc);
                _allyWndProcHooked = true;
            }
            RegisterAllyHotkey();
            _ = RefreshBatteryHealthAsync();
            _allyTimer.Start();
            _batteryStatsTimer.Start();
            _ = RefreshBatteryStatsAsync();
            if (!_settings.AllyActionsAdded) LoadActions(); // adds the Steam / Xbox buttons once
        }
        else
        {
            StopAllyMode();
        }
        RefreshSections();
    }

    private void StopAllyMode()
    {
        UnregisterAllyHotkey();
        _allyTimer.Stop();
        _batteryStatsTimer.Stop();
        _touchOpen = false;
        EndNav();
        SetKeepAwake(false, announce: false);
        _downloadRate = 0;
    }

    // ---------- Open-notch shortcut (map an Ally back button to it in Armoury Crate) ----------

    private void RegisterAllyHotkey()
    {
        UnregisterAllyHotkey();
        if (_hwnd == IntPtr.Zero) return;
        if (!KeySender.TryParseHotkey(_settings.AllyHotkey, out uint mods, out uint key)) return;
        const uint MOD_NOREPEAT = 0x4000;
        _hotkeyRegistered = RegisterHotKey(_hwnd, AllyHotkeyId, mods | MOD_NOREPEAT, key);
    }

    private void UnregisterAllyHotkey()
    {
        if (!_hotkeyRegistered) return;
        UnregisterHotKey(_hwnd, AllyHotkeyId);
        _hotkeyRegistered = false;
    }

    private IntPtr AllyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == AllyHotkeyId)
        {
            handled = true;
            Dispatcher.InvokeAsync(OnAllyButton);
        }
        return IntPtr.Zero;
    }

    /// <summary>The shortcut or View + Menu on the controller: glance in games, otherwise open for the controller.</summary>
    private void OnAllyButton()
    {
        if (!_settings.Handheld || _userHidden) return;
        _lastAllyInteraction = DateTime.Now;

        if (_hiddenForFullscreen)
        {
            // In a game: first press shows a quick glance, pressing again while it shows opens the notch
            if (_overlayActive && _hudShowing && !_expanded)
            {
                _collapseTimer.Stop();
                SetExpanded(true);
                if (_expanded) StartNav();
            }
            else if (_expanded)
            {
                CloseFromAlly();
            }
            else
            {
                ShowGlance();
            }
            return;
        }

        if (_expanded && (_navActive || _touchOpen))
        {
            CloseFromAlly();
            return;
        }

        _collapseTimer.Stop();
        SetExpanded(true);
        if (_expanded) StartNav();
    }

    private void CloseFromAlly()
    {
        _touchOpen = false;
        EndNav();
        SetExpanded(false);
    }

    // ---------- Touch: tap to open, tap anywhere else to close ----------

    private void Pill_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (!_settings.Handheld) return;
        _lastAllyInteraction = DateTime.Now;
        if (_alarmTimer != null) StopAlarm();

        if (!_expanded)
        {
            _touchOpen = true;
            GetCursorPos(out _lastCursor);
            _collapseTimer.Stop();
            SetExpanded(true);
            e.Handled = true; // the opening tap shouldn't also press whatever is underneath
        }
        else
        {
            _touchOpen = true; // keep it open while you use it
        }
    }

    private void AllyTick()
    {
        _allyTicks++;
        PollController();

        if (_expanded && _touchOpen && !_navActive)
        {
            // A tap elsewhere moves the pointer outside the notch: close
            GetCursorPos(out POINT p);
            bool moved = p.X != _lastCursor.X || p.Y != _lastCursor.Y;
            bool pressed = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if ((moved || pressed) && !PillContainsScreenPoint(p))
            {
                CloseFromAlly();
                return;
            }
            if (moved && PillContainsScreenPoint(p)) _lastAllyInteraction = DateTime.Now;
            _lastCursor = p;
        }

        // Tidy away if left open and untouched for a while
        if (_expanded && KeepOpen && !_hovering && DateTime.Now - _lastAllyInteraction > TimeSpan.FromSeconds(20))
        {
            CloseFromAlly();
            return;
        }

        if (_navActive) UpdateNavRing();

        if (_allyTicks % 20 == 0)
        {
            AllyEverySecond();

            // Keep the panel's numbers fresh while open
            if (_expanded)
            {
                UpdateAllyStatsText();
                SyncVolumeSlider();
            }
        }
    }

    private bool PillContainsScreenPoint(POINT p)
    {
        try
        {
            if (ScreenRectContains(Pill, p)) return true;
            // Tapping a bubble counts as "inside" too
            return _bubbles.Any(b => b.IsHitTestVisible && ScreenRectContains(b, p));
        }
        catch
        {
            return false;
        }
    }

    private static bool ScreenRectContains(FrameworkElement element, POINT p)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
    }

    // ---------- Showing pop-ups over fullscreen games ----------

    private DispatcherTimer? _overlayEndTimer;

    private void EndOverlaySoon()
    {
        _overlayEndTimer?.Stop();
        _overlayEndTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _overlayEndTimer.Tick += (_, _) =>
        {
            _overlayEndTimer?.Stop();
            if (_hudShowing || _expanded) return; // another pop-up started, or it was opened fully
            _overlayActive = false;
            ApplyNotchVisibility();
        };
        _overlayEndTimer.Start();
    }

    // In a game: a quick strip with the time and battery, then it hides again
    private void ShowGlance()
    {
        string summary = BatterySummary(includePercent: true);
        string text = string.IsNullOrEmpty(summary) ? DateTime.Now.ToString("HH:mm") : $"{DateTime.Now:HH:mm} · {summary}";
        ShowHud(BatteryGlyph(), null, text, "again for more", TimeSpan.FromSeconds(4), overGames: true);
    }

    // ---------- Battery details ----------

    private int _battPercent = -1;
    private bool _battPluggedIn, _battCharging;
    private double? _battWatts;
    private TimeSpan? _battTimeLeft, _battTimeToFull;
    private int _lowWarnedAt = 101;

    private sealed record BatteryReading(double? DischargeW, double? ChargeW, double? RemainingWh, double? FullWh, bool Charging);

    private async Task RefreshBatteryStatsAsync()
    {
        var reading = await Task.Run(ReadBatteryWmi);

        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent > 100)
        {
            _battPercent = -1; // no battery
            UpdateAllyStatsText();
            return;
        }

        _battPercent = s.BatteryLifePercent;
        _battPluggedIn = s.ACLineStatus == 1;
        _battCharging = reading?.Charging ?? (_battPluggedIn && _battPercent < 100);

        _battWatts = null;
        _battTimeLeft = null;
        _battTimeToFull = null;

        if (!_battPluggedIn)
        {
            if (reading?.DischargeW is > 0.5 and var w)
            {
                _battWatts = w;
                if (reading.RemainingWh is > 0 and var wh) _battTimeLeft = TimeSpan.FromHours(wh / w);
            }
            if (_battTimeLeft == null && s.BatteryLifeTime > 0) _battTimeLeft = TimeSpan.FromSeconds(s.BatteryLifeTime);
        }
        else if (_battCharging && reading?.ChargeW is > 0.5 and var cw)
        {
            _battWatts = cw;
            if (reading.RemainingWh is { } rem && reading.FullWh is { } full && full > rem)
                _battTimeToFull = TimeSpan.FromHours((full - rem) / cw);
        }

        RecordPowerSample();
        UpdateAllyStatsText();
    }

    // Windows' battery driver reports power use (mW) and charge left (mWh)
    private static BatteryReading? ReadBatteryWmi()
    {
        try
        {
            double? discharge = null, charge = null, remaining = null, full = null;
            bool charging = false;

            using (var searcher = new ManagementObjectSearcher(@"root\wmi",
                       "SELECT DischargeRate, ChargeRate, RemainingCapacity, Charging FROM BatteryStatus"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject o in results)
                {
                    using (o)
                    {
                        discharge = Math.Abs(Convert.ToDouble(o["DischargeRate"])) / 1000.0;
                        charge = Math.Abs(Convert.ToDouble(o["ChargeRate"])) / 1000.0;
                        remaining = Convert.ToDouble(o["RemainingCapacity"]) / 1000.0;
                        charging = Convert.ToBoolean(o["Charging"]);
                    }
                    break;
                }
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
                using var results = searcher.Get();
                foreach (ManagementObject o in results)
                {
                    using (o) full = Convert.ToDouble(o["FullChargedCapacity"]) / 1000.0;
                    break;
                }
            }
            catch { }

            return new BatteryReading(discharge, charge, remaining, full, charging);
        }
        catch
        {
            return null; // not available: fall back to Windows' own estimate
        }
    }

    private string BatterySummary(bool includePercent)
    {
        if (_battPercent < 0) return "";
        var parts = new List<string>();
        if (includePercent) parts.Add($"{_battPercent}%");

        if (!_battPluggedIn)
        {
            if (_battTimeLeft is { } left) parts.Add($"{FormatDuration(left)} left");
        }
        else if (_battCharging)
        {
            parts.Add(_battTimeToFull is { } toFull ? $"full in {FormatDuration(toFull)}" : "charging");
        }
        else
        {
            parts.Add("plugged in");
        }

        if (_battWatts is { } w) parts.Add($"{w:0.0} W");
        return string.Join(" · ", parts);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)t.TotalMinutes)}m";

    private string BatteryGlyph()
    {
        if (_battPercent < 0) return ""; // clock
        int step = Math.Clamp(_battPercent / 10, 0, 10);
        if (_battPluggedIn)
            return step >= 10 ? "" : step == 9 ? "" : char.ConvertFromUtf32(0xE85A + step);
        return step >= 10 ? "" : char.ConvertFromUtf32(0xE850 + step);
    }

    private void UpdateAllyStatsText()
    {
        if (!_settings.Handheld) return;
        AllyBatteryGlyph.Text = BatteryGlyph();
        AllyStatsText.Text = _battPercent < 0 ? DateTime.Now.ToString("dddd d MMMM") : BatterySummary(includePercent: true);
        if (_battPercent >= 0) BatteryIcon.ToolTip = BatterySummary(includePercent: true);
        UpdateAllyInfoLine();
        if (_expanded) DrawPowerGraph();
    }

    // Warn at 20% and 10% (once each), even over a game
    private void CheckLowBattery(int percent, bool pluggedIn)
    {
        if (pluggedIn)
        {
            _lowWarnedAt = 101;
            return;
        }
        if (!_settings.Handheld) return;

        int threshold = percent <= 10 ? 10 : percent <= 20 ? 20 : 0;
        if (threshold == 0 || _lowWarnedAt <= threshold) return;
        _lowWarnedAt = threshold;

        string left = _battTimeLeft is { } t ? $" · {FormatDuration(t)} left" : "";
        ShowHud(char.ConvertFromUtf32(0xE850 + percent / 10), null,
                threshold == 10 ? "Battery very low" : "Battery low",
                $"{percent}%{left}", TimeSpan.FromSeconds(5), LowRed, overGames: true);
    }

    // ---------- Panel contents ----------

    private bool _syncingSliders;
    private DateTime _volumeTouchedAt, _brightnessTouchedAt;
    private bool _hasBrightness = true;

    private void RefreshAllyPanel()
    {
        UpdateAllyStatsText();
        UpdateRefreshRateButton();
        UpdatePowerModeButton();
        SyncVolumeSlider();
        _ = SyncBrightnessSliderAsync();
        RefreshAllyExtras();
    }

    private void SyncVolumeSlider()
    {
        if (DateTime.Now - _volumeTouchedAt < TimeSpan.FromSeconds(1)) return; // don't fight a drag
        try
        {
            if (_volume == null) RefreshVolumeDevice();
            if (_volume == null) return;
            _volume.GetMasterVolumeLevelScalar(out float level);
            _volume.GetMute(out bool mute);
            _syncingSliders = true;
            VolumeSlider.Value = Math.Round(level * 100);
            VolumeGlyph.Text = mute || level < 0.01 ? "" : "";
        }
        catch { }
        finally { _syncingSliders = false; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSliders || !IsLoaded) return;
        _volumeTouchedAt = DateTime.Now;
        _lastAllyInteraction = DateTime.Now;
        try
        {
            if (_volume == null) RefreshVolumeDevice();
            if (_volume == null) return;
            var context = Guid.Empty;
            float level = (float)(e.NewValue / 100.0);
            _volume.SetMasterVolumeLevelScalar(level, ref context);
            if (level > 0) _volume.SetMute(false, ref context);
            VolumeGlyph.Text = level < 0.01 ? "" : "";
        }
        catch { }
    }

    private async Task SyncBrightnessSliderAsync()
    {
        if (DateTime.Now - _brightnessTouchedAt < TimeSpan.FromSeconds(1)) return;
        int? value = await Task.Run(ReadBrightness);
        _hasBrightness = value != null;
        BrightnessGlyph.Visibility = Vis(_hasBrightness);
        BrightnessSlider.Visibility = Vis(_hasBrightness);
        if (value is int v)
        {
            _syncingSliders = true;
            BrightnessSlider.Value = v;
            _syncingSliders = false;
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSliders || !IsLoaded) return;
        _brightnessTouchedAt = DateTime.Now;
        _lastAllyInteraction = DateTime.Now;
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start(); // set it once you pause, not on every pixel of a drag
    }

    private static int? ReadBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
            }
        }
        catch { }
        return null;
    }

    private static void WriteBrightness(int value)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(value, 0, 100) });
                break;
            }
        }
        catch { }
    }

    // ---------- Refresh rate (120 Hz ↔ 60 Hz) ----------

    private void RefreshRate_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        string? device = CurrentDisplayDevice();
        if (device == null) return;

        var dm = NewDevMode();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return;
        var rates = SupportedRates(device, dm);
        if (rates.Count < 2) return;

        int max = rates.Max();
        int target = dm.dmDisplayFrequency < max
            ? max
            : rates.Contains(60) ? 60 : rates.Where(r => r >= 48).DefaultIfEmpty(rates.Min()).Min();
        if (target == dm.dmDisplayFrequency) return;

        dm.dmDisplayFrequency = target;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        UpdateRefreshRateButton();
    }

    private void UpdateRefreshRateButton()
    {
        string? device = CurrentDisplayDevice();
        var dm = NewDevMode();
        if (device == null || !EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm))
        {
            RefreshRateButton.Visibility = Visibility.Collapsed;
            return;
        }
        var rates = SupportedRates(device, dm);
        RefreshRateButton.Visibility = Vis(rates.Count >= 2);
        RefreshRateButton.Content = $"{dm.dmDisplayFrequency} Hz";
    }

    private string? CurrentDisplayDevice()
    {
        if (_currentMonitor == IntPtr.Zero) return null;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(_currentMonitor, ref info) ? info.szDevice : null;
    }

    // Refresh rates the screen supports at its current resolution
    private static List<int> SupportedRates(string device, DEVMODE current)
    {
        var rates = new SortedSet<int>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight
                && dm.dmBitsPerPel == current.dmBitsPerPel && dm.dmDisplayFrequency > 1)
                rates.Add(dm.dmDisplayFrequency);
        }
        return rates.ToList();
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    // ---------- Windows power mode ----------

    private static readonly (Guid Id, string Name)[] PowerModes =
    {
        (new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), "Efficiency"),
        (Guid.Empty, "Balanced"),
        (new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), "Performance"),
    };

    private void PowerMode_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        try
        {
            int index = CurrentPowerModeIndex();
            var next = PowerModes[(index + 1) % PowerModes.Length];
            PowerSetActiveOverlayScheme(next.Id);
        }
        catch { }
        UpdatePowerModeButton();
    }

    private void UpdatePowerModeButton()
    {
        try
        {
            PowerModeButton.Content = PowerModes[CurrentPowerModeIndex()].Name;
            PowerModeButton.Visibility = Visibility.Visible;
        }
        catch
        {
            PowerModeButton.Visibility = Visibility.Collapsed; // not supported on this PC
        }
    }

    private static int CurrentPowerModeIndex()
    {
        if (PowerGetEffectiveOverlayScheme(out Guid current) != 0) return 1;
        int index = Array.FindIndex(PowerModes, m => m.Id == current);
        return index < 0 ? 1 : index;
    }

    // ---------- On-screen keyboard and Game Bar ----------

    private async void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        CloseFromAlly();
        try
        {
            if (Process.GetProcessesByName("TabTip").Length == 0)
            {
                string tabTip = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    @"microsoft shared\ink\TabTip.exe");
                Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
                await Task.Delay(600);
            }

            var tip = (ITipInvocation)new UIHostNoLaunch();
            tip.Toggle(GetDesktopWindow());
            Marshal.ReleaseComObject(tip);
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true }); } catch { }
        }
    }

    private void GameBar_Click(object sender, RoutedEventArgs e)
    {
        CloseFromAlly();
        KeySender.Send("Win+G");
    }

    // ---------- Win32 ----------

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlayScheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlayScheme);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UIHostNoLaunch { }
}
