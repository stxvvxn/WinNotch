using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

// More Dynamic Island pop-ups: screenshot taken, battery full / battery saver, rain on the way.
public partial class MainWindow
{
    private static readonly Brush SaverYellow = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush RainBlue = Frozen(Color.FromRgb(0x64, 0xD2, 0xFF));

    /// <summary>A pop-up on the closed notch, or a message across the top if it's open.</summary>
    private void Notify(string glyph, string text, string value = "", Brush? colour = null,
                        double seconds = 3, ImageSource? thumbnail = null)
    {
        if (_expanded)
            ShowTopMessage(value.Length > 0 ? $"{text} · {value}" : text, colour ?? Brushes.White, seconds);
        else
            ShowHud(glyph, null, text, value, TimeSpan.FromSeconds(seconds), colour, thumbnail: thumbnail);
    }

    // ================= Screenshots =================

    private readonly List<FileSystemWatcher> _shotWatchers = new();
    private DateTime _lastShotAt = DateTime.MinValue;
    private string? _pendingShotFile;
    private bool _waitingForShotFile;

    // Apps that put a screenshot on the clipboard
    private static readonly string[] ScreenshotApps =
        { "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Flameshot", "PicPick" };

    private void ApplyScreenshotWatch()
    {
        foreach (var w in _shotWatchers) w.Dispose();
        _shotWatchers.Clear();
        if (!_settings.ScreenshotPopup) return;

        // Where Windows (Win+PrintScreen, Snipping Tool) and ShareX save screenshots
        var folders = new[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"), false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Screenshots"), true),
        };
        foreach (var (folder, sub) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = sub,
                    NotifyFilter = NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                w.Created += (_, e) =>
                {
                    if (IsImage(e.FullPath) || Path.GetExtension(e.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                        Dispatcher.InvokeAsync(() => OnScreenshotFile(e.FullPath));
                };
                _shotWatchers.Add(w);
            }
            catch { }
        }
    }

    /// <summary>Called from the clipboard watcher: true if this looks like a fresh screenshot.</summary>
    private bool IsScreenshotOnClipboard(IDataObject data)
    {
        if (!_settings.ScreenshotPopup) return false;
        if (!(data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap))) return false;
        if (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Html)) return false; // copied from a browser / Explorer

        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return true; // classic PrintScreen
        try
        {
            GetWindowThreadProcessId(owner, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return ScreenshotApps.Any(a => p.ProcessName.Equals(a, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async void OnClipboardScreenshot(IDataObject data)
    {
        if (DateTime.Now - _lastShotAt < TimeSpan.FromSeconds(4)) return; // already handled (e.g. our own button)
        _lastShotAt = DateTime.Now;

        BitmapSource? image = ReadClipboardImage(data);
        if (image == null) return;

        // The Snipping Tool usually saves a file too - wait a moment and use that instead of a copy
        _pendingShotFile = null;
        _waitingForShotFile = true;
        await Task.Delay(1500);
        _waitingForShotFile = false;

        string? file = _pendingShotFile;
        if (file == null && _settings.ScreenshotToShelf)
            file = SaveScreenshotCopy(image);

        AnnounceScreenshot(file, image);
    }

    private async void OnScreenshotFile(string path)
    {
        if (_waitingForShotFile)
        {
            _pendingShotFile = path; // the clipboard handler will announce it
            return;
        }
        if (DateTime.Now - _lastShotAt < TimeSpan.FromSeconds(4)) return;
        _lastShotAt = DateTime.Now;

        // Give the app a moment to finish writing the file
        BitmapSource? thumb = null;
        for (int i = 0; i < 10 && thumb == null; i++)
        {
            await Task.Delay(300);
            thumb = LoadThumbnail(path);
        }
        AnnounceScreenshot(path, thumb);
    }

    private void AnnounceScreenshot(string? file, BitmapSource? image)
    {
        bool shelved = file != null && _settings.ScreenshotToShelf;
        if (shelved) AddToShelf(file!);
        Notify("\uE722", shelved ? "Screenshot added to shelf" : "Screenshot taken", "", null, 3,
               image ?? (file != null ? LoadThumbnail(file) : null));
    }

    private static BitmapSource? ReadClipboardImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                png.Position = 0;
                var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            }
            var bmp = Clipboard.GetImage();
            bmp?.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 120;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // still being written
        }
    }

    private static string? SaveScreenshotCopy(BitmapSource image)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinNotch Screenshots");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            using var stream = File.Create(file);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return file;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

    // ================= Battery full / battery saver =================

    private bool _fullAnnounced;
    private int _notChargingTicks;
    private bool? _saverWasOn;

    private void CheckBatteryPopups(int percent, bool pluggedIn, byte batteryFlag, byte systemStatus)
    {
        // Fully charged (or stopped at a charge limit like 80%): once per time on the charger
        bool charging = (batteryFlag & 8) != 0;
        if (!pluggedIn)
        {
            _fullAnnounced = false;
            _notChargingTicks = 0;
        }
        else if (!_fullAnnounced)
        {
            _notChargingTicks = charging ? 0 : _notChargingTicks + 1;
            bool full = percent >= 100 || (_notChargingTicks >= 2 && percent >= 60);
            if (full)
            {
                _fullAnnounced = true;
                if (_settings.BatteryFullPopup)
                    Notify("\uE865", percent >= 100 ? "Fully charged" : $"Charged to {percent}%", "Unplug any time",
                           ChargingGreen, 4);
            }
        }

        // Battery saver switched on / off
        bool saverOn = systemStatus == 1;
        if (_saverWasOn is bool was && was != saverOn && _settings.BatterySaverPopup)
            Notify("\uE85A", saverOn ? "Battery saver on" : "Battery saver off", "", saverOn ? SaverYellow : null, 3);
        _saverWasOn = saverOn;
    }

    // ================= Rain on the way =================

    private bool _rainAnnounced;

    /// <summary>Looks at the next couple of hours (15-minute steps) for rain or snow starting soon.</summary>
    private void CheckRain(JsonElement root)
    {
        if (!_settings.RainPopup) return;
        try
        {
            var current = root.GetProperty("current");
            int code = current.GetProperty("weather_code").GetInt32();
            double nowPrecip = current.TryGetProperty("precipitation", out var np) ? np.GetDouble() : 0;
            bool wetNow = nowPrecip > 0.05 || code is >= 51 and <= 67 or >= 71 and <= 86 or >= 95;

            var m15 = root.GetProperty("minutely_15");
            var times = m15.GetProperty("time").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            var rain = m15.GetProperty("precipitation").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToList();
            var snow = m15.TryGetProperty("snowfall", out var sf)
                ? sf.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToList()
                : new List<double>();

            var now = DateTime.UtcNow;
            int lead = Math.Clamp(_settings.RainLeadMinutes, 15, 120);
            (double Minutes, bool Snow, double Amount)? coming = null;
            bool anyInWindow = false;
            for (int i = 0; i < times.Count && i < rain.Count; i++)
            {
                if (!DateTime.TryParse(times[i], CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
                double minutes = (t - now).TotalMinutes;
                if (minutes < -14 || minutes > lead) continue;
                if (rain[i] < 0.1) continue;
                anyInWindow = true;
                coming ??= (Math.Max(0, minutes), i < snow.Count && snow[i] > 0, rain[i]);
            }

            if (wetNow || !anyInWindow)
            {
                // Already raining, or dry for a while: ready to warn about the next shower
                if (!anyInWindow && !wetNow) _rainAnnounced = false;
                if (wetNow) _rainAnnounced = true;
                return;
            }

            if (_rainAnnounced || coming is not { } c) return;
            _rainAnnounced = true;

            string what = c.Snow ? "Snow" : c.Amount >= 1 ? "Heavy rain" : c.Amount >= 0.3 ? "Rain" : "Light rain";
            string when = c.Minutes < 10 ? "starting soon" : $"in about {Math.Round(c.Minutes / 5) * 5:0} min";
            Notify(WeatherGlyph(c.Snow ? 73 : 63, true), $"{what} {when}", "", RainBlue, 6);
        }
        catch
        {
            // Forecast missing or in an unexpected shape: try again next time
        }
    }

    // ================= Buttons along the top =================

    private void ApplyTopButtons()
    {
        var buttons = TopButtonsPanel.Children.OfType<FrameworkElement>().ToList();
        TopButtonsPanel.Children.Clear();
        foreach (var setting in _settings.TopButtons)
        {
            var b = buttons.FirstOrDefault(x => Equals(x.Tag, setting.Key));
            if (b == null) continue;
            buttons.Remove(b);
            b.Visibility = Vis(setting.Shown);
            TopButtonsPanel.Children.Add(b);
        }
        foreach (var leftover in buttons) TopButtonsPanel.Children.Add(leftover); // anything not in the list

        // A hidden tool shouldn't stay open
        if (_openTool != null && !_settings.TopButtonShown(_openTool))
        {
            _openTool = null;
            RefreshSections();
        }

        // Keep the message at the top clear of the buttons
        int shown = TopButtonsPanel.Children.OfType<FrameworkElement>().Count(x => x.Visibility == Visibility.Visible);
        TopMessage.Margin = new Thickness(12 + shown * 26 + 10, 0, 120, 0);
    }
}
