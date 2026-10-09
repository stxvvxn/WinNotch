using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Volume for each app: one slider and a mute button per app that's making (or recently made) sound.
public partial class MainWindow
{
    private sealed class MixerApp
    {
        public string Key = "";
        public string Name = "";
        public readonly List<ISimpleAudioVolume> Volumes = new();
        public Slider? Slider;
        public TextBlock? Percent;
        public Button? Mute;
        public bool Updating;
    }

    private readonly List<MixerApp> _mixerApps = new();
    private readonly DispatcherTimer _mixerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _mixerTimerReady;

    private void MixerToolButton_Click(object sender, RoutedEventArgs e) => ToggleTool("mixer");

    private void RefreshMixer()
    {
        if (!_mixerTimerReady)
        {
            _mixerTimerReady = true;
            _mixerTimer.Tick += (_, _) =>
            {
                if (_openTool == "mixer" && _expanded) RefreshMixer();
                else _mixerTimer.Stop();
            };
        }
        _mixerTimer.Start();

        var apps = ReadMixerApps();

        // Same apps as before: just update the values (rebuilding would interrupt a drag)
        if (apps.Select(a => a.Key).SequenceEqual(_mixerApps.Select(a => a.Key)))
        {
            for (int i = 0; i < apps.Count; i++)
            {
                _mixerApps[i].Volumes.Clear();
                _mixerApps[i].Volumes.AddRange(apps[i].Volumes);
                if (_mixerApps[i].Slider?.IsMouseCaptureWithin != true) ShowMixerValues(_mixerApps[i]);
            }
            return;
        }

        _mixerApps.Clear();
        _mixerApps.AddRange(apps);
        MixerRows.Children.Clear();
        foreach (var app in apps) MixerRows.Children.Add(BuildMixerRow(app));
        MixerEmpty.Visibility = Vis(apps.Count == 0);
        AnimateExpandedHeight();
    }

    private FrameworkElement BuildMixerRow(MixerApp app)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Height = 30 };

        var mute = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 30,
            Height = 30,
            FontSize = 13,
            ToolTip = "Mute / unmute",
        };
        mute.Click += (_, _) =>
        {
            bool muteNow = !IsMuted(app);
            foreach (var v in app.Volumes) { var ctx = Guid.Empty; try { v.SetMute(muteNow, ref ctx); } catch { } }
            ShowMixerValues(app);
        };
        DockPanel.SetDock(mute, Dock.Right);
        row.Children.Add(mute);

        var percent = new TextBlock
        {
            Width = 36,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        DockPanel.SetDock(percent, Dock.Right);
        row.Children.Add(percent);

        var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Source = AppIcon(app.Key);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = app.Name,
            Width = 110,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = app.Name,
        };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Focusable = false,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentOrange,
        };
        slider.ValueChanged += (_, e) =>
        {
            if (app.Updating) return;
            foreach (var v in app.Volumes) { var ctx = Guid.Empty; try { v.SetMasterVolume((float)(e.NewValue / 100), ref ctx); } catch { } }
            percent.Text = $"{Math.Round(e.NewValue)}%";
        };
        row.Children.Add(slider);

        app.Slider = slider;
        app.Percent = percent;
        app.Mute = mute;
        ShowMixerValues(app);
        return row;
    }

    private static bool IsMuted(MixerApp app)
    {
        foreach (var v in app.Volumes)
        {
            try { v.GetMute(out bool m); if (!m) return false; } catch { }
        }
        return app.Volumes.Count > 0;
    }

    private void ShowMixerValues(MixerApp app)
    {
        if (app.Slider == null || app.Volumes.Count == 0) return;
        float level = 0;
        try { app.Volumes[0].GetMasterVolume(out level); } catch { }
        bool muted = IsMuted(app);
        app.Updating = true;
        app.Slider.Value = Math.Round(level * 100);
        app.Updating = false;
        app.Percent!.Text = muted ? "Muted" : $"{Math.Round(level * 100)}%";
        app.Mute!.Content = muted ? "" : "";
        app.Mute.Foreground = muted ? LowRed : Brushes.White;
        app.Slider.Opacity = muted ? 0.4 : 1;
    }

    private static ImageSource? AppIcon(string key)
    {
        if (key == "system") return null;
        try
        {
            var p = Process.GetProcessesByName(key).FirstOrDefault();
            string? exe = p?.MainModule?.FileName;
            return exe != null ? GetIcon(exe) : null;
        }
        catch
        {
            return null; // e.g. an app running as administrator
        }
    }

    /// <summary>Every app with an audio session on the speakers / headphones, grouped by app.</summary>
    private static List<MixerApp> ReadMixerApps()
    {
        var apps = new List<MixerApp>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0) return apps;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out object obj) != 0) return apps;
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return apps;
            sessions.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl2 control) != 0) continue;
                control.GetState(out int state);
                if (state == 2) continue; // expired

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
                        continue; // process already gone
                    }
                }

                var app = apps.FirstOrDefault(a => a.Key == key);
                if (app == null)
                {
                    app = new MixerApp { Key = key, Name = name };
                    apps.Add(app);
                }
                app.Volumes.Add((ISimpleAudioVolume)control);
            }
        }
        catch { }

        // System sounds last, apps alphabetically
        return apps.OrderBy(a => a.Key == "system").ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Core Audio session interfaces ----------

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
