using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

// Screenshots go on the shelf: Snipping Tool / Win+Shift+S / PrintScreen / Win+PrintScreen and ShareX.
// Two ways in: the screenshot apps put the picture on the clipboard, and they save a file in a known folder.
public partial class MainWindow
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly List<FileSystemWatcher> _shotWatchers = new();
    private bool _clipboardHooked;
    private DateTime _lastShotAt = DateTime.MinValue;
    private string? _pendingShotFile;
    private bool _waitingForShotFile;

    // Apps whose clipboard pictures count as screenshots (copying a picture from a web page doesn't)
    private static readonly string[] ScreenshotApps =
        { "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Flameshot", "PicPick" };

    public static string ScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinNotch Screenshots");

    private void ApplyScreenshots()
    {
        foreach (var w in _shotWatchers) w.Dispose();
        _shotWatchers.Clear();

        bool on = _settings.ShelfPage && _settings.ShelfScreenshots;
        if (_hwnd != IntPtr.Zero && on && !_clipboardHooked)
        {
            _clipboardHooked = AddClipboardFormatListener(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(ClipboardHook);
        }
        if (!on) return;

        // Where Windows (Win+PrintScreen, Snipping Tool) and ShareX save their screenshots
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
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = sub, NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
                w.Created += (_, e) =>
                {
                    string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                        Dispatcher.InvokeAsync(() => OnScreenshotFile(e.FullPath));
                };
                _shotWatchers.Add(w);
            }
            catch { }
        }
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _settings.ShelfPage && _settings.ShelfScreenshots)
            Dispatcher.InvokeAsync(OnClipboardChanged, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private async void OnClipboardChanged()
    {
        // The app that just copied may still have the clipboard open, so try a few times
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null || !IsScreenshot(data)) return;
                OnClipboardScreenshot(data);
                return;
            }
            catch (COMException)
            {
                await Task.Delay(60);
            }
            catch
            {
                return;
            }
        }
    }

    private static bool IsScreenshot(IDataObject data)
    {
        if (!(data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap))) return false;
        if (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Html)) return false; // copied from a web page / Explorer

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
        if (DateTime.Now - _lastShotAt < TimeSpan.FromSeconds(4)) return; // same screenshot, already handled
        _lastShotAt = DateTime.Now;
        var image = ReadClipboardImage(data);
        if (image == null) return;

        // The Snipping Tool and ShareX usually save a file too - wait a moment and use that rather than a copy
        _pendingShotFile = null;
        _waitingForShotFile = true;
        await Task.Delay(1500);
        _waitingForShotFile = false;

        string? file = _pendingShotFile ?? SaveScreenshotCopy(image);
        if (file != null) ScreenshotToShelf(file);
    }

    private async void OnScreenshotFile(string path)
    {
        if (_waitingForShotFile)
        {
            _pendingShotFile = path; // the clipboard side will add it
            return;
        }
        if (DateTime.Now - _lastShotAt < TimeSpan.FromSeconds(4)) return;
        _lastShotAt = DateTime.Now;

        // Give the app a moment to finish writing the file
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            try
            {
                using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (f.Length > 0) break;
            }
            catch { }
        }
        ScreenshotToShelf(path);
    }

    private void ScreenshotToShelf(string file)
    {
        AddToShelf(file);
        SaveShelf();
        if (!_settings.ShelfScreenshotPeek || NotchHidden) return;

        // Pop open on the shelf so you can see it landed, then tuck away if the mouse isn't there
        SetExpanded(true);
        ShowPage(2);
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hoverTimer.Start();
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

    private static string? SaveScreenshotCopy(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotFolder);
            string file = Path.Combine(ScreenshotFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
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

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
}
