using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinNotch;

// Second page of the open notch: scroll down for volume controls (the whole PC, then each app),
// scroll up to go back. The date, time and search box stay where they are.
public partial class MainWindow
{
    private sealed class VolumeRow
    {
        public string Key = "";
        public string Name = "";
        public Func<float> GetLevel = () => 0;
        public Action<float> SetLevel = _ => { };
        public Func<bool> GetMute = () => false;
        public Action<bool> SetMute = _ => { };
        public Slider? Slider;
        public TextBlock? Percent;
        public TextBlock? MuteIcon;
        public bool Updating;
    }

    private int _page;
    private readonly List<VolumeRow> _volumeRows = new();
    private readonly DispatcherTimer _mixerTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _mixerTimerReady;

    // ---------- Switching pages ----------
    // Page ids: 0 = system info (and search results), 3 = music, 1 = volume, 2 = shelf - in that order.
    // Pages switched off in Settings are skipped.

    private List<int> EnabledPages()
    {
        var pages = new List<int> { 0 };
        if (_settings.MusicPage) pages.Add(3);
        if (_settings.VolumePage) pages.Add(1);
        if (_settings.ShelfPage) pages.Add(2);
        return pages;
    }

    // Pages are switched with the round page bubbles under the notch now, so the wheel
    // just scrolls whatever it's over (the scratch pad, a slider...)
    private void Pill_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
    }

    private void ShowPage(int page, bool animate = true)
    {
        var pages = EnabledPages();
        if (!pages.Contains(page)) page = 0;
        bool changed = page != _page;
        bool down = pages.IndexOf(page) > pages.IndexOf(_page);
        _page = page;
        HomePage.Visibility = Vis(page == 0);
        MixerPage.Visibility = Vis(page == 1);
        ShelfPage.Visibility = Vis(page == 2);
        MusicPage.Visibility = Vis(page == 3);
        if (page == 3) RefreshMusic(); else StopMusicTimer();
        BuildPageDots();

        if (page == 1)
        {
            if (!_mixerTimerReady)
            {
                _mixerTimerReady = true;
                _mixerTimer.Tick += (_, _) =>
                {
                    if (_page == 1 && _expanded) RefreshMixer();
                    else _mixerTimer.Stop();
                };
            }
            RefreshMixer();
            _mixerTimer.Start();
        }
        else
        {
            _mixerTimer.Stop();
        }
        if (page == 2) RefreshShelf();

        if (animate && changed)
        {
            // Slide the new page in from the direction you scrolled
            var shown = page switch { 1 => (FrameworkElement)MixerPage, 2 => ShelfPage, 3 => MusicPage, _ => HomePage };
            var move = new TranslateTransform(0, down ? 14 : -14);
            shown.RenderTransform = move;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        AnimateHeight();
    }

    // The page dots have been replaced by page bubbles under the notch: just light up the right one
    private void BuildPageDots()
    {
        PageDots.Visibility = Visibility.Collapsed;
        UpdatePageBubbles();
    }

    // ---------- Volume rows ----------

    private void RefreshMixer()
    {
        var rows = ReadVolumeRows();

        // Same apps as before: just update the numbers (rebuilding would interrupt a drag)
        if (rows.Select(r => r.Key).SequenceEqual(_volumeRows.Select(r => r.Key)))
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var old = _volumeRows[i];
                old.GetLevel = rows[i].GetLevel;
                old.SetLevel = rows[i].SetLevel;
                old.GetMute = rows[i].GetMute;
                old.SetMute = rows[i].SetMute;
                if (old.Slider?.IsMouseCaptureWithin != true) ShowVolume(old);
            }
            return;
        }

        _volumeRows.Clear();
        _volumeRows.AddRange(rows);
        MixerRows.Children.Clear();
        foreach (var row in rows) MixerRows.Children.Add(BuildVolumeRow(row));
        MixerEmpty.Visibility = Vis(rows.Count <= 1);
        AnimateHeight();
    }

    private FrameworkElement BuildVolumeRow(VolumeRow row)
    {
        bool master = row.Key == "master";
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, master ? 8 : 2), Height = 34 };

        // Mute button
        var muteIcon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var mute = new Border
        {
            Width = 32,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Mute / unmute",
            Child = muteIcon,
        };
        mute.MouseEnter += (_, _) => mute.Background = (Brush)FindResource("SurfaceHover");
        mute.MouseLeave += (_, _) => mute.Background = Brushes.Transparent;
        mute.MouseLeftButtonUp += (_, _) =>
        {
            try { row.SetMute(!row.GetMute()); } catch { }
            ShowVolume(row);
        };
        DockPanel.SetDock(mute, Dock.Right);
        dock.Children.Add(mute);

        var percent = new TextBlock
        {
            Width = 44,
            FontSize = 12,
            Foreground = (Brush)FindResource("Secondary"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        DockPanel.SetDock(percent, Dock.Right);
        dock.Children.Add(percent);

        // Icon and name
        FrameworkElement icon = !master && AppIcon(row.Key) is { } img
            ? new Image { Source = img, Width = 20, Height = 20 }
            : new TextBlock
            {
                Text = master ? "" : row.Key == "system" ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Brushes.White,
                Width = 20,
                TextAlignment = TextAlignment.Center,
            };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(4, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var name = new TextBlock
        {
            Text = row.Name,
            Width = 150,
            FontSize = 13,
            FontWeight = master ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = row.Name,
        };
        DockPanel.SetDock(name, Dock.Left);
        dock.Children.Add(name);

        var slider = new Slider { Style = (Style)FindResource("SlimSlider"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        slider.ValueChanged += (_, e) =>
        {
            if (row.Updating) return;
            try { row.SetLevel((float)(e.NewValue / 100)); } catch { }
            percent.Text = $"{Math.Round(e.NewValue)}%";
        };
        dock.Children.Add(slider);

        row.Slider = slider;
        row.Percent = percent;
        row.MuteIcon = muteIcon;
        ShowVolume(row);

        if (!master) return dock;
        // A thin line under the whole-PC volume
        var wrap = new StackPanel();
        wrap.Children.Add(dock);
        wrap.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Surface"), Margin = new Thickness(4, 0, 4, 8) });
        return wrap;
    }

    private void ShowVolume(VolumeRow row)
    {
        if (row.Slider == null) return;
        float level = 0;
        bool muted = false;
        try { level = row.GetLevel(); muted = row.GetMute(); } catch { }
        row.Updating = true;
        row.Slider.Value = Math.Round(level * 100);
        row.Updating = false;
        row.Percent!.Text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
        row.MuteIcon!.Text = muted ? "" : level < 0.01 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        row.MuteIcon.Foreground = muted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.White;
        row.Slider.Opacity = muted ? 0.45 : 1;
    }

    private readonly Dictionary<string, ImageSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? AppIcon(string processName)
    {
        if (processName == "system") return null;
        if (_appIcons.TryGetValue(processName, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            using var p = Process.GetProcessesByName(processName).FirstOrDefault();
            if (p?.MainModule?.FileName is { } exe) icon = ShellIcon(exe);
        }
        catch { } // e.g. an app running as administrator
        _appIcons[processName] = icon;
        return icon;
    }

    /// <summary>The whole-PC volume, then one row per app with sound (grouped by app).</summary>
    private static List<VolumeRow> ReadVolumeRows()
    {
        var rows = new List<VolumeRow>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0 /* speakers */, 1 /* multimedia */, out IMMDevice device) != 0) return rows;

            // The whole PC
            var endpointIid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref endpointIid, 23, IntPtr.Zero, out object ep) == 0 && ep is IAudioEndpointVolume endpoint)
            {
                rows.Add(new VolumeRow
                {
                    Key = "master",
                    Name = "Whole PC",
                    GetLevel = () => { endpoint.GetMasterVolumeLevelScalar(out float l); return l; },
                    SetLevel = l => { var ctx = Guid.Empty; endpoint.SetMasterVolumeLevelScalar(l, ref ctx); },
                    GetMute = () => { endpoint.GetMute(out bool m); return m; },
                    SetMute = m => { var ctx = Guid.Empty; endpoint.SetMute(m, ref ctx); },
                });
            }

            // Each app
            var managerIid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerIid, 23, IntPtr.Zero, out object mgr) != 0) return rows;
            if (((IAudioSessionManager2)mgr).GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return rows;
            sessions.GetCount(out int count);

            var groups = new Dictionary<string, (string Name, List<ISimpleAudioVolume> Volumes)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl2 control) != 0) continue;
                control.GetState(out int state);
                if (state == 2) continue; // finished

                string key, name;
                if (control.IsSystemSoundsSession() == 0)
                {
                    key = "system";
                    name = "System sounds";
                }
                else
                {
                    control.GetProcessId(out uint pid);
                    if (pid == 0) continue;
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        key = p.ProcessName;
                        string? described = null;
                        try { described = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                        name = !string.IsNullOrWhiteSpace(described) ? described! : p.ProcessName;
                    }
                    catch
                    {
                        continue; // already closed
                    }
                }
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (name, new List<ISimpleAudioVolume>());
                g.Volumes.Add((ISimpleAudioVolume)control);
            }

            foreach (var (key, (name, volumes)) in groups.OrderBy(g => g.Key == "system").ThenBy(g => g.Value.Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new VolumeRow
                {
                    Key = key,
                    Name = name,
                    GetLevel = () => { volumes[0].GetMasterVolume(out float l); return l; },
                    SetLevel = l => { foreach (var v in volumes) { var ctx = Guid.Empty; v.SetMasterVolume(l, ref ctx); } },
                    GetMute = () => volumes.All(v => { v.GetMute(out bool m); return m; }),
                    SetMute = m => { foreach (var v in volumes) { var ctx = Guid.Empty; v.SetMute(m, ref ctx); } },
                });
            }
        }
        catch { }
        return rows;
    }
}
