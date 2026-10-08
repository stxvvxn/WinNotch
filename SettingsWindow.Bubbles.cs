using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinNotch;

// Settings → Bubbles: a live preview of the open notch where you drag bubbles into place.
public partial class SettingsWindow
{
    private const double PreviewScale = 0.72;     // preview pixels per notch pixel
    private const double PreviewNotchWidth = 210; // narrower than the real notch so it fits; heights are true to life
    private const double TallHeight = 210, ShortHeight = 110, MiniHeight = 80;
    private const double DropDistance = 30;       // how close (preview pixels) a drop must be to a spot

    // The accent colour from Settings → Theme (kept up to date by ApplyAccentToWindow)
    private Brush Orange => (Brush)Resources["Accent"];
    private static readonly Brush SlotStroke = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private NotchLayout _previewLayout = NotchLayout.Tall;
    private BubbleSetting? _selected;

    private readonly List<(string Side, int Index, Point Centre)> _dropSpots = new();
    private double _trayTop;
    private Ellipse? _dropRing;

    private FrameworkElement? _dragging;
    private Point _dragStartMouse, _dragStartPos;
    private bool _dragMoved;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private double NotchHeight => _previewLayout switch
    {
        NotchLayout.Short => ShortHeight,
        NotchLayout.Mini => MiniHeight,
        _ => TallHeight,
    };
    private NotchLayout Editing => _previewLayout; // each size has its own layout

    // ---------- Drawing ----------

    private void DrawBubblePreview()
    {
        var all = _owner.Settings.Bubbles;
        double k = PreviewScale;
        double virtualWidth = BubbleCanvas.Width / k;
        double notchLeft = virtualWidth / 2 - PreviewNotchWidth / 2;
        double notchHeight = NotchHeight;
        int perSide = BubbleLayout.SideCapacity(notchHeight);

        BubbleCanvas.Children.Clear();
        _dropSpots.Clear();

        TallPreviewButton.Foreground = _previewLayout == NotchLayout.Tall ? Orange : Brushes.White;
        ShortPreviewButton.Foreground = _previewLayout == NotchLayout.Short ? Orange : Brushes.White;
        MiniPreviewButton.Foreground = _previewLayout == NotchLayout.Mini ? Orange : Brushes.White;
        CopyTallButton.Visibility = _previewLayout == NotchLayout.Tall ? Visibility.Collapsed : Visibility.Visible;

        // The open notch
        var notch = new Border
        {
            Width = PreviewNotchWidth * k,
            Height = notchHeight * k,
            Background = MainWindow.NotchBrushFor(_owner.Settings.NotchStyle),
            BorderBrush = SlotStroke,
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 16, 16),
            Child = new TextBlock
            {
                Text = _previewLayout switch
                {
                    NotchLayout.Short => "Open notch (short)",
                    NotchLayout.Mini => "Open notch (mini)",
                    _ => "Open notch (tall)",
                },
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Place(notch, notchLeft * k, 0);

        // Arrange exactly like the real notch does
        var visible = all.Where(b => b.IsShown(Editing)).ToList();
        var arranged = BubbleLayout.Arrange(visible, perSide, Editing);
        int bottomCount = arranged.Count(a => a.Side == BubbleSide.Bottom);
        int bottomSpots = bottomCount + 1; // one spare spot at the end to drop into

        // Empty spots down each side
        for (int i = 0; i < perSide; i++)
        {
            foreach (var side in new[] { BubbleSide.Left, BubbleSide.Right })
            {
                var (x, y) = BubbleLayout.Position(side, i, 0, notchLeft, PreviewNotchWidth, notchHeight);
                AddSpot(side == BubbleSide.Left ? "left" : "right", i, x, y, drawMarker: true);
            }
        }
        // Spots along the bottom (in the preview the row leaves room for one more)
        for (int i = 0; i < bottomSpots; i++)
        {
            var (x, y) = BubbleLayout.Position(BubbleSide.Bottom, i, bottomSpots, notchLeft, PreviewNotchWidth, notchHeight);
            AddSpot("bottom", i, x, y, drawMarker: i == bottomCount);
        }

        // The bubbles themselves
        foreach (var (bubble, side, index, overflow) in arranged)
        {
            var (x, y) = BubbleLayout.Position(side, index, side == BubbleSide.Bottom ? bottomSpots : 0,
                                               notchLeft, PreviewNotchWidth, notchHeight);
            var element = MakeBubble(bubble, overflow);
            Place(element, x * k, y * k);
        }

        // Hidden tray underneath
        _trayTop = (notchHeight + BubbleLayout.Gap + BubbleLayout.Size + 26) * k;
        var tray = new Rectangle
        {
            Width = BubbleCanvas.Width - 16,
            Height = BubbleLayout.Size * k + 26,
            RadiusX = 8,
            RadiusY = 8,
            Stroke = SlotStroke,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        Place(tray, 8, _trayTop - 4);
        var trayLabel = new TextBlock
        {
            Text = "HIDDEN · drag a bubble here to hide it",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        };
        Place(trayLabel, 16, _trayTop);

        int h = 0;
        foreach (var bubble in all.Where(b => !b.IsShown(Editing)))
        {
            Place(MakeBubble(bubble, overflow: false), 16 + h++ * (BubbleLayout.Size + BubbleLayout.Gap) * k, _trayTop + 16);
        }

        BubbleCanvas.Height = _trayTop + BubbleLayout.Size * k + 30;

        _dropRing = new Ellipse
        {
            Width = BubbleLayout.Size * k + 6,
            Height = BubbleLayout.Size * k + 6,
            Stroke = Orange,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        BubbleCanvas.Children.Add(_dropRing);

        ShowBubbleEditor();
    }

    private void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        BubbleCanvas.Children.Add(element);
    }

    private void AddSpot(string side, int index, double x, double y, bool drawMarker)
    {
        double k = PreviewScale, size = BubbleLayout.Size * k;
        _dropSpots.Add((side, index, new Point(x * k + size / 2, y * k + size / 2)));
        if (!drawMarker) return;
        Place(new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = SlotStroke,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
        }, x * k, y * k);
    }

    private FrameworkElement MakeBubble(BubbleSetting bubble, bool overflow)
    {
        double size = BubbleLayout.Size * PreviewScale;
        bool selected = ReferenceEquals(bubble, _selected);

        var circle = new Ellipse
        {
            Fill = MainWindow.NotchBrushFor(_owner.Settings.NotchStyle),
            Stroke = selected || overflow ? Orange : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = selected ? 2 : 1,
        };
        if (overflow && !selected) circle.StrokeDashArray = new DoubleCollection { 2, 2 };

        var element = new Grid
        {
            Width = size,
            Height = size,
            Tag = bubble,
            Cursor = Cursors.SizeAll,
            Opacity = bubble.IsShown(Editing) ? 1 : 0.55,
            ToolTip = overflow
                ? $"{bubble.Label} — no room on the side when the notch is short, so it sits here"
                : bubble.Label,
        };
        element.Children.Add(circle);
        element.Children.Add(new TextBlock
        {
            Text = int.TryParse(bubble.Icon, System.Globalization.NumberStyles.HexNumber, null, out int code)
                ? char.ConvertFromUtf32(code) : "?",
            FontFamily = IconFont,
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        element.MouseLeftButtonDown += Bubble_MouseDown;
        element.MouseMove += Bubble_MouseMove;
        element.MouseLeftButtonUp += Bubble_MouseUp;
        return element;
    }

    // ---------- Dragging ----------

    private void Bubble_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        _dragging = element;
        _dragMoved = false;
        _dragStartMouse = e.GetPosition(BubbleCanvas);
        _dragStartPos = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));
        Panel.SetZIndex(element, 10);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void Bubble_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging == null || !ReferenceEquals(sender, _dragging) || !_dragging.IsMouseCaptured) return;
        var delta = e.GetPosition(BubbleCanvas) - _dragStartMouse;
        if (!_dragMoved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 4) return;
        _dragMoved = true;

        Canvas.SetLeft(_dragging, _dragStartPos.X + delta.X);
        Canvas.SetTop(_dragging, _dragStartPos.Y + delta.Y);

        // Show where it would land
        var centre = CentreOf(_dragging);
        var spot = NearestSpot(centre);
        if (_dropRing == null) return;
        if (centre.Y >= _trayTop - 6 || spot == null)
        {
            _dropRing.Visibility = Visibility.Collapsed;
        }
        else
        {
            Canvas.SetLeft(_dropRing, spot.Value.Centre.X - _dropRing.Width / 2);
            Canvas.SetTop(_dropRing, spot.Value.Centre.Y - _dropRing.Height / 2);
            _dropRing.Visibility = Visibility.Visible;
        }
    }

    private void Bubble_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == null || !ReferenceEquals(sender, _dragging)) return;
        var element = _dragging;
        _dragging = null;
        element.ReleaseMouseCapture();
        if (element.Tag is not BubbleSetting bubble) return;

        if (!_dragMoved)
        {
            _selected = bubble; // a click: select it for editing
            DrawBubblePreview();
            return;
        }

        Drop(bubble, CentreOf(element));
    }

    private static Point CentreOf(FrameworkElement element) =>
        new(Canvas.GetLeft(element) + element.Width / 2, Canvas.GetTop(element) + element.Height / 2);

    private (string Side, int Index, Point Centre)? NearestSpot(Point centre)
    {
        if (_dropSpots.Count == 0) return null;
        var best = _dropSpots.OrderBy(s => (s.Centre - centre).Length).First();
        return (best.Centre - centre).Length <= DropDistance ? best : null;
    }

    private void Drop(BubbleSetting bubble, Point centre)
    {
        var all = _owner.Settings.Bubbles;

        var layout = Editing; // only the layout you're looking at changes

        if (centre.Y >= _trayTop - 6)
        {
            bubble.SetShown(layout, false); // dropped in the hidden tray
        }
        else if (NearestSpot(centre) is { } spot)
        {
            bubble.SetShown(layout, true);
            // Insert into that side's order (others shift along to make room)
            var line = all.Where(b => b.IsShown(layout) && !ReferenceEquals(b, bubble) && b.SideIn(layout) == spot.Side)
                          .OrderBy(b => b.SlotIn(layout)).ToList();
            line.Insert(Math.Clamp(spot.Index, 0, line.Count), bubble);
            for (int i = 0; i < line.Count; i++) line[i].Place(layout, spot.Side, i);
        }
        // else: dropped nowhere useful, so it snaps back

        BubbleLayout.Renumber(all, layout);
        _selected = bubble;
        _owner.SettingsChanged();
        DrawBubblePreview();
    }

    // ---------- Tall / short preview ----------

    private void PreviewTall_Click(object sender, RoutedEventArgs e) => ShowPreview(NotchLayout.Tall);
    private void PreviewShort_Click(object sender, RoutedEventArgs e) => ShowPreview(NotchLayout.Short);
    private void PreviewMini_Click(object sender, RoutedEventArgs e) => ShowPreview(NotchLayout.Mini);

    private void ShowPreview(NotchLayout layout)
    {
        _previewLayout = layout;
        DrawBubblePreview();
    }

    // Make the layout you're looking at the same as the tall one
    private void CopyTall_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _owner.Settings.Bubbles)
            b.CopyLayout(NotchLayout.Tall, _previewLayout);
        _owner.SettingsChanged();
        DrawBubblePreview();
    }

    // ---------- Editing the selected bubble ----------

    private void ShowBubbleEditor()
    {
        bool any = _selected != null;
        BubbleEditor.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        BubbleHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (_selected == null) return;
        if (!BubbleLabelBox.IsKeyboardFocused) BubbleLabelBox.Text = _selected.Label;
        if (!BubbleTargetBox.IsKeyboardFocused) BubbleTargetBox.Text = _selected.Target;
        BuildIconPicker();
    }

    // ---------- Icons ----------

    private static readonly string[] IconChoices =
    {
        "E8B7", "E8A5", "E8B9", "E8D6", "E714", "E896", "E7F4", "E8F1",
        "E722", "E756", "E943", "E8EF", "E713", "E70B", "E74D", "E715",
        "E787", "E774", "E7FC", "E8BD", "E80F", "E719", "E716", "E734",
        "E945", "E7F8", "E9D9", "E8A7",
    };

    private void BuildIconPicker()
    {
        BubbleIconPicker.Children.Clear();
        if (_selected == null) return;
        foreach (var icon in IconChoices)
        {
            bool current = string.Equals(icon, _selected.Icon, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = char.ConvertFromUtf32(int.Parse(icon, System.Globalization.NumberStyles.HexNumber)),
                FontFamily = IconFont,
                FontSize = 14,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 4, 4),
                Foreground = current ? Orange : Brushes.White,
                Tag = icon,
            };
            button.Click += (_, _) =>
            {
                if (_selected == null) return;
                _selected.Icon = icon;
                _owner.SettingsChanged();
                DrawBubblePreview();
            };
            BubbleIconPicker.Children.Add(button);
        }
    }

    // ---------- Adding and removing ----------

    private static IEnumerable<BubbleSetting> Presets()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return new() { Key = "downloads", Label = "Downloads", Icon = "E896", Target = System.IO.Path.Combine(home, "Downloads") };
        yield return new() { Key = "desktop", Label = "Desktop", Icon = "E7F4", Target = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        yield return new() { Key = "videos", Label = "Videos", Icon = "E714", Target = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        yield return new() { Key = "thispc", Label = "This PC", Icon = "E7F8", Target = "shell:MyComputerFolder" };
        yield return new() { Key = "recycle", Label = "Recycle Bin", Icon = "E74D", Target = "shell:RecycleBinFolder" };
        yield return new() { Key = "calculator", Label = "Calculator", Icon = "E8EF", Target = "calc.exe" };
        yield return new() { Key = "notepad", Label = "Notepad", Icon = "E70B", Target = "notepad.exe" };
        yield return new() { Key = "taskmgr", Label = "Task Manager", Icon = "E9D9", Target = "taskmgr.exe" };
        yield return new() { Key = "settings", Label = "Windows Settings", Icon = "E713", Target = "ms-settings:" };
        yield return new() { Key = "mail", Label = "New email", Icon = "E715", Target = "mailto:" };
        yield return new() { Key = "claude", Label = "Claude", Icon = "E8BD", Target = "claude://claude.ai/new" };
        yield return new() { Key = "steam", Label = "Steam library", Icon = "E7FC", Target = "steam://open/games" };
    }

    // Shows a menu of ready-made bubbles, plus "Custom…"
    private void AddBubble_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddBubbleButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var preset in Presets())
        {
            var item = new MenuItem
            {
                Header = preset.Label,
                Icon = new TextBlock
                {
                    Text = char.ConvertFromUtf32(int.Parse(preset.Icon, System.Globalization.NumberStyles.HexNumber)),
                    FontFamily = IconFont,
                },
            };
            var chosen = preset;
            item.Click += (_, _) => AddBubble(chosen);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Custom bubble…" };
        custom.Click += (_, _) => AddBubble(new BubbleSetting { Label = "New bubble", Icon = "E8A7", Target = "" });
        menu.Items.Add(custom);
        menu.IsOpen = true;
    }

    private void AddBubble(BubbleSetting bubble)
    {
        var all = _owner.Settings.Bubbles;
        // Each bubble needs its own key, even if you add the same preset twice
        bubble.Key = $"{(string.IsNullOrEmpty(bubble.Key) ? "custom" : bubble.Key)}-{Guid.NewGuid():N}";

        // Add it to the end of the bottom row in both layouts
        all.Add(bubble);
        foreach (var layout in BubbleLayout.All)
        {
            bubble.SetShown(layout, true);
            bubble.Place(layout, "bottom", 999);
            BubbleLayout.Renumber(all, layout);
        }

        _selected = bubble;
        _owner.SettingsChanged();
        DrawBubblePreview();
        if (string.IsNullOrEmpty(bubble.Target)) BubbleTargetBox.Focus(); // custom: type what it opens
    }

    private void RemoveBubble_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var all = _owner.Settings.Bubbles;
        all.Remove(_selected);
        foreach (var layout in BubbleLayout.All) BubbleLayout.Renumber(all, layout);
        _selected = null;
        _owner.SettingsChanged();
        DrawBubblePreview();
    }

    private void BubbleFields_Commit(object sender, RoutedEventArgs e) => CommitBubbleFields();

    private void BubbleFields_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitBubbleFields();
    }

    private void CommitBubbleFields()
    {
        if (_selected == null) return;
        string label = BubbleLabelBox.Text.Trim();
        string target = BubbleTargetBox.Text.Trim();
        bool changed = false;

        if (label.Length > 0 && label != _selected.Label) { _selected.Label = label; changed = true; }
        if (target != _selected.Target) { _selected.Target = target; changed = true; }
        if (!changed) return;

        _owner.SettingsChanged();

        // Update the tooltip in place (redrawing here could swallow a click on another bubble)
        foreach (var child in BubbleCanvas.Children.OfType<FrameworkElement>())
            if (ReferenceEquals(child.Tag, _selected)) child.ToolTip = _selected.Label;
    }

    private void BubbleBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Choose the folder for {_selected.Label}" };
        if (System.IO.Directory.Exists(_selected.Target)) dialog.InitialDirectory = _selected.Target;
        if (dialog.ShowDialog(this) == true)
        {
            BubbleTargetBox.Text = dialog.FolderName;
            CommitBubbleFields();
        }
    }
}
