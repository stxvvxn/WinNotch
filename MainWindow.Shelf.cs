using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

// Shelf (third page): drag files onto the notch to park them, drag them out again to drop them somewhere else.
public partial class MainWindow
{
    private readonly List<string> _shelf = new();
    private bool _shelfLoaded, _dragOver, _draggingOut;
    private readonly DispatcherTimer _dragLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _dragLeaveReady;
    private Point _shelfPressAt;
    private string? _shelfPressed;

    private static string ShelfFile => Path.Combine(AppSettings.Folder, "shelf.json");

    // ---------- Dragging files in ----------

    private void Pill_DragEnter(object sender, DragEventArgs e)
    {
        bool files = !_draggingOut && _settings.ShelfPage && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;

        _dragLeaveTimer.Stop();
        _hoverTimer.Stop();
        if (!_dragOver)
        {
            _dragOver = true;
            if (_settings.ShelfAutoOpen)
            {
                SetExpanded(true);
                ShowPage(2);
            }
            ShowDropHighlight();
        }
    }

    private void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && _settings.ShelfPage && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
        _dragLeaveTimer.Stop();
    }

    private void Pill_DragLeave(object sender, DragEventArgs e)
    {
        // Also fires when moving between things inside the notch, so wait a moment before deciding
        if (!_dragLeaveReady)
        {
            _dragLeaveReady = true;
            _dragLeaveTimer.Tick += (_, _) =>
            {
                _dragLeaveTimer.Stop();
                if (!_dragOver) return;
                _dragOver = false;
                ShowDropHighlight();
                if (!_hovering && !_typing) SetExpanded(false);
            };
        }
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void Pill_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragOver = false;
        _dragLeaveTimer.Stop();
        if (_draggingOut || !_settings.ShelfPage || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (var path in paths) AddToShelf(path);
        SetExpanded(true);
        ShowPage(2);
        ShowDropHighlight();
        SaveShelf();

        // Stay open a moment so you can see what landed, then tuck away if the mouse has gone
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromSeconds(1.5);
        _hoverTimer.Start();
    }

    private void ShowDropHighlight()
    {
        ShelfDrop.BorderBrush = _dragOver ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        ShelfDrop.BorderThickness = new Thickness(_dragOver ? 2 : 1);
    }

    private void AddToShelf(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (_shelf.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _shelf.Add(path);
        RefreshShelf();
    }

    // ---------- Showing it ----------

    private void RefreshShelf()
    {
        if (!_shelfLoaded) LoadShelf();
        _shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)); // moved or deleted elsewhere

        ShelfItems.Children.Clear();
        foreach (var path in _shelf) ShelfItems.Children.Add(BuildShelfItem(path));
        ShelfEmpty.Visibility = Vis(_shelf.Count == 0);
        ShelfClear.Visibility = Vis(_shelf.Count > 0);
        ShelfTitle.Text = _shelf.Count == 0 ? "SHELF" : $"SHELF · {_shelf.Count} item{(_shelf.Count == 1 ? "" : "s")}";
        AnimateHeight();
    }

    private FrameworkElement BuildShelfItem(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) name = path;

        var stack = new StackPanel { Width = 72 };
        stack.Children.Add(new Image
        {
            Source = ShelfIcon(path),
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4),
        });
        stack.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 68,
        });

        var tile = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2, 4, 2, 6),
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = path,
            Child = stack,
        };
        tile.MouseEnter += (_, _) => tile.Background = (Brush)FindResource("SurfaceHover");
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

        // Double-click opens; drag to take it out
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                WebTools.Open(path);
                e.Handled = true;
                return;
            }
            _shelfPressed = path;
            _shelfPressAt = e.GetPosition(this);
        };
        tile.PreviewMouseMove += (_, e) => ShelfItemMouseMove(tile, e);

        // Right-click menu, styled like the notch
        var menu = new ContextMenu { Style = (Style)FindResource("NotchMenu") };
        void Add(string glyph, string header, Action action, Brush? colour = null)
        {
            var mi = new MenuItem { Header = header, Tag = glyph, Style = (Style)FindResource("NotchMenuItem") };
            if (colour != null) mi.Foreground = colour;
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("\uE8E5", "Open", () => WebTools.Open(path));
        Add("\uE838", "Show in folder", () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } });
        Add("\uE8C8", "Copy path", () => { try { Clipboard.SetText(path); } catch { } });
        menu.Items.Add(new Separator { Style = (Style)FindResource("NotchMenuSeparator") });
        Add("\uE74D", "Remove from shelf", () => { _shelf.Remove(path); SaveShelf(); RefreshShelf(); },
            new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));

        // The menu is its own little window, so moving onto it would look like leaving the notch:
        // keep the notch open while it's showing
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            _hoverTimer.Stop();
            SetForegroundWindow(_hwnd); // so the menu closes when you click elsewhere
            BringMenuToFront(menu);
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!Pill.IsMouseOver)
            {
                _hovering = false;
                _hoverTimer.Stop();
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(600);
                _hoverTimer.Start();
            }
        };
        tile.ContextMenu = menu;
        return tile;
    }

    // The notch is "always on top", so make sure the menu sits above it
    private void BringMenuToFront(ContextMenu menu)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source)
            {
                const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
                SetWindowPos(source.Handle, new IntPtr(-1) /* topmost */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
        }), DispatcherPriority.Loaded);
    }

    // ---------- Dragging files out ----------

    private void ShelfItemMouseMove(FrameworkElement tile, MouseEventArgs e)
    {
        if (_shelfPressed == null || e.LeftButton != MouseButtonState.Pressed)
        {
            _shelfPressed = null;
            return;
        }
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _shelfPressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _shelfPressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        string path = _shelfPressed;
        _shelfPressed = null;
        _draggingOut = true;
        try
        {
            // Like dragging from File Explorer: moves on the same drive, copies to another
            // (hold Ctrl to copy, Shift to move)
            DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { }
        finally
        {
            _draggingOut = false;
        }

        if (_settings.ShelfRemoveAfterDrag || (!File.Exists(path) && !Directory.Exists(path)))
        {
            _shelf.Remove(path);
            SaveShelf();
        }
        RefreshShelf();
    }

    private void ShelfClear_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ClearShelf();

    public void ClearShelf()
    {
        _shelf.Clear();
        SaveShelf();
        RefreshShelf();
    }

    // ---------- Remembering it ----------

    private void LoadShelf()
    {
        _shelfLoaded = true;
        if (!_settings.ShelfRemember) return;
        try
        {
            if (!File.Exists(ShelfFile)) return;
            foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(ShelfFile)) ?? Array.Empty<string>())
                if ((File.Exists(path) || Directory.Exists(path)) && !_shelf.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _shelf.Add(path);
        }
        catch { }
    }

    private void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (_settings.ShelfRemember) File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf));
            else if (File.Exists(ShelfFile)) File.Delete(ShelfFile);
        }
        catch { }
    }

    // Pictures get a real thumbnail, everything else its normal Windows icon
    private ImageSource? ShelfIcon(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (_settings.ShelfThumbnails && ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".jfif")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 80;
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }
        return ShellIcon(path);
    }
}
