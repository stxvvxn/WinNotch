using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WinNotch;

/// <summary>A file or folder sitting on the shelf. The shelf holds a reference, not a copy.</summary>
public sealed class ShelfItem
{
    public string FullPath { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string Name
    {
        get
        {
            string name = System.IO.Path.GetFileName(FullPath.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? FullPath : name;
        }
    }
}

// Shelf feature: drag files onto the notch to park them, drag them out again to drop them somewhere else
public partial class MainWindow
{
    private readonly ObservableCollection<ShelfItem> _shelf = new();
    private bool _dragOver;       // something is being dragged over the notch right now
    private bool _draggingOut;    // we're dragging an item out of the shelf
    private Point _dragStart;
    private ShelfItem? _pressedItem;

    private static readonly string ShelfFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "shelf.json");

    private bool ShelfVisible => _shelf.Count > 0 || _dragOver;

    private void InitShelf()
    {
        ShelfItems.ItemsSource = _shelf;
        LoadShelf();
        _shelf.CollectionChanged += (_, _) =>
        {
            SaveShelf();
            UpdateShelfLayout();
        };
        UpdateShelfLayout();
    }

    private void UpdateShelfLayout()
    {
        ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearShelfButton.Visibility = _shelf.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfArea.BorderBrush = new SolidColorBrush(_dragOver
            ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        RefreshSections();
    }

    // ---------- Dragging files IN ----------

    private void Pill_DragEnter(object sender, DragEventArgs e)
    {
        if (_draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _collapseTimer.Stop();

        if (!_dragOver)
        {
            _dragOver = true;
            _openTool = null; // show the shelf, not a tool panel
            SetExpanded(true);
            UpdateShelfLayout();
        }
    }

    private void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
        _collapseTimer.Stop();
    }

    private void Pill_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between elements inside the notch,
        // so wait a moment before deciding the drag has really left
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(350);
        _collapseTimer.Tick -= EndDragOverOnTimer;
        _collapseTimer.Tick += EndDragOverOnTimer;
        _collapseTimer.Start();
    }

    private void EndDragOverOnTimer(object? sender, EventArgs e)
    {
        _collapseTimer.Tick -= EndDragOverOnTimer;
        if (_dragOver)
        {
            _dragOver = false;
            UpdateShelfLayout();
            if (!_hovering) SetExpanded(false);
        }
    }

    private void Pill_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragOver = false;

        if (!_draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths) AddToShelf(path);
        }

        UpdateShelfLayout();

        // Stay open briefly so you can see what landed, then tuck away
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromSeconds(1.5);
        _collapseTimer.Start();
    }

    private void AddToShelf(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (_shelf.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;
        _shelf.Add(new ShelfItem { FullPath = path, Icon = GetIcon(path) });
    }

    // ---------- Dragging files OUT ----------

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ShelfItem;
        if (item == null) return;

        if (e.ClickCount == 2)
        {
            OpenPath(item.FullPath);
            e.Handled = true;
            return;
        }

        _pressedItem = item;
        _dragStart = e.GetPosition(this);
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem == null || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedItem = null;
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _pressedItem;
        _pressedItem = null;

        var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
        _draggingOut = true;
        try
        {
            // Same as dragging from File Explorer: moves on the same drive, copies to another drive.
            // Hold Ctrl while dropping to force a copy, Shift to force a move.
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { }
        finally
        {
            _draggingOut = false;
        }

        // If the file was moved somewhere else, it no longer exists here, so take it off the shelf
        if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
            _shelf.Remove(item);
    }

    private void ShelfScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShelfScroller.ScrollToHorizontalOffset(ShelfScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // ---------- Right-click menu & clear ----------

    private static ShelfItem? ItemFromMenu(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

    private void ShelfOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item) OpenPath(item.FullPath);
    }

    private void ShelfShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is not { } item) return;
        try { Process.Start("explorer.exe", $"/select,\"{item.FullPath}\""); } catch { }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item) _shelf.Remove(item);
    }

    private void ClearShelf_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---------- Remember the shelf between restarts ----------

    private void LoadShelf()
    {
        try
        {
            if (!File.Exists(ShelfFile)) return;
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ShelfFile)) ?? Array.Empty<string>();
            foreach (var path in paths) AddToShelf(path); // skips anything that's since been deleted
        }
        catch { }
    }

    private void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile)!);
            File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf.Select(i => i.FullPath).ToArray()));
        }
        catch { }
    }

    // ---------- File icons ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private static ImageSource? GetIcon(string path)
    {
        // Pictures get a real thumbnail
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 72;
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }

        // Everything else gets its normal Windows icon
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
