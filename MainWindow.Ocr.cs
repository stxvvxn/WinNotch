using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WinNotch;

// Copy text from the screen: snip an area and its text goes to the clipboard,
// using the text recognition built into Windows (works offline, in your Windows display languages).
public partial class MainWindow
{
    private bool _awaitingOcr;
    private DateTime _ocrRequestedAt;

    private async void OcrTool_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
        _hiddenForSnip = true;
        ApplyNotchVisibility();
        await Task.Delay(300);

        _awaitingOcr = true;
        _ocrRequestedAt = DateTime.Now;
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch
        {
            _awaitingOcr = false;
            Notify("", "Snipping Tool isn't available", "", LowRed, 3);
        }
        await Task.Delay(1200);
        _hiddenForSnip = false;
        ApplyNotchVisibility();
    }

    /// <summary>Called by the clipboard watcher: true if it was the snip we asked for.</summary>
    private bool HandleOcrSnip(IDataObject data)
    {
        if (!_awaitingOcr || DateTime.Now - _ocrRequestedAt > TimeSpan.FromMinutes(2)) return false;
        if (!(data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap))) return false;
        _awaitingOcr = false;
        _lastShotAt = DateTime.Now; // the Snipping Tool may also save it; don't announce that as a screenshot

        var image = ReadClipboardImage(data);
        if (image != null) _ = RunOcrAsync(image);
        return true;
    }

    private async Task RunOcrAsync(BitmapSource image)
    {
        try
        {
            string? text = await RecognizeTextAsync(image);
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify("", "No text found", "", LowRed, 3);
                return;
            }
            if (!TrySetClipboardText(text))
            {
                Notify("", "Couldn't copy the text", "", LowRed, 3);
                return;
            }
            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            Notify("", "Text copied", words == 1 ? "1 word" : $"{words} words", ChargingGreen, 3);
        }
        catch (Exception ex)
        {
            Notify("", "Text recognition failed", ex.Message, LowRed, 4);
        }
    }

    private static async Task<string?> RecognizeTextAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null) throw new InvalidOperationException("No text recognition language is installed");

        // Small snips read better a bit bigger; huge ones must fit the engine's limit
        double scale = 1;
        int longest = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longest < 800) scale = Math.Min(3, 800.0 / longest);
        if (longest * scale > OcrEngine.MaxImageDimension) scale = (double)OcrEngine.MaxImageDimension / longest;
        if (Math.Abs(scale - 1) > 0.01)
        {
            var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            scaled.Freeze();
            image = scaled;
        }

        // WPF image → PNG bytes → Windows image
        byte[] png;
        using (var ms = new MemoryStream())
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(ms);
            png = ms.ToArray();
        }
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }
}
