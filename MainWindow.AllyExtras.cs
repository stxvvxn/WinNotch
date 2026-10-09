using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace WinNotch;

// ROG Ally extras: automatic profiles, per-game settings, session stats, battery health,
// power graph, download speed, stay awake, screen off, Wi-Fi / Bluetooth / airplane and resolution.
public partial class MainWindow
{
    private static readonly Brush OffGrey = Frozen(Color.FromRgb(0x77, 0x77, 0x77));

    // Called once a second while Ally mode is on
    private void AllyEverySecond()
    {
        SampleNetwork();
        CheckKeepAwakeAutoOff();
        CheckGame();

        CompactDownloadText.Text = FormatRate(_downloadRate);
        if (DownloadShowing != _wasDownloadShowing)
        {
            _wasDownloadShowing = DownloadShowing;
            UpdateCompactView();
            UpdateAllyInfoLine();
        }
    }

    private void RefreshAllyExtras()
    {
        UpdateGameButton();
        UpdateKeepAwakeButton();
        UpdateResolutionButton();
        _ = UpdateRadioButtonsAsync();
        UpdateAllyInfoLine();
        DrawPowerGraph();
    }

    // Second line of the Ally panel: health, download, stay awake, current game
    private void UpdateAllyInfoLine()
    {
        var parts = new List<string>();
        if (_battHealth is int h) parts.Add($"Battery health {h}%");
        if (DownloadShowing) parts.Add($"↓ {FormatRate(_downloadRate)}");
        if (_keepAwake) parts.Add("Staying awake");
        if (_game != null) parts.Add(_game.Name);
        AllyInfoText.Text = string.Join(" · ", parts);
        AllyInfoText.Visibility = Vis(parts.Count > 0);
    }

    // ---------- Refresh rate / power mode helpers ----------

    private bool TryGetDisplay(out string device, out DEVMODE current, out List<int> rates)
    {
        device = CurrentDisplayDevice() ?? "";
        current = NewDevMode();
        rates = new List<int>();
        if (device == "" || !EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref current)) return false;
        rates = SupportedRates(device, current);
        return rates.Count > 0;
    }

    private static int LowRate(List<int> rates) =>
        rates.Contains(60) ? 60 : rates.Where(r => r >= 48).DefaultIfEmpty(rates.Min()).Min();

    private void SetRefreshRate(int hz)
    {
        if (!TryGetDisplay(out string device, out DEVMODE dm, out List<int> rates)) return;
        int target = rates.OrderBy(r => Math.Abs(r - hz)).First();
        if (target == dm.dmDisplayFrequency) return;
        dm.dmDisplayFrequency = target;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
    }

    private int CurrentRefreshRate() =>
        TryGetDisplay(out _, out DEVMODE dm, out _) ? dm.dmDisplayFrequency : 0;

    private static void SetPowerModeIndex(int index)
    {
        try { PowerSetActiveOverlayScheme(PowerModes[Math.Clamp(index, 0, PowerModes.Length - 1)].Id); }
        catch { }
    }

    private void RefreshSwitchButtons()
    {
        if (!_expanded) return;
        UpdateRefreshRateButton();
        UpdatePowerModeButton();
        UpdateResolutionButton();
    }

    // ---------- Automatic profile when plugging in / unplugging ----------

    private DispatcherTimer? _profileHudTimer;

    private void OnPowerSourceChanged(bool pluggedIn)
    {
        if (!_settings.Handheld || !_settings.AutoProfile) return;
        if (_game?.ProfileApplied == true) return; // the game's own settings win while it runs
        ApplyPowerSourceProfile(pluggedIn, announce: true);
    }

    private void ApplyPowerSourceProfile(bool pluggedIn, bool announce)
    {
        if (!TryGetDisplay(out _, out _, out List<int> rates)) return;
        int hz = pluggedIn ? rates.Max() : LowRate(rates);
        int mode = pluggedIn ? 2 : 0;
        SetRefreshRate(hz);
        SetPowerModeIndex(mode);
        RefreshSwitchButtons();

        if (!announce) return;
        // Let the "Charging" pop-up show first, then say what changed
        _profileHudTimer?.Stop();
        _profileHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pluggedIn ? 1.8 : 0.1) };
        _profileHudTimer.Tick += (_, _) =>
        {
            _profileHudTimer?.Stop();
            ShowHud(pluggedIn ? "" : BatteryGlyph(), null, pluggedIn ? "Plugged in" : "On battery",
                    $"{hz} Hz · {PowerModes[mode].Name}", TimeSpan.FromSeconds(3),
                    pluggedIn ? ChargingGreen : null, overGames: true);
        };
        _profileHudTimer.Start();
    }

    // ---------- Games: per-game settings and session stats ----------

    private sealed class GameSession
    {
        public int Pid;
        public string Key = "", Name = "";
        public DateTime Started = DateTime.Now;
        public int StartPercent;
        public bool Charged;
        public readonly List<double> Watts = new();
        public (int Hz, int Mode)? Before;
        public bool ProfileApplied;
    }

    private GameSession? _game;

    // Fullscreen apps that aren't games
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "vlc", "mpc-hc64", "mpc-be64",
        "PotPlayerMini64", "Video.UI", "Microsoft.Media.Player", "POWERPNT", "steam", "steamwebhelper",
        "ApplicationFrameHost", "WinNotch", "Discord", "Teams", "ms-teams", "Zoom",
    };

    private void CheckGame()
    {
        if (_game != null && !ProcessAlive(_game.Pid)) EndGame();

        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !ForegroundIsFullscreen()) return;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId || _game?.Pid == (int)pid) return;

        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { return; }
        if (NotGames.Contains(name)) return;

        if (_game != null) EndGame();
        StartGame((int)pid, name, WindowTitle(fg));
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            Process.GetProcessById(pid); // throws once it has exited (works even for anti-cheat games)
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void StartGame(int pid, string processName, string title)
    {
        string display = string.IsNullOrWhiteSpace(title) ? processName : title.Replace("™", "").Replace("®", "").Trim();
        if (display.Length > 32) display = display[..32].TrimEnd() + "…";

        _game = new GameSession
        {
            Pid = pid,
            Key = processName.ToLowerInvariant(),
            Name = display,
            StartPercent = _battPercent,
            Charged = _battPluggedIn,
        };

        if (_settings.Handheld && _settings.GameProfiles.TryGetValue(_game.Key, out var profile))
        {
            _game.Before = (CurrentRefreshRate(), CurrentPowerModeIndex());
            SetRefreshRate(profile.RefreshRate);
            SetPowerModeIndex(profile.PowerMode);
            _game.ProfileApplied = true;
            ShowHud("", null, $"{display} settings",
                    $"{profile.RefreshRate} Hz · {PowerModes[Math.Clamp(profile.PowerMode, 0, 2)].Name}",
                    TimeSpan.FromSeconds(3), overGames: true);
        }

        UpdateGameButton();
        UpdateAllyInfoLine();
    }

    private void EndGame()
    {
        var game = _game;
        _game = null;
        if (game == null) return;

        // Put things back the way they were
        if (game.ProfileApplied)
        {
            if (_settings.AutoProfile) ApplyPowerSourceProfile(_battPluggedIn, announce: false);
            else if (game.Before is { } before)
            {
                if (before.Hz > 0) SetRefreshRate(before.Hz);
                SetPowerModeIndex(before.Mode);
            }
        }

        UpdateGameButton();
        UpdateAllyInfoLine();

        // Session summary (skip quick launches)
        var length = DateTime.Now - game.Started;
        if (!_settings.Handheld || length < TimeSpan.FromMinutes(2)) return;

        var parts = new List<string> { FormatDuration(length) };
        if (!game.Charged && game.StartPercent >= 0 && _battPercent >= 0 && game.StartPercent > _battPercent)
            parts.Add($"used {game.StartPercent - _battPercent}%");
        if (game.Watts.Count > 0) parts.Add($"avg {game.Watts.Average():0.0} W");

        ShowHud("", null, game.Name, string.Join(" · ", parts), TimeSpan.FromSeconds(6));
    }

    private void GameProfile_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        var game = _game;
        if (game == null) return;

        if (_settings.GameProfiles.Remove(game.Key))
        {
            ShowTopMessage($"Forgot settings for {game.Name}", OffGrey, 3);
        }
        else
        {
            int hz = CurrentRefreshRate();
            int mode = CurrentPowerModeIndex();
            _settings.GameProfiles[game.Key] = new GameProfile { Name = game.Name, RefreshRate = hz, PowerMode = mode };
            game.ProfileApplied = true; // put things back when it closes
            ShowTopMessage($"{game.Name}: {hz} Hz · {PowerModes[mode].Name}", ChargingGreen, 3);
        }

        _settings.Save();
        UpdateGameButton();
    }

    private void UpdateGameButton()
    {
        var game = _game;
        GameProfileButton.Visibility = Vis(game != null);
        if (game == null) return;
        bool saved = _settings.GameProfiles.ContainsKey(game.Key);
        GameProfileButton.Content = saved ? "Forget game" : "Save for game";
        GameProfileButton.ToolTip = saved
            ? $"Stop using saved refresh rate and power mode for {game.Name}"
            : $"Always use the current refresh rate and power mode for {game.Name}";
    }

    // ---------- Battery health and power graph ----------

    private int? _battHealth;
    private readonly List<double> _powerSamples = new(); // one every 5 seconds, 30 minutes

    private async Task RefreshBatteryHealthAsync()
    {
        _battHealth = await Task.Run(ReadBatteryHealth);
        UpdateAllyInfoLine();
    }

    // How much charge the battery holds now compared with when it was new
    private static int? ReadBatteryHealth()
    {
        try
        {
            double design = 0, full = 0;
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"))
            using (var results = s.Get())
                foreach (ManagementObject o in results) { using (o) design = Convert.ToDouble(o["DesignedCapacity"]); break; }

            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            using (var results = s.Get())
                foreach (ManagementObject o in results) { using (o) full = Convert.ToDouble(o["FullChargedCapacity"]); break; }

            if (design <= 0 || full <= 0) return null;
            return (int)Math.Round(Math.Min(100, full * 100 / design));
        }
        catch
        {
            return null;
        }
    }

    private void RecordPowerSample()
    {
        if (_battPercent < 0) return;
        double watts = !_battPluggedIn && _battWatts is double w ? w : 0;
        _powerSamples.Add(watts);
        if (_powerSamples.Count > 360) _powerSamples.RemoveAt(0);

        if (_game != null)
        {
            if (_battPluggedIn) _game.Charged = true;
            else if (watts > 0) _game.Watts.Add(watts);
        }
    }

    private void DrawPowerGraph()
    {
        double width = PowerGraph.ActualWidth, height = PowerGraph.Height;
        if (_battPercent < 0)
        {
            PowerLine.Points = new PointCollection();
            PowerGraphLabel.Text = "No battery";
            return;
        }
        if (width <= 0 || _powerSamples.Count < 2)
        {
            PowerLine.Points = new PointCollection();
            PowerGraphLabel.Text = "Power use · collecting…";
            return;
        }

        double peak = _powerSamples.Max();
        double scale = Math.Max(10, peak * 1.15);
        var points = new PointCollection(_powerSamples.Count);
        for (int i = 0; i < _powerSamples.Count; i++)
        {
            double x = i * width / (_powerSamples.Count - 1);
            double y = height - 3 - _powerSamples[i] / scale * (height - 6);
            points.Add(new Point(x, y));
        }
        PowerLine.Points = points;

        int minutes = Math.Max(1, _powerSamples.Count * 5 / 60);
        PowerGraphLabel.Text = peak > 0 ? $"Power · last {minutes} min · peak {peak:0} W" : "Power · plugged in";
    }

    // ---------- Download speed ----------

    private long _lastRxBytes = -1;
    private DateTime _lastRxAt;
    private double _downloadRate; // bytes per second
    private bool _wasDownloadShowing;

    private bool DownloadShowing => _settings.Handheld && _downloadRate > 300_000;

    private void SampleNetwork()
    {
        long total = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                total += ni.GetIPStatistics().BytesReceived;
            }
        }
        catch
        {
            return;
        }

        var now = DateTime.Now;
        if (_lastRxBytes >= 0)
        {
            double seconds = (now - _lastRxAt).TotalSeconds;
            if (seconds > 0.2)
            {
                double rate = Math.Max(0, (total - _lastRxBytes) / seconds);
                _downloadRate = _downloadRate * 0.5 + rate * 0.5; // smooth out spikes
            }
        }
        _lastRxBytes = total;
        _lastRxAt = now;
    }

    private static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond >= 1_000_000 ? $"{bytesPerSecond / 1_000_000:0.0} MB/s" : $"{bytesPerSecond / 1000:0} KB/s";

    // ---------- Stay awake and screen off ----------

    private bool _keepAwake, _sawDownload;
    private DateTime? _quietSince;

    private void KeepAwake_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        SetKeepAwake(!_keepAwake, announce: true);
    }

    private void SetKeepAwake(bool on, bool announce)
    {
        _keepAwake = on;
        _sawDownload = false;
        _quietSince = null;
        ApplyExecutionState();
        UpdateKeepAwakeButton();
        UpdateAllyInfoLine();
        if (announce)
            ShowTopMessage(on ? "Won't sleep (stops when a download finishes)" : "Sleep allowed again",
                           on ? AccentOrange : OffGrey, 3);
    }

    // Once a download has started, let the Ally sleep again after 3 quiet minutes
    private void CheckKeepAwakeAutoOff()
    {
        if (!_keepAwake) return;
        if (_downloadRate > 500_000)
        {
            _sawDownload = true;
            _quietSince = null;
            return;
        }
        if (!_sawDownload) return;

        _quietSince ??= DateTime.Now;
        if (DateTime.Now - _quietSince > TimeSpan.FromMinutes(3))
        {
            SetKeepAwake(false, announce: false);
            ShowHud("", null, "Download finished", "sleep allowed", TimeSpan.FromSeconds(5), ChargingGreen);
        }
    }

    private void UpdateKeepAwakeButton()
    {
        KeepAwakeButton.Foreground = _keepAwake ? AccentOrange : Brushes.White;
        KeepAwakeButton.Content = _keepAwake ? "Awake ✓" : "Stay awake";
    }

    private async void ScreenOff_Click(object sender, RoutedEventArgs e)
    {
        CloseFromAlly();
        if (!_keepAwake) SetKeepAwake(true, announce: false); // keep downloads going while the screen is off
        await Task.Delay(700); // so letting go of the button doesn't wake it straight back up
        const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
        SendMessage(_hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
    }

    // ---------- Wi-Fi, Bluetooth and airplane mode ----------

    private HashSet<string> _radiosBeforeAirplane = new();

    private static async Task<IReadOnlyList<Radio>> RadiosAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return Array.Empty<Radio>();
            return await Radio.GetRadiosAsync();
        }
        catch
        {
            return Array.Empty<Radio>();
        }
    }

    private static string RadioKey(Radio r) => $"{r.Kind}|{r.Name}";

    private async void Wifi_Click(object sender, RoutedEventArgs e) => await ToggleRadioKindAsync(RadioKind.WiFi);
    private async void Bluetooth_Click(object sender, RoutedEventArgs e) => await ToggleRadioKindAsync(RadioKind.Bluetooth);

    private async Task ToggleRadioKindAsync(RadioKind kind)
    {
        _lastAllyInteraction = DateTime.Now;
        var radios = (await RadiosAsync()).Where(r => r.Kind == kind).ToList();
        if (radios.Count == 0) return;
        bool anyOn = radios.Any(r => r.State == RadioState.On);
        foreach (var r in radios)
        {
            try { await r.SetStateAsync(anyOn ? RadioState.Off : RadioState.On); } catch { }
        }
        await UpdateRadioButtonsAsync();
    }

    // Airplane mode: everything wireless off; pressing again turns back on what was on before
    private async void Airplane_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        var radios = await RadiosAsync();
        if (radios.Count == 0) return;

        bool allOff = radios.All(r => r.State != RadioState.On);
        if (!allOff)
        {
            _radiosBeforeAirplane = radios.Where(r => r.State == RadioState.On).Select(RadioKey).ToHashSet();
            foreach (var r in radios.Where(r => r.State == RadioState.On))
            {
                try { await r.SetStateAsync(RadioState.Off); } catch { }
            }
        }
        else
        {
            foreach (var r in radios)
            {
                bool restore = _radiosBeforeAirplane.Count == 0
                    ? r.Kind is RadioKind.WiFi or RadioKind.Bluetooth
                    : _radiosBeforeAirplane.Contains(RadioKey(r));
                if (!restore) continue;
                try { await r.SetStateAsync(RadioState.On); } catch { }
            }
        }
        await UpdateRadioButtonsAsync();
    }

    private async Task UpdateRadioButtonsAsync()
    {
        var radios = await RadiosAsync();
        var wifi = radios.Where(r => r.Kind == RadioKind.WiFi).ToList();
        var bt = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();

        WifiButton.Visibility = Vis(wifi.Count > 0);
        bool wifiOn = wifi.Any(r => r.State == RadioState.On);
        WifiButton.Content = wifiOn ? "Wi-Fi" : "Wi-Fi off";
        WifiButton.Foreground = wifiOn ? Brushes.White : OffGrey;

        BluetoothButton.Visibility = Vis(bt.Count > 0);
        bool btOn = bt.Any(r => r.State == RadioState.On);
        BluetoothButton.Content = btOn ? "Bluetooth" : "BT off";
        BluetoothButton.Foreground = btOn ? Brushes.White : OffGrey;

        AirplaneButton.Visibility = Vis(radios.Count > 0);
        bool airplane = radios.Count > 0 && radios.All(r => r.State != RadioState.On);
        AirplaneButton.Foreground = airplane ? AccentOrange : Brushes.White;
        AirplaneButton.Content = airplane ? "Airplane ✓" : "Airplane";
    }

    // ---------- Desktop resolution (1080p ↔ 720p) ----------

    private const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;

    private static List<(int W, int H)> DisplaySizes(string device, DEVMODE current)
    {
        var sizes = new HashSet<(int, int)>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            if (dm.dmBitsPerPel == current.dmBitsPerPel) sizes.Add((dm.dmPelsWidth, dm.dmPelsHeight));
        return sizes.ToList();
    }

    private static (int W, int H) LowSize(List<(int W, int H)> sizes, (int W, int H) native)
    {
        if (sizes.Contains((1280, 720))) return (1280, 720);
        double aspect = (double)native.W / native.H;
        int wantH = native.H * 2 / 3;
        return sizes.Where(s => Math.Abs((double)s.W / s.H - aspect) < 0.02 && s.H < native.H)
                    .OrderBy(s => Math.Abs(s.H - wantH))
                    .DefaultIfEmpty(native)
                    .First();
    }

    private void Resolution_Click(object sender, RoutedEventArgs e)
    {
        _lastAllyInteraction = DateTime.Now;
        if (!TryGetDisplay(out string device, out DEVMODE dm, out _)) return;
        var sizes = DisplaySizes(device, dm);
        if (sizes.Count < 2) return;

        var native = sizes.OrderByDescending(s => s.W * s.H).First();
        var low = LowSize(sizes, native);
        var target = (dm.dmPelsWidth, dm.dmPelsHeight) == native ? low : native;
        if (target == (dm.dmPelsWidth, dm.dmPelsHeight)) return;

        dm.dmPelsWidth = target.W;
        dm.dmPelsHeight = target.H;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        UpdateResolutionButton();
    }

    private void UpdateResolutionButton()
    {
        if (!TryGetDisplay(out string device, out DEVMODE dm, out _))
        {
            ResolutionButton.Visibility = Visibility.Collapsed;
            return;
        }
        ResolutionButton.Visibility = Vis(DisplaySizes(device, dm).Count >= 2);
        ResolutionButton.Content = $"{dm.dmPelsHeight}p";
    }

    // ---------- Win32 ----------

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
