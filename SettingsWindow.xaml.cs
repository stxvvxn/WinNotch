using System.Windows;
using System.Windows.Controls;
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
        MaxHeight = SystemParameters.WorkArea.Height - 40; // fits small screens like the Ally's
        LoadValues();
        _loading = false;
    }

    private void LoadValues()
    {
        var s = _owner.Settings;

        RogAllyBox.IsChecked = s.RogAlly;
        AutoProfileBox.IsChecked = s.AutoProfile;
        StartWithWindowsBox.IsChecked = s.StartWithWindows;
        HideInFullscreenBox.IsChecked = s.HideInFullscreen;
        SelectByTag(SizeBox, s.Size);

        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Main display", Tag = "primary" });
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Follow the mouse", Tag = "mouse" });
        foreach (var (device, label) in _owner.GetMonitorChoices())
            MonitorBox.Items.Add(new ComboBoxItem { Content = label, Tag = device });
        if (!SelectByTag(MonitorBox, s.Monitor)) MonitorBox.SelectedIndex = 0;

        NowPlayingBox.IsChecked = s.ShowNowPlaying;
        WeatherBox.IsChecked = s.ShowWeather;
        BatteryBox.IsChecked = s.ShowBattery;
        SoundBarsBox.IsChecked = s.ShowSoundBars;
        PeekBox.IsChecked = s.PeekOnTrackChange;

        VolumeBox.IsChecked = s.VolumePopup;
        BrightnessBox.IsChecked = s.BrightnessPopup;
        ChargingBox.IsChecked = s.ChargingPopup;
        PrivacyBox.IsChecked = s.PrivacyDots;

        ClipboardBox.IsChecked = s.ClipboardHistory;
        TimerSoundBox.IsChecked = s.TimerSound;
        if (!SelectByTag(ScreenshotBox, s.ScreenshotMethod)) ScreenshotBox.SelectedIndex = 0;

        BubblesBox.IsChecked = s.ShowBubbles;
        DrawBubblePreview();

        // Updates
        AutoUpdateBox.IsChecked = s.AutoUpdate;
        UpdateRepoBox.Text = s.UpdateRepo;
        VersionText.Text = $"You have version {Updater.CurrentVersion}. New versions come from the repo's GitHub releases.";

        // Theme
        BuildAccentSwatches();
        AccentHexBox.Text = s.AccentColor;
        AccentBarsBox.IsChecked = s.AccentBars;
        if (!SelectByTag(NotchStyleBox, s.NotchStyle)) NotchStyleBox.SelectedIndex = 0;
        if (!SelectByTag(NotchWidthBox, s.NotchWidth)) NotchWidthBox.SelectedIndex = 1;
        ShowAccent();

        // System stats
        StatsBox.IsChecked = s.ShowStats;
        StatsCpuBox.IsChecked = s.StatsCpu;
        StatsRamBox.IsChecked = s.StatsRam;
        StatsNetBox.IsChecked = s.StatsNet;

        // Mic
        MicButtonBox.IsChecked = s.MicButton;
        MicHotkeyBox.Text = s.MicHotkey;
        MicDotBox.IsChecked = s.MicShowDot;
        MicPopupBox.IsChecked = s.MicPopup;

        // Notes and calculator
        NotesEnabledBox.IsChecked = s.NotesEnabled;
        if (!SelectByTag(NotesSizeBox, s.NotesTextSize)) NotesSizeBox.SelectedIndex = 1;
        CalcEnabledBox.IsChecked = s.CalcEnabled;
        CalcCurrencyBox.IsChecked = s.CalcCurrency;
        CalcCopyBox.IsChecked = s.CalcCopyOnEnter;
        if (!SelectByTag(CalcDecimalsBox, s.CalcDecimals.ToString())) CalcDecimalsBox.SelectedIndex = 2;

        // Image actions
        ImageActionsBox.IsChecked = s.ImageActions;
        ResizeWidthBox.Text = s.ImageResizeWidth.ToString();
        JpegQualitySlider.Value = Math.Clamp(s.JpegQuality, 10, 100);
        JpegQualityText.Text = $"{s.JpegQuality}%";
        if (!SelectByTag(ImageOutputBox, s.ImageOutput)) ImageOutputBox.SelectedIndex = 0;
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

        s.RogAlly = RogAllyBox.IsChecked == true;
        s.AutoProfile = AutoProfileBox.IsChecked == true;
        s.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        s.HideInFullscreen = HideInFullscreenBox.IsChecked == true;
        s.Size = SelectedTag(SizeBox) ?? "normal";
        s.Monitor = SelectedTag(MonitorBox) ?? "primary";

        s.ShowNowPlaying = NowPlayingBox.IsChecked == true;
        s.ShowWeather = WeatherBox.IsChecked == true;
        s.ShowBattery = BatteryBox.IsChecked == true;
        s.ShowSoundBars = SoundBarsBox.IsChecked == true;
        s.PeekOnTrackChange = PeekBox.IsChecked == true;

        s.VolumePopup = VolumeBox.IsChecked == true;
        s.BrightnessPopup = BrightnessBox.IsChecked == true;
        s.ChargingPopup = ChargingBox.IsChecked == true;
        s.PrivacyDots = PrivacyBox.IsChecked == true;

        s.ClipboardHistory = ClipboardBox.IsChecked == true;
        s.TimerSound = TimerSoundBox.IsChecked == true;
        s.ScreenshotMethod = SelectedTag(ScreenshotBox) ?? "snipping";
        s.ShowBubbles = BubblesBox.IsChecked == true;

        // Updates
        s.AutoUpdate = AutoUpdateBox.IsChecked == true;
        string repo = UpdateRepoBox.Text.Trim().Replace("https://github.com/", "").Trim('/');
        if (System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) s.UpdateRepo = repo;
        UpdateRepoBox.Text = s.UpdateRepo;

        // Theme
        if (MainWindow.TryParseColour(AccentHexBox.Text, out var accent))
            s.AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        AccentHexBox.Text = s.AccentColor;
        s.AccentBars = AccentBarsBox.IsChecked == true;
        s.NotchStyle = SelectedTag(NotchStyleBox) ?? "black";
        s.NotchWidth = SelectedTag(NotchWidthBox) ?? "normal";
        ShowAccent();

        // System stats
        s.ShowStats = StatsBox.IsChecked == true;
        s.StatsCpu = StatsCpuBox.IsChecked == true;
        s.StatsRam = StatsRamBox.IsChecked == true;
        s.StatsNet = StatsNetBox.IsChecked == true;

        // Mic
        s.MicButton = MicButtonBox.IsChecked == true;
        string hotkey = MicHotkeyBox.Text.Trim();
        if (hotkey.Length == 0 || KeySender.TryParseHotkey(hotkey, out _, out _)) s.MicHotkey = hotkey;
        MicHotkeyBox.Text = s.MicHotkey;
        s.MicShowDot = MicDotBox.IsChecked == true;
        s.MicPopup = MicPopupBox.IsChecked == true;

        // Notes and calculator
        s.NotesEnabled = NotesEnabledBox.IsChecked == true;
        s.NotesTextSize = SelectedTag(NotesSizeBox) ?? "normal";
        s.CalcEnabled = CalcEnabledBox.IsChecked == true;
        s.CalcCurrency = CalcCurrencyBox.IsChecked == true;
        s.CalcCopyOnEnter = CalcCopyBox.IsChecked == true;
        if (int.TryParse(SelectedTag(CalcDecimalsBox), out int decimals)) s.CalcDecimals = decimals;

        // Image actions
        s.ImageActions = ImageActionsBox.IsChecked == true;
        if (int.TryParse(ResizeWidthBox.Text.Trim().TrimEnd('p', 'x', 'P', 'X'), out int width) && width is >= 16 and <= 20000)
            s.ImageResizeWidth = width;
        ResizeWidthBox.Text = s.ImageResizeWidth.ToString();
        s.JpegQuality = (int)JpegQualitySlider.Value;
        s.ImageOutput = SelectedTag(ImageOutputBox) ?? "beside";

        _owner.SettingsChanged();
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

    // Outlines the chosen swatch and shows the colour beside the hex box
    private void ShowAccent()
    {
        string current = _owner.Settings.AccentColor;
        if (MainWindow.TryParseColour(current, out var c)) AccentPreview.Background = new SolidColorBrush(c);
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

    // Enter in a text field applies it straight away
    private void Field_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Changed(sender, e);
    }

    private void JpegQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JpegQualityText != null) JpegQualityText.Text = $"{(int)e.NewValue}%";
        Changed(sender, e);
    }

    private void OpenNotes_Click(object sender, RoutedEventArgs e)
    {
        string file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "notes.txt");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            if (!System.IO.File.Exists(file)) System.IO.File.WriteAllText(file, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch { }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { await _owner.CheckForUpdatesAsync(manual: true); }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private void EditActions_Click(object sender, RoutedEventArgs e) => _owner.OpenActionsFile();

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // Shuts down WinNotch completely (same as Close WinNotch in the tray menu)
    private void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
