using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinNotch;

// Music page (second page): what's playing through Windows' media controls - artwork, song, progress,
// shuffle / previous / play / next / repeat - plus extras for the chosen music service (Spotify's Like
// and Smart Shuffle), done by pressing that app's own keyboard shortcuts.
public partial class MainWindow
{
    /// <summary>A music service: how to recognise it in Windows' media controls and how to open it.</summary>
    public sealed record MusicServiceInfo(string Key, string Name, string[] Matches, string ProcessName, string[] OpenLinks);

    public static readonly MusicServiceInfo[] MusicServices =
    {
        new("auto", "Whatever is playing", Array.Empty<string>(), "", Array.Empty<string>()),
        new("spotify", "Spotify", new[] { "Spotify" }, "Spotify", new[] { "spotify:", "https://open.spotify.com" }),
        new("applemusic", "Apple Music", new[] { "AppleMusic", "AppleInc." }, "AppleMusic", new[] { "https://music.apple.com" }),
        new("youtubemusic", "YouTube Music", new[] { "YouTube Music", "youtube-music", "cinhimbnkkaeohfgghhklpknlkffjgod" }, "YouTube Music", new[] { "https://music.youtube.com" }),
        new("amazon", "Amazon Music", new[] { "AmazonMusic", "Amazon Music", "AmazonMobileLLC" }, "Amazon Music", new[] { "https://music.amazon.co.uk" }),
        new("tidal", "TIDAL", new[] { "TIDAL" }, "TIDAL", new[] { "tidal://", "https://listen.tidal.com" }),
        new("deezer", "Deezer", new[] { "Deezer" }, "Deezer", new[] { "deezer://", "https://www.deezer.com" }),
    };

    private GlobalSystemMediaTransportControlsSessionManager? _media;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _pickedSession; // chosen by clicking the app name; null = follow the settings / Windows
    private readonly DispatcherTimer _musicTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _musicTimerReady, _seeking, _updatingProgress, _mediaStarting;
    private TimeSpan _tlPosition, _tlEnd;
    private DateTimeOffset _tlUpdated;
    private bool _tlPlaying;

    private MusicServiceInfo PreferredService =>
        MusicServices.FirstOrDefault(s => s.Key == _settings.MusicService) ?? MusicServices[0];

    private static MusicServiceInfo? ServiceFor(string appId) =>
        MusicServices.Skip(1).FirstOrDefault(s => s.Matches.Any(m => appId.Contains(m, StringComparison.OrdinalIgnoreCase)));

    // ---------- Connecting to Windows' media controls ----------

    private async Task EnsureMediaAsync()
    {
        if (_media != null || _mediaStarting) return;
        _mediaStarting = true;
        try
        {
            _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _media.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(PickMediaSession);
            _media.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(PickMediaSession);
            PickMediaSession();
        }
        catch
        {
            _media = null;
        }
        finally
        {
            _mediaStarting = false;
        }
    }

    /// <summary>Which app to show: the one you clicked to, then your chosen service, then whatever Windows says.</summary>
    private void PickMediaSession()
    {
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            var sessions = _media?.GetSessions().ToList() ?? new();
            if (_pickedSession != null) pick = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pickedSession);
            if (pick == null && PreferredService.Key != "auto")
                pick = sessions.FirstOrDefault(s => ServiceFor(s.SourceAppUserModelId)?.Key == PreferredService.Key);
            pick ??= _media?.GetCurrentSession();
        }
        catch { }

        if (!ReferenceEquals(pick, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = pick;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }
        if (_page == 3) _ = ShowSongAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => { if (_page == 3) _ = ShowSongAsync(); });

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => { if (_page == 3) { ReadTimeline(); BuildMusicControls(); } });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => { if (_page == 3) ReadTimeline(); });

    // ---------- Showing the page ----------

    private void RefreshMusic()
    {
        if (!_musicTimerReady)
        {
            _musicTimerReady = true;
            _musicTimer.Tick += (_, _) =>
            {
                if (_page == 3 && _expanded) ShowProgress();
                else _musicTimer.Stop();
            };
        }
        _musicTimer.Start();
        if (_media == null) _ = EnsureMediaAsync();
        else PickMediaSession();
        _ = ShowSongAsync();
    }

    private void StopMusicTimer() => _musicTimer.Stop();

    private async Task ShowSongAsync()
    {
        var session = _session;
        if (session == null)
        {
            MusicTitle.Text = "Nothing playing";
            MusicArtist.Text = PreferredService.Key == "auto" ? "Play something and it shows up here" : $"Play something in {PreferredService.Name}";
            MusicAlbum.Text = "";
            MusicSourceText.Text = "No app playing";
            MusicArt.Background = (Brush)FindResource("Surface");
            MusicArtIcon.Visibility = Visibility.Visible;
            _tlEnd = TimeSpan.Zero;
            ShowProgress();
            BuildMusicControls();
            AnimateHeight();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(session, _session)) return;
            MusicTitle.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            MusicArtist.Text = props.Artist ?? "";
            MusicAlbum.Text = props.AlbumTitle != props.Artist ? props.AlbumTitle ?? "" : "";
            MusicTitle.ToolTip = MusicTitle.Text;

            var art = props.Thumbnail != null ? await LoadArtAsync(props.Thumbnail) : null;
            if (art != null)
            {
                MusicArt.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                MusicArtIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                MusicArt.Background = (Brush)FindResource("Surface");
                MusicArtIcon.Visibility = Visibility.Visible;
            }
        }
        catch { }

        int others = 0;
        try { others = (_media?.GetSessions().Count ?? 1) - 1; } catch { }
        string app = AppName(session.SourceAppUserModelId);
        MusicSourceText.Text = others > 0 ? $"{app}  ·  {others} more  ›" : app;
        MusicSource.Cursor = others > 0 ? Cursors.Hand : null;

        ReadTimeline();
        BuildMusicControls();
        AnimateHeight();
    }

    private static async Task<BitmapImage?> LoadArtAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = memory;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string AppName(string id)
    {
        if (ServiceFor(id) is { } service) return service.Name;
        string name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        name = Path.GetFileNameWithoutExtension(name);
        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "vlc" => "VLC",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Unknown app",
        };
    }

    // Click the app name to switch between apps that are playing
    private void MusicSource_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var sessions = _media?.GetSessions().ToList();
            if (sessions == null || sessions.Count < 2) return;
            int i = _session == null ? -1 : sessions.FindIndex(s => s.SourceAppUserModelId == _session.SourceAppUserModelId);
            _pickedSession = sessions[(i + 1) % sessions.Count].SourceAppUserModelId;
            PickMediaSession();
        }
        catch { }
    }

    // ---------- Progress ----------

    private void ReadTimeline()
    {
        try
        {
            var tl = _session?.GetTimelineProperties();
            var info = _session?.GetPlaybackInfo();
            if (tl == null) { _tlEnd = TimeSpan.Zero; ShowProgress(); return; }
            _tlPosition = tl.Position - tl.StartTime;
            _tlEnd = tl.EndTime - tl.StartTime;
            _tlUpdated = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : tl.LastUpdatedTime;
            _tlPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            MusicProgress.IsEnabled = info?.Controls?.IsPlaybackPositionEnabled == true;
        }
        catch { }
        ShowProgress();
    }

    private TimeSpan CurrentPosition()
    {
        var pos = _tlPosition;
        if (_tlPlaying) pos += DateTimeOffset.Now - _tlUpdated;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (pos > _tlEnd) pos = _tlEnd;
        return pos;
    }

    private void ShowProgress()
    {
        if (_seeking) return;
        bool known = _tlEnd > TimeSpan.Zero;
        var pos = known ? CurrentPosition() : TimeSpan.Zero;
        _updatingProgress = true;
        MusicProgress.Value = known ? pos.TotalMilliseconds / _tlEnd.TotalMilliseconds * 1000 : 0;
        _updatingProgress = false;
        MusicElapsed.Text = known ? Time(pos) : "";
        MusicRemaining.Text = known ? "-" + Time(_tlEnd - pos) : "";
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void MusicProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void MusicProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingProgress || _tlEnd <= TimeSpan.Zero) return;
        var target = TimeSpan.FromMilliseconds(_tlEnd.TotalMilliseconds * e.NewValue / 1000);
        MusicElapsed.Text = Time(target);
        MusicRemaining.Text = "-" + Time(_tlEnd - target);
    }

    private async void MusicProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // IsMoveToPointEnabled moves the slider on the click itself; wait for that, then jump
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        _seeking = false;
        if (_session == null || _tlEnd <= TimeSpan.Zero) return;
        var target = TimeSpan.FromMilliseconds(_tlEnd.TotalMilliseconds * MusicProgress.Value / 1000);
        try
        {
            var start = _session.GetTimelineProperties().StartTime;
            await _session.TryChangePlaybackPositionAsync((start + target).Ticks);
            _tlPosition = target;
            _tlUpdated = DateTimeOffset.Now;
        }
        catch { }
        ShowProgress();
    }

    // ---------- Controls ----------

    private void BuildMusicControls()
    {
        MusicControls.Children.Clear();
        MusicExtrasPanel.Children.Clear();

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
        try { info = _session?.GetPlaybackInfo(); } catch { }
        var c = info?.Controls;
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var accent = (Brush)FindResource("Accent");

        var repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
        MusicControls.Children.Add(ControlButton("", "Shuffle", 15, c?.IsShuffleEnabled == true,
            info?.IsShuffleActive == true ? accent : null, async () =>
            {
                if (_session != null) await _session.TryChangeShuffleActiveAsync(info?.IsShuffleActive != true);
            }));
        MusicControls.Children.Add(ControlButton("", "Previous", 18, c?.IsPreviousEnabled != false, null,
            async () => { if (_session != null) await _session.TrySkipPreviousAsync(); }));
        MusicControls.Children.Add(ControlButton(playing ? "" : "", playing ? "Pause" : "Play", 26, true, null,
            async () =>
            {
                if (_session != null) await _session.TryTogglePlayPauseAsync();
                else OpenService(PreferredService.Key == "auto" ? MusicServices[1] : PreferredService);
            }, big: true));
        MusicControls.Children.Add(ControlButton("", "Next", 18, c?.IsNextEnabled != false, null,
            async () => { if (_session != null) await _session.TrySkipNextAsync(); }));
        MusicControls.Children.Add(ControlButton(repeat == MediaPlaybackAutoRepeatMode.Track ? "" : "",
            repeat switch { MediaPlaybackAutoRepeatMode.Track => "Repeating this song", MediaPlaybackAutoRepeatMode.List => "Repeating all", _ => "Repeat" },
            15, c?.IsRepeatEnabled == true, repeat != MediaPlaybackAutoRepeatMode.None ? accent : null, async () =>
            {
                if (_session == null) return;
                var next = repeat switch
                {
                    MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                    MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                    _ => MediaPlaybackAutoRepeatMode.None,
                };
                await _session.TryChangeAutoRepeatModeAsync(next);
            }));

        // Extras for the service that's playing (or the one chosen in Settings)
        if (!_settings.MusicExtras) return;
        var service = (_session != null ? ServiceFor(_session.SourceAppUserModelId) : null)
                      ?? (PreferredService.Key != "auto" ? PreferredService : null);
        if (service == null) return;

        if (service.Key == "spotify")
        {
            MusicExtrasPanel.Children.Add(Chip("", "Like", "Add to (or remove from) Liked Songs - Spotify's Alt+Shift+B",
                () => SendToApp(service, "Alt+Shift+B", "Liked")));
            MusicExtrasPanel.Children.Add(Chip("", "Smart Shuffle", "Steps Spotify's shuffle button: Shuffle → Smart Shuffle → off (Smart Shuffle needs Premium)",
                () => SendToApp(service, "Ctrl+S", "Shuffle changed")));
        }
        MusicExtrasPanel.Children.Add(Chip("", $"Open {service.Name}", $"Bring {service.Name} to the front",
            () => OpenService(service)));
    }

    private FrameworkElement ControlButton(string glyph, string tip, double size, bool enabled, Brush? colour,
                                           Func<Task> action, bool big = false)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Foreground = colour ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = big ? 52 : 42,
            Height = big ? 52 : 42,
            CornerRadius = new CornerRadius(big ? 26 : 21),
            Margin = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : null,
            Opacity = enabled ? 1 : 0.3,
            ToolTip = tip,
            Child = text,
        };
        if (enabled)
        {
            button.MouseEnter += (_, _) => button.Background = (Brush)FindResource("SurfaceHover");
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += async (_, _) =>
            {
                try { await action(); } catch { }
                await Task.Delay(250);
                ReadTimeline();
                BuildMusicControls();
            };
        }
        return button;
    }

    private FrameworkElement Chip(string glyph, string label, string tip, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            Background = (Brush)FindResource("Surface"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 13, 6),
            Margin = new Thickness(4),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = row,
        };
        chip.MouseEnter += (_, _) => chip.Background = (Brush)FindResource("SurfaceHover");
        chip.MouseLeave += (_, _) => chip.Background = (Brush)FindResource("Surface");
        chip.MouseLeftButtonUp += (_, _) => action();
        return chip;
    }

    // ---------- Talking to the music app ----------

    /// <summary>
    /// Presses one of the app's own shortcuts: brings it to the front for a moment, presses the keys,
    /// then puts the keyboard back. (Windows' media controls don't cover things like "Like".)
    /// </summary>
    private async void SendToApp(MusicServiceInfo service, string keys, string done)
    {
        IntPtr window = IntPtr.Zero;
        try
        {
            foreach (var p in Process.GetProcessesByName(service.ProcessName))
            {
                if (p.MainWindowHandle != IntPtr.Zero) window = p.MainWindowHandle;
                p.Dispose();
            }
        }
        catch { }

        if (window == IntPtr.Zero)
        {
            MusicArtist.Text = $"Open the {service.Name} app window first (it can't be only in the tray)";
            return;
        }

        IntPtr back = GetForegroundWindow();
        if (IsIconic(window)) ShowWindow(window, 9 /* restore */);
        ForceForegroundTo(window);
        await Task.Delay(120);
        KeySender.Send(keys);
        await Task.Delay(120);
        if (back != IntPtr.Zero && back != window) ForceForegroundTo(back);
        MusicSourceText.Text = $"{service.Name}  ·  {done}";
    }

    private void ForceForegroundTo(IntPtr window)
    {
        if (SetForegroundWindow(window)) return;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint ours = GetCurrentThreadId();
        if (fgThread == 0 || fgThread == ours) return;
        AttachThreadInput(ours, fgThread, true);
        try { SetForegroundWindow(window); }
        finally { AttachThreadInput(ours, fgThread, false); }
    }

    private void OpenService(MusicServiceInfo service)
    {
        // Already running: bring its window forward
        try
        {
            foreach (var p in Process.GetProcessesByName(service.ProcessName))
            {
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);
                    ForceForegroundTo(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { }
        foreach (var link in service.OpenLinks)
            if (WebTools.Open(link)) return;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
