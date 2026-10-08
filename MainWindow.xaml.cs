using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Geolocation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinNotch;

public partial class MainWindow : Window
{
    // Sizes at "Normal" notch size. The Size setting scales the whole notch.
    private const double CollapsedH = 34;
    // Width depends on Settings → Theme → Notch width
    private double CollapsedW => _settings.NotchWidth switch { "narrow" => 196, "wide" => 250, _ => 220 };
    private double ExpandedW => _settings.NotchWidth switch { "narrow" => 384, "wide" => 480, _ => 420 };
    private const double WindowW = 640, WindowH = 680; // wider than the notch: room for the bubbles

    private readonly AppSettings _settings = AppSettings.Load();
    private SettingsWindow? _settingsWindow;
    private IntPtr _hwnd;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    private bool _expanded;
    private bool _hovering;
    private string _lastTrack = "";
    private bool _hasArt;

    // Which sections of the expanded notch have something to show
    private bool _mediaWanted, _actionsWanted;

    // Something is playing AND the now playing section is switched on in Settings
    private bool MediaShown => _mediaWanted && _settings.ShowNowPlaying;
    private string? _openTool; // "timer", "clipboard", "claude" or null

    // Weather
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private (double Lat, double Lon)? _location;

    // Battery
    private bool? _wasPluggedIn;

    public MainWindow()
    {
        InitializeComponent();

        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_hovering && !_dragOver && !KeepOpen && !TextEntryActive) SetExpanded(false);
        };

        InitShelf();
        InitProgress();
        InitActions();
        InitTimer();
        InitHud();
        _mediaWanted = false;
        RefreshSections();

        UpdateClock();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        UpdateBattery();
        _batteryTimer.Tick += (_, _) => UpdateBattery();
        _batteryTimer.Start();
        // Update instantly when the charger is plugged in or removed
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.StatusChange)
                Dispatcher.InvokeAsync(UpdateBattery);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            MakeToolWindow();
            InitClipboard();
            InitTray();
        };

        // The notch never takes focus (so it doesn't steal it from your other apps),
        // but a right-click menu only closes on an outside click if its window is active.
        // So briefly bring the notch to the front whenever a right-click menu opens.
        ContextMenuOpening += (_, _) => SetForegroundWindow(_hwnd);

        Loaded += async (_, _) =>
        {
            ApplySettings();
            InitScreens();
            InitPrivacy();
            StartBrightnessWatcher();
            _weatherTimer.Tick += async (_, _) => await UpdateWeatherAsync();
            _weatherTimer.Start();
            _ = UpdateWeatherAsync();
            await InitMediaAsync();
        };

        Closed += (_, _) =>
        {
            StopColorPicker(cancelled: true);
            UnhookMouse();
            StopClipboard();
            DisposeTray();
            StopAllyMode();
            _brightnessWatcher?.Stop();
        };
    }

    // ---------- Settings ----------

    public AppSettings Settings => _settings;

    // Called by the settings window whenever something changes
    public void SettingsChanged()
    {
        _settings.Save();
        ApplySettings();
    }

    private void ApplySettings()
    {
        double scale = _settings.Size switch { "small" => 0.85, "large" => 1.2, _ => 1.0 };
        if (_settings.RogAlly) scale = Math.Max(scale, 1.25); // bigger targets for fingers
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = WindowW * scale;
        Height = WindowH * scale;

        WeatherIcon.Visibility = Vis(_settings.ShowWeather && !string.IsNullOrEmpty(WeatherIcon.Text));
        UpdateBattery();
        UpdateCompactView();
        UpdatePrivacyDots();
        if (!_settings.ClipboardHistory) _clipHistory.Clear();
        RefreshSections();

        StartupRegistration.Apply(_settings.StartWithWindows);
        PositionWindow();
        CheckFullscreen();
        ApplyAllyMode();
        ApplyTheme();
        ApplyExtras();
        BuildBubbles();
        InitUpdates();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ---------- Window behaviour ----------

    // Hide from Alt+Tab and stop the notch stealing keyboard focus when clicked
    private void MakeToolWindow()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;

        int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static void Fade(UIElement element, double to, int ms, int delayMs = 0) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        { BeginTime = TimeSpan.FromMilliseconds(delayMs) });

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm");

    // ---------- Collapsed view (left side) ----------

    // Left of the collapsed notch: a running timer takes priority over album art + sound bars
    private void UpdateCompactView()
    {
        bool timer = TimerActive;
        CompactTimer.Visibility = Vis(timer);
        bool download = DownloadShowing && !timer;
        CompactDownload.Visibility = Vis(download);
        CompactArt.Visibility = Vis(_hasArt && MediaShown && !timer && !download);
        UpdateSoundBars();
    }

    // ---------- Battery ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 1 = plugged in
        public byte BatteryFlag;         // 128 = no battery
        public byte BatteryLifePercent;  // 0-100, 255 = unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static readonly Brush ChargingGreen = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush LowRed = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    // The accent colour (Settings → Theme). Not frozen, so changing its colour updates everything using it.
    private static readonly SolidColorBrush AccentOrange = new(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void UpdateBattery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent > 100)
        {
            BatteryIcon.Visibility = Visibility.Collapsed; // desktop PC or unknown
            return;
        }

        int percent = s.BatteryLifePercent;
        bool pluggedIn = s.ACLineStatus == 1;

        const double innerWidth = 21 - 2 * 1.2 - 2 * 1.5; // body minus border and padding
        BatteryFill.Width = Math.Max(1.5, innerWidth * percent / 100.0);
        BatteryFill.Background = pluggedIn ? ChargingGreen : percent <= 20 ? LowRed : Brushes.White;
        BatteryBolt.Visibility = Vis(pluggedIn);
        BatteryIcon.ToolTip = $"{percent}%{(pluggedIn ? " (charging)" : "")}";
        BatteryIcon.Visibility = Vis(_settings.ShowBattery);

        CheckLowBattery(percent, pluggedIn);
        if (_wasPluggedIn is bool was && was != pluggedIn) OnPowerSourceChanged(pluggedIn);

        // Charging pop-up when the charger goes in
        if (_wasPluggedIn == false && pluggedIn && _settings.ChargingPopup)
            ShowHud("", null, "Charging", $"{percent}%", iconBrush: ChargingGreen);
        _wasPluggedIn = pluggedIn;
    }

    // ---------- Weather (Open-Meteo: free, no API key needed) ----------

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinNotch/1.0");
        return client;
    }

    private async Task UpdateWeatherAsync()
    {
        try
        {
            _location ??= await GetLocationAsync();
            if (_location is not { } loc) return;

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={loc.Lat:F3}&longitude={loc.Lon:F3}&current=weather_code,is_day");

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            int code = current.GetProperty("weather_code").GetInt32();
            bool isDay = current.GetProperty("is_day").GetInt32() == 1;

            WeatherIcon.Text = WeatherGlyph(code, isDay);
            WeatherIcon.Visibility = Vis(_settings.ShowWeather);
        }
        catch
        {
            // Offline or the service is down: keep showing the last icon, try again next time
        }
    }

    // Uses Windows location if it's switched on, otherwise a rough location from your internet connection
    private static async Task<(double Lat, double Lon)?> GetLocationAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() == GeolocationAccessStatus.Allowed)
            {
                var pos = await new Geolocator().GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
                var p = pos.Coordinate.Point.Position;
                return (p.Latitude, p.Longitude);
            }
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
            var r = doc.RootElement;
            if (r.GetProperty("success").GetBoolean())
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
        }
        catch { }

        return null;
    }

    // Maps the standard WMO weather codes to a small icon
    private static string WeatherGlyph(int code, bool isDay) => code switch
    {
        0 => isDay ? "☀" : "☾",                              // clear: sun / moon
        1 or 2 => isDay ? "⛅" : "☁",                         // partly cloudy
        3 => "☁",                                                 // cloudy
        45 or 48 => "\U0001F32B",                                      // fog
        >= 51 and <= 67 or >= 80 and <= 82 => "\U0001F327",            // drizzle / rain / showers
        >= 71 and <= 77 or 85 or 86 => "\U0001F328",                   // snow
        >= 95 => "⛈",                                             // thunderstorm
        _ => "☁"
    };

    // ---------- Expand / collapse ----------

    private void Pill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovering = true;
        if (_alarmTimer != null) StopAlarm(); // you have noticed: stop ringing
        _collapseTimer.Stop();
        SetExpanded(true);
    }

    private void Pill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovering = false;
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(350);
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void SetExpanded(bool expand)
    {
        if (expand && (_picking || NotchHidden)) return;
        if (_expanded == expand) return;

        if (expand) EndHud(immediate: true);
        _expanded = expand;

        if (expand)
        {
            if (_settings.RogAlly) RefreshAllyPanel();
            UpdateStats();
        }
        else
        {
            _touchOpen = false;
            EndNav();
            if (_overlayActive) EndOverlaySoon(); // opened over a game: hide again once closed
            EndTextEntry();
        }

        // Closing the notch also closes any open tool panel
        if (!expand && _openTool != null)
        {
            _openTool = null;
            RefreshSections();
        }

        var springy = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sizeTime = TimeSpan.FromMilliseconds(expand ? 420 : 260);

        Pill.BeginAnimation(WidthProperty,
            new DoubleAnimation(expand ? ExpandedW : CollapsedW, sizeTime) { EasingFunction = expand ? springy : smooth });
        Pill.BeginAnimation(HeightProperty,
            new DoubleAnimation(expand ? ExpandedTargetHeight : CollapsedH, sizeTime) { EasingFunction = expand ? springy : smooth });

        Fade(ExpandedView, expand ? 1 : 0, expand ? 220 : 100, expand ? 140 : 0);
        Fade(TopLeftBar, expand ? 1 : 0, expand ? 220 : 100, expand ? 140 : 0);
        Fade(CompactView, expand ? 0 : 1, expand ? 80 : 200, expand ? 0 : 120);
        Fade(StatusBar, 1, 150);

        ExpandedView.IsHitTestVisible = expand;
        ShowBubbles(expand);
        TopLeftBar.IsHitTestVisible = expand;
        Pill.CornerRadius = expand ? new CornerRadius(0, 0, 26, 26) : new CornerRadius(0, 0, 16, 16);
    }

    // Height the expanded notch needs for whatever is showing
    private double ExpandedTargetHeight
    {
        get
        {
            ExpandedView.Measure(new Size(ExpandedW, double.PositiveInfinity));
            return Math.Max(CollapsedH + 30, ExpandedView.DesiredSize.Height);
        }
    }

    // Smoothly resize the open notch when a section appears or disappears
    private void AnimateExpandedHeight()
    {
        if (!_expanded) return;
        double height = ExpandedTargetHeight;
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        MoveBubbles(height);
    }

    // Decides which sections of the expanded notch are visible.
    // An open tool (timer / clipboard) takes over the whole expanded view.
    private void RefreshSections()
    {
        bool tool = _openTool != null;

        MediaRow.Visibility = Vis(!tool && MediaShown);
        ProgressRow.Visibility = Vis(!tool && MediaShown);
        ActionsPanel.Visibility = Vis(!tool && _actionsWanted);
        ShelfArea.Visibility = Vis(!tool && ShelfVisible);
        TimerPanel.Visibility = Vis(_openTool == "timer");
        ClipboardPanel.Visibility = Vis(_openTool == "clipboard");
        ClaudePanel.Visibility = Vis(_openTool == "claude");
        AllyPanel.Visibility = Vis(!tool && _settings.RogAlly);

        DateText.Text = DateTime.Now.ToString("dddd d MMMM");
        DateText.Visibility = Vis(!tool && !MediaShown && !_actionsWanted && !ShelfVisible && !_settings.RogAlly && !_settings.ShowStats);

        TimerToolButton.Foreground = _openTool == "timer" ? AccentOrange : Brushes.White;
        ClipboardToolButton.Foreground = _openTool == "clipboard" ? AccentOrange : Brushes.White;
        ClaudeToolButton.Foreground = _openTool == "claude" ? AccentOrange : Brushes.White;
        NotesToolButton.Foreground = _openTool == "notes" ? AccentOrange : Brushes.White;
        CalcToolButton.Foreground = _openTool == "calc" ? AccentOrange : Brushes.White;
        NotesPanel.Visibility = Vis(_openTool == "notes");
        CalcPanel.Visibility = Vis(_openTool == "calc");
        StatsRow.Visibility = Vis(!tool && _settings.ShowStats);

        AnimateExpandedHeight();
    }

    private void ToggleTool(string tool)
    {
        _openTool = _openTool == tool ? null : tool;
        RefreshSections();
    }

    // Hide the media player (and its progress bar) when nothing is playing
    private void SetMediaVisible(bool visible)
    {
        if (_mediaWanted == visible) return;
        _mediaWanted = visible;
        RefreshSections();
        UpdateCompactView();
    }

    // Briefly pop open (song change, screenshot added...), like a Dynamic Island alert
    private void Peek(bool force = false)
    {
        if (!force && !_settings.PeekOnTrackChange) return;
        if (_hovering || _dragOver) return;
        SetExpanded(true);
        _collapseTimer.Interval = TimeSpan.FromSeconds(3);
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    // ---------- Media (Spotify, browsers, etc. via Windows' media controls) ----------

    private async Task InitMediaAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(AttachSession);
            AttachSession();
        }
        catch
        {
            TitleText.Text = "Media controls unavailable";
        }
    }

    private void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = RefreshMediaAsync(allowPeek: false);
        UpdatePlaybackState();
        RefreshTimeline(force: true);
        SetMediaVisible(_session != null);
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => Dispatcher.InvokeAsync(() => RefreshTimeline(force: false));

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => Dispatcher.InvokeAsync(() => RefreshMediaAsync(allowPeek: true));

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => Dispatcher.InvokeAsync(UpdatePlaybackState);

    private async Task RefreshMediaAsync(bool allowPeek)
    {
        if (_session == null)
        {
            ShowNothingPlaying();
            return;
        }

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            string title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            string artist = props.Artist ?? "";

            BitmapImage? art = props.Thumbnail != null ? await LoadThumbnailAsync(props.Thumbnail) : null;

            await Dispatcher.InvokeAsync(() =>
            {
                TitleText.Text = title;
                ArtistText.Text = artist;
                SetArtwork(art);

                string trackKey = title + "|" + artist;
                if (allowPeek && trackKey != _lastTrack && _settings.ShowNowPlaying) Peek();
                if (trackKey != _lastTrack) RefreshTimeline(force: true);
                _lastTrack = trackKey;
            });
        }
        catch
        {
            // Some apps briefly report nothing while switching tracks; ignore
        }
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void SetArtwork(BitmapImage? art)
    {
        _hasArt = art != null;
        if (art == null)
        {
            ArtBorder.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            ArtPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            ArtBorder.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            ArtPlaceholder.Visibility = Visibility.Collapsed;
            CompactArt.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }
        UpdateCompactView();
    }

    private void ShowNothingPlaying()
    {
        TitleText.Text = "Nothing playing";
        ArtistText.Text = "";
        SetArtwork(null);
        PlayPauseButton.Content = "";
        _lastTrack = "";
        RefreshTimeline(force: true);
    }

    private void UpdatePlaybackState()
    {
        var status = _session?.GetPlaybackInfo()?.PlaybackStatus;
        bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlayPauseButton.Content = playing ? "" : ""; // pause : play icon
        SetPlayingForTimeline(playing);
        UpdateSoundBars();
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
