using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

// Screen recording: choose an area (or the whole screen), record to MP4, and it lands on the shelf.
// While recording the notch shows a red dot and the time; press the record button again to stop.
public partial class MainWindow
{
    private ScreenRecorder? _recorder;
    private readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _recTimerReady;

    private bool Recording => _recorder != null;

    private async void RecordTool_Click(object sender, RoutedEventArgs e)
    {
        if (Recording)
        {
            _recorder!.Stop();
            return;
        }

        SetExpanded(false);
        _hiddenForSnip = true;
        ApplyNotchVisibility();
        await Task.Delay(250);

        Int32Rect? area;
        try
        {
            area = RegionPicker.Pick(AccentOrange);
        }
        finally
        {
            _hiddenForSnip = false;
            ApplyNotchVisibility();
        }
        if (area is not { } a || a.Width < 16 || a.Height < 16) return;

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "WinNotch Recordings");
        string file = Path.Combine(folder, $"Recording {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        var recorder = new ScreenRecorder(a, _settings.RecordFps, _settings.RecordCursor, file);
        _recorder = recorder;

        // Keep the notch itself out of the video, and keep the PC awake
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
        ApplyExecutionState();
        StartRecordingUi();

        try
        {
            await recorder.RecordAsync();
            FinishRecording(recorder, null);
        }
        catch (Exception ex)
        {
            FinishRecording(recorder, ex);
        }
    }

    private void StartRecordingUi()
    {
        if (!_recTimerReady)
        {
            _recTimerReady = true;
            _recTimer.Tick += (_, _) =>
            {
                if (_recorder == null) return;
                var t = _recorder.Elapsed;
                CompactRecText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
                CompactRecDot.Opacity = CompactRecDot.Opacity > 0.5 ? 0.35 : 1; // blink
            };
        }
        CompactRecText.Text = "0:00";
        CompactRec.Visibility = Visibility.Visible;
        RecordToolButton.Content = ""; // stop
        RecordToolButton.Foreground = LowRed;
        RecordToolButton.ToolTip = "Stop recording";
        _recTimer.Start();
        Notify("", "Recording", "press ⏹ in the notch to stop", LowRed, 2.5);
    }

    private void FinishRecording(ScreenRecorder recorder, Exception? error)
    {
        _recorder = null;
        _recTimer.Stop();
        CompactRec.Visibility = Visibility.Collapsed;
        RecordToolButton.Content = "";
        RecordToolButton.ClearValue(ForegroundProperty);
        RecordToolButton.ToolTip = "Record the screen (MP4 to the shelf)";
        SetWindowDisplayAffinity(_hwnd, 0);
        ApplyExecutionState();

        if (error != null || !File.Exists(recorder.File) || new FileInfo(recorder.File).Length < 1000)
        {
            try { if (File.Exists(recorder.File)) File.Delete(recorder.File); } catch { }
            Notify("", "Recording failed", error?.Message ?? "", LowRed, 5);
            return;
        }

        AddToShelf(recorder.File);
        var length = recorder.Elapsed;
        Notify("", "Recording added to shelf", length.ToString(@"m\:ss"), ChargingGreen, 4, RecordingThumbnail(recorder));
    }

    private static BitmapSource? RecordingThumbnail(ScreenRecorder recorder)
    {
        try
        {
            if (recorder.LastFrame is not { } bgra) return null;
            var full = BitmapSource.Create(recorder.Width, recorder.Height, 96, 96, PixelFormats.Bgr32, null, bgra, recorder.Width * 4);
            double scale = 120.0 / recorder.Width;
            var small = new TransformedBitmap(full, new ScaleTransform(scale, scale));
            small.Freeze();
            return small;
        }
        catch
        {
            return null;
        }
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
