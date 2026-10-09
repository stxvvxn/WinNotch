using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace WinNotch;

/// <summary>
/// Records an area of the screen to an MP4 (H.264, no sound) using only what's built into Windows:
/// frames are grabbed from the screen and handed to the Windows video encoder as they're needed.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly Int32Rect _area;
    private readonly int _fps;
    private readonly bool _cursor;
    private readonly int _width, _height;
    private readonly Stopwatch _clock = new();
    private volatile bool _stop;
    private byte[]? _bgra;

    public string File { get; }
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>The last frame, for a thumbnail (BGRA, Width × Height).</summary>
    public byte[]? LastFrame => _bgra;
    public int Width => _width;
    public int Height => _height;

    public ScreenRecorder(Int32Rect area, int fps, bool cursor, string file)
    {
        // The encoder needs even sizes
        _area = area;
        _width = Math.Max(16, area.Width & ~1);
        _height = Math.Max(16, area.Height & ~1);
        _fps = Math.Clamp(fps, 5, 60);
        _cursor = cursor;
        File = file;
    }

    public void Stop() => _stop = true;

    /// <summary>Records until Stop() is called, then finishes writing the file.</summary>
    public async Task RecordAsync()
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)_width, (uint)_height);
        props.FrameRate.Numerator = (uint)_fps;
        props.FrameRate.Denominator = 1;

        var source = new MediaStreamSource(new VideoStreamDescriptor(props))
        {
            BufferTime = TimeSpan.Zero,
            CanSeek = false,
        };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)_width;
        profile.Video.Height = (uint)_height;
        profile.Video.FrameRate.Numerator = (uint)_fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp((long)_width * _height * _fps / 8, 1_000_000, 40_000_000);

        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        await using var output = new FileStream(File, FileMode.Create, FileAccess.ReadWrite);
        using var stream = output.AsRandomAccessStream();

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Windows can't record video here ({prepared.FailureReason})");

        _clock.Start();
        await prepared.TranscodeAsync();
        _clock.Stop();
    }

    // The encoder asks for the next frame: wait until it's due, grab the screen, hand it over
    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (_stop)
        {
            args.Request.Sample = null; // end of the video
            return;
        }

        var request = args.Request;
        var deferral = request.GetDeferral();
        Task.Run(async () =>
        {
            try
            {
                var frameTime = TimeSpan.FromSeconds(1.0 / _fps);
                // Don't run ahead of real time
                var due = _lastTimestamp + frameTime;
                var wait = due - _clock.Elapsed;
                if (wait > TimeSpan.Zero && _sampleCount > 0) await Task.Delay(wait);

                if (_stop)
                {
                    request.Sample = null;
                    return;
                }

                byte[] nv12 = CaptureNv12();
                var timestamp = _sampleCount == 0 ? TimeSpan.Zero : _clock.Elapsed;
                if (timestamp <= _lastTimestamp && _sampleCount > 0) timestamp = _lastTimestamp + TimeSpan.FromMilliseconds(1);
                var sample = MediaStreamSample.CreateFromBuffer(nv12.AsBuffer(), timestamp);
                sample.Duration = frameTime;
                request.Sample = sample;
                _lastTimestamp = timestamp;
                _sampleCount++;
            }
            catch
            {
                request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private TimeSpan _lastTimestamp;
    private long _sampleCount;

    // ---------- Grabbing the screen ----------

    private byte[] CaptureNv12()
    {
        using var bmp = new System.Drawing.Bitmap(_width, _height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(_area.X, _area.Y, 0, 0, new System.Drawing.Size(_width, _height),
                             System.Drawing.CopyPixelOperation.SourceCopy);
            if (_cursor) DrawCursor(g);
        }

        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, _width, _height),
                                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var bgra = _bgra is { Length: var len } && len == _width * _height * 4 ? _bgra : new byte[_width * _height * 4];
        try
        {
            for (int y = 0; y < _height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * _width * 4, _width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        _bgra = bgra;
        return ToNv12(bgra, _width, _height);
    }

    private void DrawCursor(System.Drawing.Graphics g)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || info.flags != 1 /* showing */) return;
        int x = info.ptScreenPos.X - _area.X, y = info.ptScreenPos.Y - _area.Y;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            x -= icon.xHotspot;
            y -= icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        if (x < -64 || y < -64 || x > _width || y > _height) return;
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>BGRA → NV12 (what the video encoder wants), BT.601.</summary>
    private static byte[] ToNv12(byte[] bgra, int w, int h)
    {
        var nv12 = new byte[w * h * 3 / 2];
        int uv = w * h;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4, yRow = y * w;
            bool chromaRow = (y & 1) == 0;
            int uvRow = uv + (y / 2) * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                nv12[yRow + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                if (chromaRow && (x & 1) == 0)
                {
                    nv12[uvRow + x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uvRow + x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }
        return nv12;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
