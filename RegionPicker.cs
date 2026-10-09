using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace WinNotch;

/// <summary>
/// Dims the screen under the mouse and lets you drag a box. Returns the area in screen pixels,
/// the whole screen if you just click, or null if you press Esc / right-click.
/// </summary>
public sealed class RegionPicker : Window
{
    private readonly System.Drawing.Rectangle _screen; // the monitor, in pixels
    private readonly Canvas _canvas = new();
    private readonly Rectangle _box;
    private readonly ShapePath _shade;
    private Point? _start;
    private Int32Rect? _result;

    private RegionPicker(System.Drawing.Rectangle screen, Brush accent)
    {
        _screen = screen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = Top = 0;
        Width = Height = 100; // real size set in pixels once the window exists

        // Everything outside the box is dimmed
        _shade = new ShapePath { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)) };
        _box = new Rectangle { Stroke = accent, StrokeThickness = 2, Visibility = Visibility.Collapsed };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x10, 0x10, 0x10)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock
            {
                Text = "Drag to choose an area · Click to record the whole screen · Esc to cancel",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_box);
        _canvas.Children.Add(hint);
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, new IntPtr(-1), _screen.Left, _screen.Top, _screen.Width, _screen.Height, 0x0040);
        };
        Loaded += (_, _) =>
        {
            UpdateShade(null);
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, (ActualWidth - hint.DesiredSize.Width) / 2);
            Canvas.SetTop(hint, 60);
            Activate();
            Focus();
        };

        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_canvas);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_start is not Point s) return;
            var r = new Rect(s, e.GetPosition(_canvas));
            Canvas.SetLeft(_box, r.X);
            Canvas.SetTop(_box, r.Y);
            _box.Width = r.Width;
            _box.Height = r.Height;
            _box.Visibility = Visibility.Visible;
            UpdateShade(r);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not Point s) return;
            ReleaseMouseCapture();
            var r = new Rect(s, e.GetPosition(_canvas));

            // Convert from WPF units to screen pixels
            var dpi = VisualTreeHelper.GetDpi(this);
            if (r.Width < 8 || r.Height < 8)
                _result = new Int32Rect(_screen.Left, _screen.Top, _screen.Width, _screen.Height); // just a click
            else
                _result = new Int32Rect(
                    _screen.Left + (int)Math.Round(r.X * dpi.DpiScaleX),
                    _screen.Top + (int)Math.Round(r.Y * dpi.DpiScaleY),
                    (int)Math.Round(r.Width * dpi.DpiScaleX),
                    (int)Math.Round(r.Height * dpi.DpiScaleY));
            Close();
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void UpdateShade(Rect? hole)
    {
        var all = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1)));
        _shade.Data = hole is Rect h
            ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(h))
            : all;
    }

    /// <summary>Shows the picker on the screen the mouse is on.</summary>
    public static Int32Rect? Pick(Brush accent)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        var picker = new RegionPicker(screen, accent);
        picker.ShowDialog();
        return picker._result;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
