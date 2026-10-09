using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinNotch;

// Batch actions for everything on the shelf: rename all with a pattern, and convert / resize / compress all images.
public partial class MainWindow
{
    private void AddBatchItems(ContextMenu menu)
    {
        if (_shelf.Count < 2) return;

        var group = new MenuItem { Header = $"All {_shelf.Count} items on the shelf", Tag = "image" };
        var rename = new MenuItem { Header = "Rename all…" };
        rename.Click += (_, _) => BatchRename();
        group.Items.Add(rename);

        var images = _shelf.Where(s => IsImage(s.FullPath)).Select(s => s.FullPath).ToList();
        if (_settings.ImageActions && images.Count >= 2)
        {
            group.Items.Add(new Separator());
            void Add(string header, Action<string> action)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => BatchImages(images, action);
                group.Items.Add(mi);
            }
            Add($"Convert {images.Count} images to PNG", p => SaveImage(p, Load(p), ".png", ""));
            Add($"Convert {images.Count} images to JPG", p => SaveImage(p, Load(p), ".jpg", ""));
            Add($"Resize {images.Count} images to {_settings.ImageResizeWidth}px wide",
                p => SaveImage(p, Resize(Load(p), _settings.ImageResizeWidth), KeepFormat(p), $"-{_settings.ImageResizeWidth}w"));
            Add($"Compress {images.Count} images (JPG {_settings.JpegQuality}%)", p => SaveImage(p, Load(p), ".jpg", "-small"));
        }

        menu.Items.Add(new Separator { Tag = "image" });
        menu.Items.Add(group);
    }

    private void BatchImages(List<string> files, Action<string> action)
    {
        int done = 0, failed = 0;
        foreach (var f in files)
        {
            try { action(f); done++; }
            catch { failed++; }
        }
        ShowTopMessage(failed == 0 ? $"Done: {done} new images on the shelf" : $"{done} done, {failed} couldn't be changed",
                       failed == 0 ? ChargingGreen : LowRed, 4);
    }

    private void BatchRename()
    {
        var items = _shelf.ToList();
        string? baseName = AskRenamePattern(items.Count, Path.GetFileNameWithoutExtension(items[0].FullPath));
        if (baseName == null) return;

        int digits = Math.Max(2, items.Count.ToString().Length);
        int done = 0, failed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            string old = items[i].FullPath.TrimEnd('\\', '/');
            try
            {
                string dir = Path.GetDirectoryName(old)!;
                bool isDir = Directory.Exists(old);
                string ext = isDir ? "" : Path.GetExtension(old);
                string name = $"{baseName} {(i + 1).ToString().PadLeft(digits, '0')}";
                string target = Path.Combine(dir, name + ext);
                for (int n = 2; (File.Exists(target) || Directory.Exists(target)) && !target.Equals(old, StringComparison.OrdinalIgnoreCase); n++)
                    target = Path.Combine(dir, $"{name} ({n}){ext}");
                if (!target.Equals(old, StringComparison.OrdinalIgnoreCase))
                {
                    if (isDir) Directory.Move(old, target); else File.Move(old, target);
                }

                // Swap the shelf entry for the renamed file, in the same place
                int index = _shelf.IndexOf(items[i]);
                if (index >= 0) _shelf[index] = new ShelfItem { FullPath = target, Icon = GetIcon(target) };
                done++;
            }
            catch
            {
                failed++;
            }
        }
        ShowTopMessage(failed == 0 ? $"Renamed {done} items" : $"Renamed {done}, {failed} couldn't be renamed (in use?)",
                       failed == 0 ? ChargingGreen : LowRed, 4);
    }

    /// <summary>A small box asking for the new name. Returns null if cancelled.</summary>
    private string? AskRenamePattern(int count, string suggestion)
    {
        string? result = null;
        var dialog = new Window
        {
            Title = "Rename all",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"Rename {count} items", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var box = new TextBox
        {
            Text = suggestion,
            Margin = new Thickness(0, 12, 0, 6),
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)),
        };
        var preview = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void UpdatePreview()
        {
            string n = box.Text.Trim();
            int digits = Math.Max(2, count.ToString().Length);
            preview.Text = n.Length == 0 ? "Type a name" : $"{n} {"1".PadLeft(digits, '0')}, {n} {"2".PadLeft(digits, '0')}, … {n} {count.ToString().PadLeft(digits, '0')}  (extensions are kept)";
        }
        box.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        panel.Children.Add(box);
        panel.Children.Add(preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        var ok = new Button { Content = "Rename", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            string n = box.Text.Trim();
            if (n.Length == 0 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                preview.Text = "That name has characters Windows doesn't allow ( \\ / : * ? \" < > | )";
                return;
            }
            result = n;
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }
}
