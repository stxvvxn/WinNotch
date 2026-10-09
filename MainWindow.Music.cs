using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Windows.Media;
using Windows.Media.Control;

namespace WinNotch;

// Music mode: the music button (or clicking the album art) turns the open notch into big music controls -
// artwork, song details, shuffle / repeat, volume, and switching between apps that are playing.
public partial class MainWindow
{
    private string _album = "";
    private string? _pinnedSessionId;   // chosen with the app switcher; null = whatever Windows says is current
    private bool _settingMusicVolume;

    private void MusicToolButton_Click(object sender, RoutedEventArgs e) => ToggleTool("music");

    private void ArtBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _openTool = "music";
        RefreshSections();
    }

    /// <summary>The session to show: the one picked in the switcher if it's still there, otherwise the current one.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        if (_pinnedSessionId != null)
        {
            var pinned = _manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId == _pinnedSessionId);
            if (pinned != null) return pinned;
            _pinnedSessionId = null; // that app closed
        }
        return _manager.GetCurrentSession();
    }

    private void RefreshMusicPanel()
    {
        if (_openTool != "music") return;

        MusicTitle.Text = TitleText.Text;
        MusicArtist.Text = ArtistText.Text;
        MusicAlbum.Text = _album;
        MusicAlbum.Visibility = Vis(_album.Length > 0 && _album != ArtistText.Text);
        if (_hasArt && ArtBorder.Background is ImageBrush art)
        {
            MusicArt.Background = new ImageBrush(art.ImageSource) { Stretch = Stretch.UniformToFill };
            MusicArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            MusicArt.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            MusicArtPlaceholder.Visibility = Visibility.Visible;
        }

        // Which app, and how many others are playing
        int count = 0;
        try { count = _manager?.GetSessions().Count ?? 0; } catch { }
        string app = _session != null ? FriendlyAppName(_session.SourceAppUserModelId) : "No app";
        MusicSourceText.Text = count > 1 ? $"{app}  ·  {count - 1} more  ›" : app;
        MusicSourceButton.IsHitTestVisible = count > 1;

        // Play / pause, shuffle, repeat
        MusicPlayButton.Content = PlayPauseButton.Content;
        try
        {
            var info = _session?.GetPlaybackInfo();
            bool canShuffle = info?.Controls?.IsShuffleEnabled == true;
            bool canRepeat = info?.Controls?.IsRepeatEnabled == true;
            ShuffleButton.Opacity = canShuffle ? 1 : 0.3;
            RepeatButton.Opacity = canRepeat ? 1 : 0.3;
            ShuffleButton.Foreground = info?.IsShuffleActive == true ? AccentOrange : Brushes.White;
            var repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
            RepeatButton.Foreground = repeat != MediaPlaybackAutoRepeatMode.None ? AccentOrange : Brushes.White;
            RepeatButton.Content = repeat == MediaPlaybackAutoRepeatMode.Track ? "" : "";
            RepeatButton.ToolTip = repeat switch
            {
                MediaPlaybackAutoRepeatMode.Track => "Repeating this song",
                MediaPlaybackAutoRepeatMode.List => "Repeating all",
                _ => "Repeat",
            };
        }
        catch { }

        UpdateMusicVolume();
    }

    private void UpdateMusicVolume()
    {
        if (_openTool != "music" || _volume == null) return;
        try
        {
            _volume.GetMasterVolumeLevelScalar(out float level);
            _volume.GetMute(out bool mute);
            _settingMusicVolume = true;
            MusicVolume.Value = Math.Round(level * 100);
            MusicVolumeIcon.Text = mute || level < 0.01 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        }
        catch { }
        finally
        {
            _settingMusicVolume = false;
        }
    }

    private void MusicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingMusicVolume || _volume == null) return;
        try
        {
            var ctx = Guid.Empty;
            _volume.SetMasterVolumeLevelScalar((float)(e.NewValue / 100), ref ctx);
            _lastVolume = (float)(e.NewValue / 100); // don't treat our own change as a pop-up
            UpdateMusicVolume();
        }
        catch { }
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            if (_session != null && info?.Controls?.IsShuffleEnabled == true)
                await _session.TryChangeShuffleActiveAsync(info.IsShuffleActive != true);
        }
        catch { }
        RefreshMusicPanel();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            if (_session != null && info?.Controls?.IsRepeatEnabled == true)
            {
                var next = (info.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None) switch
                {
                    MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                    MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                    _ => MediaPlaybackAutoRepeatMode.None,
                };
                await _session.TryChangeAutoRepeatModeAsync(next);
            }
        }
        catch { }
        RefreshMusicPanel();
    }

    // Cycles through the apps that are playing (Spotify, a browser tab, a game...)
    private void MusicSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = _manager?.GetSessions().ToList();
            if (sessions == null || sessions.Count < 2) return;
            int i = _session == null ? -1 : sessions.FindIndex(s => s.SourceAppUserModelId == _session.SourceAppUserModelId);
            _pinnedSessionId = sessions[(i + 1) % sessions.Count].SourceAppUserModelId;
            AttachSession();
        }
        catch { }
    }

    /// <summary>"Spotify.exe" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify".</summary>
    private static string FriendlyAppName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Unknown app";
        string name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        name = System.IO.Path.GetFileNameWithoutExtension(name);
        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome",
            "msedge" or "microsoftedge" => "Edge",
            "firefox" => "Firefox",
            "spotify" => "Spotify",
            "app" when id.Contains("Apple", StringComparison.OrdinalIgnoreCase) => "Apple Music",
            "microsoft.zunemusic" or "music.ui" => "Media Player",
            "vlc" => "VLC",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Unknown app",
        };
    }
}
