using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Colour picker: click the dropper, then click anywhere on screen to copy that colour's hex code.
// While picking, the notch shows a live preview of the colour under the mouse.
public partial class MainWindow
{
    private bool _picking;
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc; // kept in a field so it isn't garbage-collected while hooked
    private bool _swallowLeftUp, _swallowRightUp;
    private readonly DispatcherTimer _pickTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _pickTimerHooked;
    private Color _pickColor;

    private void ColorPickerTool_Click(object sender, RoutedEventArgs e) => StartColorPicker();

    private void StartColorPicker()
    {
        if (_picking) return;

        SetExpanded(false);
        _picking = true;

        if (!_pickTimerHooked)
        {
            _pickTimer.Tick += (_, _) => PickTick();
            _pickTimerHooked = true;
        }

        // Catch the next click anywhere on screen so it picks a colour instead of clicking the app underneath
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        }

        _pickTimer.Start();
        PickTick();
    }

    private void PickTick()
    {
        if (!_picking) return;

        // Esc cancels
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0)
        {
            StopColorPicker(cancelled: true);
            return;
        }

        GetCursorPos(out POINT p);
        IntPtr dc = GetDC(IntPtr.Zero);
        uint pixel = GetPixel(dc, p.X, p.Y);
        ReleaseDC(IntPtr.Zero, dc);
        if (pixel == 0xFFFFFFFF) return; // couldn't read (e.g. a protected window)

        _pickColor = Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
        ShowHud("", null, Hex(_pickColor), "Click · Esc to cancel", TimeSpan.FromHours(1), swatch: _pickColor);
    }

    private void FinishColorPick()
    {
        if (!_picking) return;
        PickTick(); // read the exact spot that was clicked
        var colour = _pickColor;
        StopColorPicker(cancelled: false);

        string hex = Hex(colour);
        bool copied = TrySetClipboardText(hex);
        ShowHud("", null, copied ? $"Copied {hex}" : hex, $"rgb({colour.R}, {colour.G}, {colour.B})",
                TimeSpan.FromSeconds(3), swatch: colour);
    }

    private void StopColorPicker(bool cancelled)
    {
        if (!_picking) return;
        _picking = false;
        _pickTimer.Stop();
        if (cancelled) EndHud();

        // Keep the hook just long enough to swallow the mouse-up of the picking click
        if (!_swallowLeftUp && !_swallowRightUp) UnhookMouse();
    }

    private void UnhookMouse()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _swallowLeftUp = _swallowRightUp = false;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // Runs for every mouse event while picking. Must be quick.
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;

            if (_picking && msg == WM_LBUTTONDOWN)
            {
                _swallowLeftUp = true;
                Dispatcher.BeginInvoke(new Action(FinishColorPick));
                return (IntPtr)1;
            }
            if (_picking && msg == WM_RBUTTONDOWN)
            {
                _swallowRightUp = true;
                Dispatcher.BeginInvoke(new Action(() => StopColorPicker(cancelled: true)));
                return (IntPtr)1;
            }
            if (msg == WM_LBUTTONUP && _swallowLeftUp)
            {
                _swallowLeftUp = false;
                if (!_picking) Dispatcher.BeginInvoke(new Action(UnhookMouse));
                return (IntPtr)1;
            }
            if (msg == WM_RBUTTONUP && _swallowRightUp)
            {
                _swallowRightUp = false;
                if (!_picking) Dispatcher.BeginInvoke(new Action(UnhookMouse));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
