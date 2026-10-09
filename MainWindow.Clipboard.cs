using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

/// <summary>One copied piece of text in the clipboard history.</summary>
public sealed class ClipEntry
{
    public string Full { get; init; } = "";

    public string Preview
    {
        get
        {
            // First line, with runs of spaces squashed, max ~90 characters
            string line = Full.Trim().Split('\n')[0].Trim();
            line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length > 90 ? line[..90] + "…" : line;
        }
    }

    public string Tooltip => Full.Length > 600 ? Full[..600] + "…" : Full;
}

// Clipboard history (memory only, never saved to disk) and "screenshot to shelf"
public partial class MainWindow
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxClipboardItems = 10;

    private readonly ObservableCollection<ClipEntry> _clipHistory = new();
    private HwndSource? _hwndSource;
    private bool _awaitingScreenshot;
    private DateTime _screenshotRequestedAt;
    private DispatcherTimer? _topMessageTimer;

    private void InitClipboard()
    {
        ClipboardItems.ItemsSource = _clipHistory;
        _clipHistory.CollectionChanged += (_, _) =>
        {
            ClipboardEmpty.Visibility = Vis(_clipHistory.Count == 0);
            AnimateExpandedHeight();
        };

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private void StopClipboard()
    {
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _hwndSource?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            Dispatcher.InvokeAsync(OnClipboardChanged, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private async void OnClipboardChanged()
    {
        // The app that just copied may still have the clipboard open, so retry briefly
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;

                // A screenshot we asked for has arrived
                if (_awaitingScreenshot && DateTime.Now - _screenshotRequestedAt < TimeSpan.FromMinutes(2)
                    && (data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap)))
                {
                    _awaitingScreenshot = false;
                    SaveScreenshotToShelf(data);
                    return;
                }

                // A screenshot from Win+Shift+S, PrintScreen, ShareX...: pop-up (and shelf)
                if (IsScreenshotOnClipboard(data))
                {
                    OnClipboardScreenshot(data);
                    return;
                }

                if (!_settings.ClipboardHistory) return;

                // Password managers mark secrets so clipboard tools ignore them; respect that
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
                    || data.GetDataPresent("Clipboard Viewer Ignore")
                    || IsMarkedNotForHistory(data))
                    return;

                if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
                    AddClipboardText(text);
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

    private static bool IsMarkedNotForHistory(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("CanIncludeInClipboardHistory")
                && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms
                && ms.Length >= 4)
            {
                var bytes = ms.ToArray();
                return BitConverter.ToInt32(bytes, 0) == 0;
            }
        }
        catch { }
        return false;
    }

    private void AddClipboardText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var existing = _clipHistory.FirstOrDefault(c => c.Full == text);
        if (existing != null) _clipHistory.Remove(existing);

        _clipHistory.Insert(0, new ClipEntry { Full = text });
        while (_clipHistory.Count > MaxClipboardItems) _clipHistory.RemoveAt(_clipHistory.Count - 1);
    }

    // ---------- Clipboard panel ----------

    private void ClipboardToolButton_Click(object sender, RoutedEventArgs e) => ToggleTool("clipboard");

    private void ClipboardItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipEntry entry) return;
        if (TrySetClipboardText(entry.Full)) ShowTopMessage("Copied", ChargingGreen, 2);
    }

    private void ClipboardClear_Click(object sender, RoutedEventArgs e) => _clipHistory.Clear();

    private static bool TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }

    // ---------- Screenshot to shelf ----------

    private async void ScreenshotTool_Click(object sender, RoutedEventArgs e)
    {
        // Get the notch out of the way so it isn't in the screenshot
        SetExpanded(false);
        _hiddenForSnip = true;
        ApplyNotchVisibility();
        await Task.Delay(300);

        _awaitingScreenshot = true;
        _screenshotRequestedAt = DateTime.Now;

        try
        {
            if (_settings.ScreenshotMethod == "sharex")
            {
                // Press ShareX's capture shortcut. ShareX copies the capture to the clipboard,
                // which is where we pick it up and put it on the shelf.
                KeySender.Send(_settings.ScreenshotKeys);
            }
            else
            {
                // Windows' own snipping overlay: drag a box, or pick window / full screen
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            }
        }
        catch
        {
            _awaitingScreenshot = false;
            _hiddenForSnip = false;
            ApplyNotchVisibility();
            ShowHud("", null, "Snipping Tool isn't available", "", TimeSpan.FromSeconds(3), LowRed);
            return;
        }

        // The overlay freezes the screen as it opens, so the notch can come back now
        await Task.Delay(1200);
        _hiddenForSnip = false;
        ApplyNotchVisibility();
    }

    private void SaveScreenshotToShelf(IDataObject data)
    {
        _lastShotAt = DateTime.Now; // so the screenshot pop-up doesn't announce it again
        try
        {
            BitmapSource? image = null;

            // Prefer the PNG copy (keeps transparency and colours exact)
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                png.Position = 0;
                var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image = decoder.Frames[0];
            }
            image ??= Clipboard.GetImage();
            if (image == null) return;

            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinNotch Screenshots");
            Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");

            using (var stream = File.Create(file))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }

            AddToShelf(file);
            Peek(force: true); // pop open so you can see it on the shelf
        }
        catch
        {
            ShowHud("", null, "Couldn't save the screenshot", "", TimeSpan.FromSeconds(3), LowRed);
        }
    }

    // ---------- Small message at the top of the open notch ----------

    private void ShowTopMessage(string text, Brush colour, double seconds)
    {
        TopMessage.Text = text;
        TopMessage.Foreground = colour;
        _topMessageTimer?.Stop();
        _topMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _topMessageTimer.Tick += (_, _) =>
        {
            _topMessageTimer?.Stop();
            if (TopMessage.Text == text) TopMessage.Text = "";
        };
        _topMessageTimer.Start();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
