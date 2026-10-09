using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinNotch;

// Shelf image actions: right-click an image on the shelf to convert, resize or compress it.
// The new file is saved beside the original (or in Pictures\WinNotch) and added to the shelf.
public partial class MainWindow
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".jfif" };

    private static bool IsImage(string path) =>
        File.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Adds (or removes) the image items each time the right-click menu opens
    private void ShelfItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: ShelfItem item }) return;

        // Clear out the items from last time
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(x => Equals(x.Tag, "image")).ToList())
            menu.Items.Remove(old);

        // Whole-shelf actions (rename all, convert all images...) go at the bottom
        AddBatchItems(menu);

        if (!_settings.ImageActions || !IsImage(item.FullPath)) return;

        int at = 0;
        void Add(string header, Action<string> action)
        {
            var mi = new MenuItem { Header = header, Tag = "image", DataContext = item };
            mi.Click += (_, _) => RunImageAction(item.FullPath, action);
            menu.Items.Insert(at++, mi);
        }

        string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
        if (ext != ".png") Add("Convert to PNG", p => SaveImage(p, Load(p), ".png", ""));
        if (ext is not (".jpg" or ".jpeg")) Add("Convert to JPG", p => SaveImage(p, Load(p), ".jpg", ""));
        Add($"Resize to {_settings.ImageResizeWidth}px wide", p => SaveImage(p, Resize(Load(p), _settings.ImageResizeWidth), KeepFormat(p), $"-{_settings.ImageResizeWidth}w"));
        Add($"Compress (JPG {_settings.JpegQuality}%)", p => SaveImage(p, Load(p), ".jpg", "-small"));
        Add("Copy image", p => CopyImage(p));
        menu.Items.Insert(at, new Separator { Tag = "image" });
    }

    private void RunImageAction(string path, Action<string> action)
    {
        try
        {
            action(path);
        }
        catch (Exception ex)
        {
            ShowTopMessage($"Couldn't do that: {ex.Message}", LowRed, 4);
        }
    }

    private static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource Resize(BitmapSource source, int width)
    {
        if (width <= 0 || source.PixelWidth <= width) return source; // never upscale
        double scale = (double)width / source.PixelWidth;
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    // Formats WPF can write; anything else becomes PNG
    private static string KeepFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" => ".jpg",
        ".bmp" => ".bmp",
        ".gif" => ".gif",
        ".tif" or ".tiff" => ".tif",
        _ => ".png",
    };

    private void SaveImage(string original, BitmapSource image, string ext, string suffix)
    {
        BitmapEncoder encoder = ext switch
        {
            ".jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(_settings.JpegQuality, 10, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        // JPG has no transparency: flatten onto white
        if (ext == ".jpg" && image.Format != PixelFormats.Bgr24 && image.Format != PixelFormats.Bgr32)
            image = FlattenOnWhite(image);

        encoder.Frames.Add(BitmapFrame.Create(image));

        string folder = _settings.ImageOutput == "folder"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinNotch")
            : Path.GetDirectoryName(original)!;
        Directory.CreateDirectory(folder);

        string name = Path.GetFileNameWithoutExtension(original) + suffix;
        string target = Path.Combine(folder, name + ext);
        for (int n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{name} ({n}){ext}");

        using (var stream = File.Create(target))
            encoder.Save(stream);

        AddToShelf(target);
        long kb = new FileInfo(target).Length / 1024;
        ShowTopMessage($"Saved {Path.GetFileName(target)} · {kb:N0} KB", ChargingGreen, 3);
    }

    private static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(image, rect);
        }
        var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    private void CopyImage(string path)
    {
        Clipboard.SetImage(Load(path));
        ShowTopMessage("Image copied", ChargingGreen, 2);
    }
}
