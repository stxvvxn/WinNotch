using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinNotch;

// Dynamic Island-style pop-ups on the collapsed notch: volume, brightness, charging, timer alerts...
public partial class MainWindow
{
    private const double HudW = 300;
    private readonly DispatcherTimer _hudTimer = new();
    private bool _hudShowing;

    private void InitHud()
    {
        _hudTimer.Tick += (_, _) => EndHud();
        _volumeTimer.Tick += (_, _) => PollVolume();
        _volumeTimer.Start();
    }

    /// <summary>
    /// Widens the collapsed notch for a moment to show an icon plus either a level bar (0-1) or some text.
    /// </summary>
    private void ShowHud(string glyph, double? level, string? text, string value,
                         TimeSpan? duration = null, Brush? iconBrush = null, Color? swatch = null,
                         bool overGames = false, ImageSource? thumbnail = null)
    {
        // Low-battery warnings and the in-game glance still show while a game is fullscreen
        if (overGames && _hiddenForFullscreen && !_userHidden && !_hiddenForSnip && !_overlayActive)
        {
            _overlayActive = true;
            ApplyNotchVisibility();
        }

        if (_expanded || NotchHidden) return;

        // Icon, or a colour swatch for the colour picker
        HudIcon.Text = glyph;
        HudIcon.Foreground = iconBrush ?? Brushes.White;
        HudIcon.Visibility = Vis(swatch == null && thumbnail == null);
        HudSwatch.Visibility = Vis(swatch != null);
        HudThumb.Visibility = Vis(thumbnail != null);
        if (thumbnail != null) HudThumb.Background = new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill };
        if (swatch is Color c) HudSwatch.Fill = new SolidColorBrush(c);

        if (level is double l)
        {
            l = Math.Clamp(l, 0, 1);
            HudFillCol.Width = new GridLength(l, GridUnitType.Star);
            HudRestCol.Width = new GridLength(1 - l, GridUnitType.Star);
            HudBar.Visibility = Visibility.Visible;
            HudText.Visibility = Visibility.Collapsed;
        }
        else
        {
            HudText.Text = text ?? "";
            HudBar.Visibility = Visibility.Collapsed;
            HudText.Visibility = Visibility.Visible;
        }
        HudValue.Text = value;

        if (!_hudShowing)
        {
            _hudShowing = true;
            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(HudW, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
            Fade(CompactView, 0, 100);
            Fade(StatusBar, 0, 100);
            Fade(HudView, 1, 180, 90);
        }

        _hudTimer.Stop();
        _hudTimer.Interval = duration ?? TimeSpan.FromSeconds(1.6);
        _hudTimer.Start();
    }

    private void EndHud(bool immediate = false)
    {
        _hudTimer.Stop();
        if (!_hudShowing) return;
        _hudShowing = false;
        if (_overlayActive) EndOverlaySoon();

        Fade(HudView, 0, immediate ? 1 : 120);
        if (_expanded) return; // SetExpanded is animating everything else

        Fade(StatusBar, 1, 200, 100);
        Fade(CompactView, 1, 200, 100);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(CollapsedW, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Sound bars ----------

    private readonly Random _random = new();
    private bool _barsAnimating;

    private void UpdateSoundBars()
    {
        bool show = _settings.ShowSoundBars && MediaShown && _tlPlaying && !TimerActive && !DownloadShowing;
        SoundBars.Visibility = Vis(show);
        if (show == _barsAnimating) return;
        _barsAnimating = show;

        foreach (var child in SoundBars.Children)
        {
            if (child is not Rectangle bar || bar.RenderTransform is not ScaleTransform scale) continue;
            if (show)
            {
                // Each bar bounces at its own speed so it looks like real audio
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(_random.Next(280, 520)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromMilliseconds(_random.Next(0, 250)),
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.3;
            }
        }
    }

    // ---------- Volume (Windows Core Audio) ----------

    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private IAudioEndpointVolume? _volume;
    private string? _volumeDeviceId;
    private float _lastVolume = -1;
    private bool _lastMute;
    private int _volumeTicks;

    private void PollVolume()
    {
        try
        {
            // Re-check the default output every ~3 seconds (headphones plugged in, etc.)
            if (_volume == null || ++_volumeTicks % 20 == 0) RefreshVolumeDevice();
            if (_volume == null) return;

            _volume.GetMasterVolumeLevelScalar(out float level);
            _volume.GetMute(out bool mute);

            bool changed = _lastVolume >= 0 && (Math.Abs(level - _lastVolume) > 0.001f || mute != _lastMute);
            _lastVolume = level;
            _lastMute = mute;

            if (changed && _settings.VolumePopup)
            {
                string glyph = mute ? "" : level < 0.01 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
                ShowHud(glyph, mute ? 0 : level, null, mute ? "Muted" : $"{Math.Round(level * 100)}");
            }
        }
        catch
        {
            _volume = null; // device went away; try again next tick
        }
    }

    private void RefreshVolumeDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out IMMDevice device) != 0) return;

        device.GetId(out string id);
        if (id == _volumeDeviceId && _volume != null) return;

        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object obj) != 0) return;

        _volume = (IAudioEndpointVolume)obj;
        _volumeDeviceId = id;
        _lastVolume = -1; // new device: don't pop up just because its level differs
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ---------- Brightness (laptop screens report changes through WMI) ----------

    private ManagementEventWatcher? _brightnessWatcher;

    private void StartBrightnessWatcher()
    {
        try
        {
            _brightnessWatcher = new ManagementEventWatcher(
                new ManagementScope(@"root\wmi"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));

            _brightnessWatcher.EventArrived += (_, e) =>
            {
                int brightness = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                Dispatcher.InvokeAsync(() =>
                {
                    if (_settings.BrightnessPopup)
                        ShowHud("", brightness / 100.0, null, brightness.ToString());
                });
            };
            _brightnessWatcher.Start();
        }
        catch
        {
            _brightnessWatcher = null; // desktop monitors don't report brightness; that's fine
        }
    }
}
