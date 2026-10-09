using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinNotch;

// Settings → Notch → Buttons at the top: show / hide each button and change their order
// (drag a row, or use its arrows). A preview shows the bar as it'll look.
public partial class SettingsWindow
{
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private TopButtonSetting? _draggingTop;
    private Point _topDragStart;

    private void BuildTopButtonsEditor()
    {
        var s = _owner.Settings;
        ScreenshotShelfBox.IsEnabled = s.ScreenshotPopup;
        TopButtonsList.Children.Clear();
        TopButtonsPreview.Children.Clear();

        for (int i = 0; i < s.TopButtons.Count; i++)
        {
            var item = s.TopButtons[i];
            var info = TopButtonSetting.All.FirstOrDefault(a => a.Key == item.Key);
            if (info.Key == null) continue;

            // Preview icon
            if (item.Shown)
                TopButtonsPreview.Children.Add(new TextBlock
                {
                    Text = info.Icon,
                    FontFamily = SymbolFont,
                    FontSize = 14,
                    Foreground = Brushes.White,
                    Width = 26,
                    TextAlignment = TextAlignment.Center,
                    ToolTip = info.Name,
                });

            // Row: grip, tick box with icon + name, up / down
            var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 1), Tag = item, AllowDrop = true };
            row.PreviewMouseLeftButtonDown += TopRow_MouseDown;
            row.PreviewMouseMove += TopRow_MouseMove;
            row.DragOver += TopRow_DragOver;
            row.Drop += TopRow_Drop;

            var down = ArrowButton("", "Move right", i < s.TopButtons.Count - 1, () => MoveTop(item, +1));
            var up = ArrowButton("", "Move left", i > 0, () => MoveTop(item, -1));
            DockPanel.SetDock(down, Dock.Right);
            DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down);
            row.Children.Add(up);

            var grip = new TextBlock
            {
                Text = "", // gripper
                FontFamily = SymbolFont,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to move",
            };
            DockPanel.SetDock(grip, Dock.Left);
            row.Children.Add(grip);

            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = info.Icon, FontFamily = SymbolFont, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = info.Name, VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox { Content = label, IsChecked = item.Shown, Margin = new Thickness(0, 3, 0, 3) };
            box.Click += (_, _) =>
            {
                item.Shown = box.IsChecked == true;
                SaveTopButtons();
            };
            row.Children.Add(box);

            TopButtonsList.Children.Add(row);
        }
    }

    private Button ArrowButton(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = SymbolFont, FontSize = 11 },
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void MoveTop(TopButtonSetting item, int by)
    {
        var list = _owner.Settings.TopButtons;
        int from = list.IndexOf(item);
        int to = Math.Clamp(from + by, 0, list.Count - 1);
        if (from < 0 || from == to) return;
        list.RemoveAt(from);
        list.Insert(to, item);
        SaveTopButtons();
    }

    private void SaveTopButtons()
    {
        _owner.SettingsChanged();
        BuildTopButtonsEditor();
    }

    private void ResetTopButtons_Click(object sender, RoutedEventArgs e)
    {
        var s = _owner.Settings;
        var shown = s.TopButtons.ToDictionary(b => b.Key, b => b.Shown);
        s.TopButtons = TopButtonSetting.All
            .Select(a => new TopButtonSetting { Key = a.Key, Shown = shown.GetValueOrDefault(a.Key, true) })
            .ToList();
        SaveTopButtons();
    }

    // ---------- Drag a row to move it ----------

    private void TopRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Not when clicking the tick box or arrows
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) { _draggingTop = null; return; }
        _draggingTop = (sender as FrameworkElement)?.Tag as TopButtonSetting;
        _topDragStart = e.GetPosition(this);
    }

    private void TopRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingTop == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - _topDragStart.Y) < 6 && Math.Abs(p.X - _topDragStart.X) < 6) return;

        var item = _draggingTop;
        _draggingTop = null;
        if (sender is FrameworkElement row) row.Opacity = 0.4;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(TopButtonSetting), item), DragDropEffects.Move);
        BuildTopButtonsEditor(); // restores the row (and shows any new order)
    }

    private void TopRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TopButtonSetting)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TopRow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TopButtonSetting)) is not TopButtonSetting dragged) return;
        if ((sender as FrameworkElement)?.Tag is not TopButtonSetting target || ReferenceEquals(dragged, target)) return;

        var list = _owner.Settings.TopButtons;
        int to = list.IndexOf(target);
        list.Remove(dragged);
        list.Insert(Math.Clamp(to, 0, list.Count), dragged);
        e.Handled = true;
        _owner.SettingsChanged();
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
