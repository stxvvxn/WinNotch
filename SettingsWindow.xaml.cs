using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinNotch;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private bool _loading = true;

    public SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        LoadValues();
        _loading = false;
        Loaded += async (_, _) => await LoadReleaseNotesAsync();
    }

    private void LoadValues()
    {
        var s = _owner.Settings;

        // General
        StartWithWindowsBox.IsChecked = s.StartWithWindows;
        HideInFullscreenBox.IsChecked = s.HideInFullscreen;
        SecondsBox.IsChecked = s.ShowSeconds;
        ShowQuoteBox.IsChecked = s.ShowQuote;
        TidyPageBox.IsChecked = s.TidyPage;
        CleanRecycleBinBox.IsChecked = s.CleanRecycleBin;
        if (!SelectByTag(CleanAgeBox, s.CleanOlderThanHours.ToString())) SelectByTag(CleanAgeBox, "24");
        BuildCleanPathList();
        BuildScriptList();
        WeatherBox.IsChecked = s.WeatherEnabled;
        WeatherPlaceBox.Text = s.WeatherPlace;
        if (s.WeatherPlace.Length > 0) WeatherStatus.Text = $"Showing the weather for {s.WeatherPlace}";
        SelectByTag(WeatherUnitBox, s.WeatherFahrenheit ? "f" : "c");
        CalendarBox.IsChecked = s.CalendarEnabled;
        CalendarUrlsBox.Text = string.Join(Environment.NewLine, s.CalendarUrls);
        CalendarCountdownBox.IsChecked = s.CalendarCountdown;
        DayProgressBox.IsChecked = s.DayProgress;
        WeekNumberBox.IsChecked = s.ShowWeekNumber;
        if (!SelectByTag(DayProgressModeBox, s.DayProgressMode)) DayProgressModeBox.SelectedIndex = 0;
        WorkStartBox.Text = s.WorkStart;
        WorkEndBox.Text = s.WorkEnd;
        QuickTogglesBox.IsChecked = s.QuickToggles;
        ToggleMicBox.IsChecked = s.ToggleMic;
        ToggleDndBox.IsChecked = s.ToggleDnd;
        ToggleThemeBox.IsChecked = s.ToggleTheme;
        ToggleBluetoothBox.IsChecked = s.ToggleBluetooth;
        ToggleAwakeBox.IsChecked = s.ToggleAwake;
        ToggleLockBox.IsChecked = s.ToggleLock;
        if (!SelectByTag(SizeBox, s.Size)) SizeBox.SelectedIndex = 1;
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Main display", Tag = "primary" });
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Follow the mouse", Tag = "mouse" });
        foreach (var (device, label) in _owner.GetMonitorChoices())
            MonitorBox.Items.Add(new ComboBoxItem { Content = label, Tag = device });
        if (!SelectByTag(MonitorBox, s.Monitor)) MonitorBox.SelectedIndex = 0;

        // Theme
        BuildAccentSwatches();
        AccentHexBox.Text = s.AccentColor;
        AccentBarsBox.IsChecked = s.AccentBars;
        if (!SelectByTag(NotchStyleBox, s.NotchStyle)) NotchStyleBox.SelectedIndex = 0;
        if (!SelectByTag(NotchWidthBox, s.NotchWidth)) NotchWidthBox.SelectedIndex = 1;
        ShowAccent();

        // Search
        HotkeyBox.Text = s.SearchHotkey;
        WindowsSearchBox.IsChecked = s.UseWindowsSearch;
        FoldersBox.Text = string.Join(Environment.NewLine, s.SearchFolders);
        foreach (var t in WebTools.AiTools) AiBox.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Key });
        foreach (var t in WebTools.Engines) EngineBox.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Key });
        if (!SelectByTag(AiBox, s.AiTool)) AiBox.SelectedIndex = 0;
        if (!SelectByTag(EngineBox, s.WebEngine)) EngineBox.SelectedIndex = 0;
        CustomAiBox.Text = s.CustomAiUrl;
        CustomWebBox.Text = s.CustomWebUrl;
        ClaudeAppBox.IsChecked = s.ClaudeDesktopApp;

        // System info
        ShowStatsBox.IsChecked = s.ShowSystemInfo;
        CompactStatsBox.IsChecked = s.CompactStats;
        VolumePageBox.IsChecked = s.VolumePage;
        MusicPageBox.IsChecked = s.MusicPage;
        foreach (var m in MainWindow.MusicServices) MusicServiceBox.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Key });
        if (!SelectByTag(MusicServiceBox, s.MusicService)) MusicServiceBox.SelectedIndex = 0;
        MusicExtrasBox.IsChecked = s.MusicExtras;
        BubblesBox.IsChecked = s.ShowBubbles;
        DrawBubblePreview();
        ShelfPageBox.IsChecked = s.ShelfPage;
        ShelfAutoOpenBox.IsChecked = s.ShelfAutoOpen;
        ShelfRememberBox.IsChecked = s.ShelfRemember;
        ShelfThumbsBox.IsChecked = s.ShelfThumbnails;
        ShelfRemoveBox.IsChecked = s.ShelfRemoveAfterDrag;
        ShelfScratchBox.IsChecked = s.ShelfScratch;
        ShelfScreenshotsBox.IsChecked = s.ShelfScreenshots;
        ShelfPeekBox.IsChecked = s.ShelfScreenshotPeek;
        RemindersBox.IsChecked = s.RemindersEnabled;
        ReminderSoundBox.IsChecked = s.ReminderSound;
        ReminderFullscreenBox.IsChecked = s.ReminderOverFullscreen;
        if (!SelectByTag(ReminderCountdownBox, s.ReminderCountdownMinutes.ToString())) SelectByTag(ReminderCountdownBox, "60");
        BuildReminderList();
        FillNewReminderDays();
        CpuBox.IsChecked = s.StatCpu;
        MemoryBox.IsChecked = s.StatMemory;
        GpuBox.IsChecked = s.StatGpu;
        DiskBox.IsChecked = s.StatDisk;
        NetworkBox.IsChecked = s.StatNetwork;
        BatteryBox.IsChecked = s.StatBattery;
        UptimeBox.IsChecked = s.StatUptime;
        BuildDriveChoices();

        VersionText.Text = $"WinNotch · version {Updater.CurrentVersion}";
        FeaturesText.Text = LoadFeatures();
        AutoUpdateBox.IsChecked = s.AutoUpdate;
        UpdateRepoBox.Text = s.UpdateRepo;
        UpdateVersionText.Text = $"You have version {Updater.CurrentVersion}. New versions come from the repo's GitHub releases.";
        // The debug restart only makes sense when running from the code
        HardRestartButton.Visibility = App.FindRunBat() != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var s = _owner.Settings;
        CustomAiRow.Visibility = s.AiTool == "custom" ? Visibility.Visible : Visibility.Collapsed;
        CustomWebRow.Visibility = s.WebEngine == "custom" ? Visibility.Visible : Visibility.Collapsed;
        ClaudeAppBox.Visibility = s.AiTool == "claude" ? Visibility.Visible : Visibility.Collapsed;
        AiHint.Text = s.AiTool switch
        {
            "gemini" => "Gemini can't receive text from a link, so your question is copied - just paste it in (Ctrl+V).",
            "custom" => "Put {query} in the link where your question should go.",
            _ => $"Ctrl+Enter in the search box (or the \"Ask {WebTools.Ai(s).Name}\" row) opens a new chat with your question.",
        };
        foreach (var box in new[] { CpuBox, MemoryBox, GpuBox, DiskBox, NetworkBox, BatteryBox, UptimeBox, CompactStatsBox })
            box.IsEnabled = s.ShowSystemInfo;
        foreach (var box in new[] { ShelfAutoOpenBox, ShelfRememberBox, ShelfThumbsBox, ShelfRemoveBox, ShelfScratchBox, ShelfScreenshotsBox })
            box.IsEnabled = s.ShelfPage;
        ShelfPeekBox.IsEnabled = s.ShelfPage && s.ShelfScreenshots;
        foreach (var box in new Control[] { WeatherPlaceBox, WeatherFindButton, WeatherUnitBox })
            box.IsEnabled = s.WeatherEnabled;
        foreach (var box in new Control[] { CalendarUrlsBox, CalendarCountdownBox })
            box.IsEnabled = s.CalendarEnabled;
        foreach (var box in new Control[] { WeekNumberBox, DayProgressModeBox, WorkStartBox, WorkEndBox })
            box.IsEnabled = s.DayProgress;
        WorkStartBox.IsEnabled = WorkEndBox.IsEnabled = s.DayProgress && s.DayProgressMode == "work";
        foreach (var box in new Control[] { ToggleMicBox, ToggleDndBox, ToggleThemeBox, ToggleBluetoothBox, ToggleAwakeBox, ToggleLockBox })
            box.IsEnabled = s.QuickToggles;
        foreach (var box in new Control[] { ReminderSoundBox, ReminderFullscreenBox, ReminderCountdownBox })
            box.IsEnabled = s.RemindersEnabled;
        MusicServiceHint.Text = s.MusicService switch
        {
            "spotify" => "Spotify gets Like (adds to / removes from Liked Songs) and Smart Shuffle (steps through Shuffle → Smart Shuffle → off, Premium only). These use Spotify's own shortcuts, so the Spotify window flashes to the front for a moment - it needs to be open, not only in the tray.",
            "auto" => "Shows whatever's playing. If Spotify is playing, its Like and Smart Shuffle buttons appear too.",
            _ => "This service gets the standard controls plus a button to open it. Windows doesn't offer a way to like songs in it from outside the app.",
        } + " When more than one app is playing, the notch shows your chosen service first.";
    }

    private static bool SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem c && string.Equals(c.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = c;
                return true;
            }
        }
        return false;
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    // Every change applies straight away
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _owner.Settings;

        s.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        s.HideInFullscreen = HideInFullscreenBox.IsChecked == true;
        s.ShowSeconds = SecondsBox.IsChecked == true;
        s.ShowQuote = ShowQuoteBox.IsChecked == true;
        s.TidyPage = TidyPageBox.IsChecked == true;
        s.CleanRecycleBin = CleanRecycleBinBox.IsChecked == true;
        s.CleanOlderThanHours = int.TryParse(SelectedTag(CleanAgeBox), out int age) ? age : 24;
        s.AutoUpdate = AutoUpdateBox.IsChecked == true;
        string repo = UpdateRepoBox.Text.Trim().Replace("https://github.com/", "").Trim('/');
        if (System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) s.UpdateRepo = repo;
        UpdateRepoBox.Text = s.UpdateRepo;
        s.WeatherEnabled = WeatherBox.IsChecked == true;
        bool fahrenheit = SelectedTag(WeatherUnitBox) == "f";
        if (fahrenheit != s.WeatherFahrenheit)
        {
            s.WeatherFahrenheit = fahrenheit;
            _owner.ReloadWeather();
        }
        s.CalendarEnabled = CalendarBox.IsChecked == true;
        s.CalendarUrls = CalendarUrlsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                             .Where(u => u.Length > 0).ToList();
        s.CalendarCountdown = CalendarCountdownBox.IsChecked == true;
        s.DayProgress = DayProgressBox.IsChecked == true;
        s.ShowWeekNumber = WeekNumberBox.IsChecked == true;
        s.DayProgressMode = SelectedTag(DayProgressModeBox) ?? "work";
        if (ReminderParser.TryTime(WorkStartBox.Text, out var ws)) s.WorkStart = $"{(int)ws.TotalHours:00}:{ws.Minutes:00}";
        if (ReminderParser.TryTime(WorkEndBox.Text, out var we)) s.WorkEnd = $"{(int)we.TotalHours:00}:{we.Minutes:00}";
        WorkStartBox.Text = s.WorkStart;
        WorkEndBox.Text = s.WorkEnd;
        s.QuickToggles = QuickTogglesBox.IsChecked == true;
        s.ToggleMic = ToggleMicBox.IsChecked == true;
        s.ToggleDnd = ToggleDndBox.IsChecked == true;
        s.ToggleTheme = ToggleThemeBox.IsChecked == true;
        s.ToggleBluetooth = ToggleBluetoothBox.IsChecked == true;
        s.ToggleAwake = ToggleAwakeBox.IsChecked == true;
        s.ToggleLock = ToggleLockBox.IsChecked == true;
        s.Size = SelectedTag(SizeBox) ?? "normal";
        s.Monitor = SelectedTag(MonitorBox) ?? "primary";

        if (MainWindow.TryParseColour(AccentHexBox.Text, out var accent))
            s.AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        AccentHexBox.Text = s.AccentColor;
        s.AccentBars = AccentBarsBox.IsChecked == true;
        s.NotchStyle = SelectedTag(NotchStyleBox) ?? "black";
        s.NotchWidth = SelectedTag(NotchWidthBox) ?? "normal";
        ShowAccent();

        string key = HotkeyBox.Text.Trim();
        if (key.Length == 0 || Hotkeys.TryParse(key, out _, out _)) s.SearchHotkey = key;
        HotkeyBox.Text = s.SearchHotkey;
        s.UseWindowsSearch = WindowsSearchBox.IsChecked == true;
        s.SearchFolders = FoldersBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Select(f => f.Trim('"')).Where(f => f.Length > 0).ToList();
        s.AiTool = SelectedTag(AiBox) ?? "claude";
        s.WebEngine = SelectedTag(EngineBox) ?? "google";
        s.CustomAiUrl = CustomAiBox.Text.Trim();
        s.CustomWebUrl = CustomWebBox.Text.Trim();
        s.ClaudeDesktopApp = ClaudeAppBox.IsChecked == true;

        s.ShowSystemInfo = ShowStatsBox.IsChecked == true;
        s.CompactStats = CompactStatsBox.IsChecked == true;
        s.VolumePage = VolumePageBox.IsChecked == true;
        s.MusicPage = MusicPageBox.IsChecked == true;
        s.MusicService = SelectedTag(MusicServiceBox) ?? "auto";
        s.MusicExtras = MusicExtrasBox.IsChecked == true;
        s.ShowBubbles = BubblesBox.IsChecked == true;
        s.ShelfPage = ShelfPageBox.IsChecked == true;
        s.ShelfAutoOpen = ShelfAutoOpenBox.IsChecked == true;
        s.ShelfRemember = ShelfRememberBox.IsChecked == true;
        s.ShelfThumbnails = ShelfThumbsBox.IsChecked == true;
        s.ShelfRemoveAfterDrag = ShelfRemoveBox.IsChecked == true;
        s.ShelfScratch = ShelfScratchBox.IsChecked == true;
        s.ShelfScreenshots = ShelfScreenshotsBox.IsChecked == true;
        s.ShelfScreenshotPeek = ShelfPeekBox.IsChecked == true;
        s.RemindersEnabled = RemindersBox.IsChecked == true;
        s.ReminderSound = ReminderSoundBox.IsChecked == true;
        s.ReminderOverFullscreen = ReminderFullscreenBox.IsChecked == true;
        s.ReminderCountdownMinutes = int.TryParse(SelectedTag(ReminderCountdownBox), out int cd) ? cd : 60;
        s.StatCpu = CpuBox.IsChecked == true;
        s.StatMemory = MemoryBox.IsChecked == true;
        s.StatGpu = GpuBox.IsChecked == true;
        s.StatDisk = DiskBox.IsChecked == true;
        s.StatNetwork = NetworkBox.IsChecked == true;
        s.StatBattery = BatteryBox.IsChecked == true;
        s.StatUptime = UptimeBox.IsChecked == true;

        UpdateVisibility();
        _owner.SettingsChanged();
        DrawBubblePreview(); // picks up a new notch style
    }

    // ---------- Theme ----------

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, hex) in MainWindow.AccentChoices)
        {
            MainWindow.TryParseColour(hex, out var colour);
            var swatch = new Button
            {
                ToolTip = name,
                Tag = hex,
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 4),
                Padding = new Thickness(0),
                Content = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(colour) },
            };
            swatch.Click += (_, _) =>
            {
                AccentHexBox.Text = hex;
                Changed(swatch, new RoutedEventArgs());
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    // Outlines the chosen swatch, shows the colour by the hex box, and recolours this window
    private void ShowAccent()
    {
        string current = _owner.Settings.AccentColor;
        if (!MainWindow.TryParseColour(current, out var c)) c = Color.FromRgb(0xFF, 0x9F, 0x0A);
        AccentPreview.Background = new SolidColorBrush(c);
        if (Resources["Accent"] is not SolidColorBrush b || b.Color != c) Resources["Accent"] = new SolidColorBrush(c);
        foreach (var child in AccentSwatches.Children.OfType<Button>())
        {
            if (child.Content is System.Windows.Shapes.Ellipse e)
            {
                bool chosen = string.Equals(child.Tag as string, current, StringComparison.OrdinalIgnoreCase);
                e.Stroke = chosen ? Brushes.White : null;
                e.StrokeThickness = chosen ? 2 : 0;
            }
        }
    }

    // ---------- Disk tile drives ----------

    private void BuildDriveChoices(IEnumerable<string>? extra = null)
    {
        var chosen = _owner.Settings.DiskDrives.Select(Letter).Where(l => l.Length == 1).ToList();
        var letters = new SortedSet<string>(chosen);
        var labels = new Dictionary<string, string>();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string l = Letter(d.Name);
                letters.Add(l);
                string name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel;
                labels[l] = $"{l}:  {name} · {d.TotalSize / 1_073_741_824.0:0} GB";
            }
            catch { }
        }
        foreach (var l in extra ?? Array.Empty<string>()) letters.Add(l);

        DrivesPanel.Children.Clear();
        foreach (var l in letters)
        {
            var box = new CheckBox
            {
                Content = labels.TryGetValue(l, out var text) ? text : $"{l}:  (not connected)",
                Tag = l,
                IsChecked = chosen.Contains(l) || (extra?.Contains(l) ?? false),
                Margin = new Thickness(0, 5, 22, 5),
            };
            box.Click += (_, _) => SaveDrives();
            DrivesPanel.Children.Add(box);
        }
    }

    private static string Letter(string drive) => drive.Trim().TrimEnd(':', '\\').ToUpperInvariant();

    private void SaveDrives()
    {
        _owner.Settings.DiskDrives = DrivesPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        _owner.SettingsChanged();
    }

    private void AddDrive_Click(object sender, RoutedEventArgs e)
    {
        string l = Letter(AddDriveBox.Text);
        AddDriveBox.Text = "";
        if (l.Length != 1 || !char.IsLetter(l[0])) return;
        BuildDriveChoices(new[] { l });
        SaveDrives();
    }

    private void AddDriveBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddDrive_Click(sender, e);
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Changed(sender, e);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Updates and About ----------

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { await _owner.CheckForUpdatesAsync(manual: true); }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    // FEATURES.txt is built into the exe, so the list always matches the version you're running
    private static string LoadFeatures()
    {
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("WinNotch.FEATURES.txt");
            if (stream == null) return "";
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd().TrimEnd();
        }
        catch
        {
            return "";
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/{_owner.Settings.UpdateRepo}") { UseShellExecute = true });
        }
        catch { }
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void ClearShelf_Click(object sender, RoutedEventArgs e) => _owner.ClearShelf();

    private void OpenScratch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            if (!System.IO.File.Exists(MainWindow.ScratchFile)) System.IO.File.WriteAllText(MainWindow.ScratchFile, "");
            WebTools.Open(MainWindow.ScratchFile);
        }
        catch { }
    }

    private void HardRestart_Click(object sender, RoutedEventArgs e)
    {
        _owner.Settings.Save();
        App.HardRestart();
    }

    private void RestartApp_Click(object sender, RoutedEventArgs e)
    {
        _owner.Settings.Save();
        App.Restart();
    }
}
