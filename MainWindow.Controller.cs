using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WinNotch;

// ROG Ally mode: drive the notch with the controller.
//   Hold View + Menu  → open the notch (or a quick glance while a game is fullscreen)
//   D-pad / left stick → move between buttons     A → press     B → back / close
//   Left / right on a slider → change brightness or volume
public partial class MainWindow
{
    private const ushort PAD_UP = 0x0001, PAD_DOWN = 0x0002, PAD_LEFT = 0x0004, PAD_RIGHT = 0x0008;
    private const ushort PAD_MENU = 0x0010, PAD_VIEW = 0x0020, PAD_A = 0x1000, PAD_B = 0x2000;
    private const short StickThreshold = 20000;

    private bool _navActive;
    private FrameworkElement? _navSelected;

    private int _padIndex = -1;
    private DateTime _nextPadScan = DateTime.MinValue;
    private bool _xinputMissing;
    private ushort _padPrev;
    private DateTime? _comboSince;
    private bool _comboFired;
    private ushort _navHeldDir;
    private DateTime _navRepeatAt;

    // ---------- Reading the controller ----------

    private void PollController()
    {
        if (_xinputMissing) return;

        try
        {
            // Look for a connected controller every few seconds (asking empty slots is slow)
            if (_padIndex < 0)
            {
                if (DateTime.Now < _nextPadScan) return;
                for (uint i = 0; i < 4; i++)
                {
                    if (XInputGetState(i, out _) == 0) { _padIndex = (int)i; break; }
                }
                if (_padIndex < 0) { _nextPadScan = DateTime.Now.AddSeconds(3); return; }
            }

            if (XInputGetState((uint)_padIndex, out var state) != 0)
            {
                _padIndex = -1;
                _nextPadScan = DateTime.Now.AddSeconds(3);
                return;
            }

            ushort buttons = state.Gamepad.wButtons;
            // Treat the left stick like the D-pad
            if (state.Gamepad.sThumbLY > StickThreshold) buttons |= PAD_UP;
            if (state.Gamepad.sThumbLY < -StickThreshold) buttons |= PAD_DOWN;
            if (state.Gamepad.sThumbLX < -StickThreshold) buttons |= PAD_LEFT;
            if (state.Gamepad.sThumbLX > StickThreshold) buttons |= PAD_RIGHT;

            ushort pressed = (ushort)(buttons & ~_padPrev);
            _padPrev = buttons;

            // Hold View + Menu for a moment to open / close
            bool combo = (buttons & (PAD_VIEW | PAD_MENU)) == (PAD_VIEW | PAD_MENU);
            if (combo)
            {
                _comboSince ??= DateTime.Now;
                if (!_comboFired && DateTime.Now - _comboSince > TimeSpan.FromMilliseconds(400))
                {
                    _comboFired = true;
                    OnAllyButton();
                }
                return;
            }
            _comboSince = null;
            _comboFired = false;

            if (!_navActive || !_expanded) return;

            if ((pressed & PAD_A) != 0) { _lastAllyInteraction = DateTime.Now; NavActivate(); }
            if ((pressed & PAD_B) != 0) { _lastAllyInteraction = DateTime.Now; NavBack(); return; }

            // Directions, with key-repeat when held
            ushort dir = (buttons & PAD_UP) != 0 ? PAD_UP
                       : (buttons & PAD_DOWN) != 0 ? PAD_DOWN
                       : (buttons & PAD_LEFT) != 0 ? PAD_LEFT
                       : (buttons & PAD_RIGHT) != 0 ? PAD_RIGHT : (ushort)0;

            if (dir != _navHeldDir)
            {
                _navHeldDir = dir;
                if (dir != 0) { NavMove(dir); _navRepeatAt = DateTime.Now.AddMilliseconds(400); }
            }
            else if (dir != 0 && DateTime.Now >= _navRepeatAt)
            {
                NavMove(dir);
                _navRepeatAt = DateTime.Now.AddMilliseconds(120);
            }
        }
        catch (DllNotFoundException) { _xinputMissing = true; }
        catch (EntryPointNotFoundException) { _xinputMissing = true; }
    }

    // ---------- Navigation ----------

    private void StartNav()
    {
        _navActive = true;
        _navHeldDir = 0;
        _lastAllyInteraction = DateTime.Now;

        // Pick a starting button once the open notch has laid itself out
        Dispatcher.InvokeAsync(() =>
        {
            if (!_navActive) return;
            var items = NavItems();
            var inContent = items.Where(i => IsInside(i, ExpandedView)).ToList();
            var pool = inContent.Count > 0 ? inContent : items;
            _navSelected = pool.OrderBy(i => Math.Round(NavRect(i).Top / 10)).ThenBy(i => NavRect(i).Left).FirstOrDefault();
            UpdateNavRing();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void EndNav()
    {
        _navActive = false;
        _navSelected = null;
        if (NavRing != null) NavRing.Visibility = Visibility.Collapsed;
    }

    // Every button and slider currently visible in the open notch
    private List<FrameworkElement> NavItems()
    {
        var list = new List<FrameworkElement>();

        void Walk(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { IsVisible: false }) continue;

                if (child is Button button)
                {
                    if (button.IsEnabled) list.Add(button);
                    continue;
                }
                if (child is Slider slider)
                {
                    list.Add(slider);
                    continue;
                }
                Walk(child);
            }
        }

        Walk(TopLeftBar);
        Walk(ExpandedView);
        Walk(BubbleLayer);
        return list;
    }

    private Rect NavRect(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    private void NavMove(ushort dir)
    {
        _lastAllyInteraction = DateTime.Now;

        // Left / right on a slider changes its value
        if (_navSelected is Slider slider && (dir == PAD_LEFT || dir == PAD_RIGHT))
        {
            slider.Value = Math.Clamp(slider.Value + (dir == PAD_RIGHT ? 5 : -5), slider.Minimum, slider.Maximum);
            return;
        }

        var items = NavItems();
        if (_navSelected == null || !items.Contains(_navSelected))
        {
            _navSelected = items.FirstOrDefault();
            UpdateNavRing();
            return;
        }

        var from = NavRect(_navSelected);
        var fromCentre = new Point(from.Left + from.Width / 2, from.Top + from.Height / 2);

        FrameworkElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var item in items)
        {
            if (item == _navSelected) continue;
            var r = NavRect(item);
            if (r.IsEmpty) continue;
            double dx = r.Left + r.Width / 2 - fromCentre.X;
            double dy = r.Top + r.Height / 2 - fromCentre.Y;

            (double along, double across) = dir switch
            {
                PAD_UP => (-dy, Math.Abs(dx)),
                PAD_DOWN => (dy, Math.Abs(dx)),
                PAD_LEFT => (-dx, Math.Abs(dy)),
                _ => (dx, Math.Abs(dy)),
            };
            if (along <= 4) continue; // not in that direction

            double score = along + across * 2.5; // prefer things straight ahead
            if (score < bestScore) { bestScore = score; best = item; }
        }

        if (best != null)
        {
            _navSelected = best;
            best.BringIntoView();
            UpdateNavRing();
        }
    }

    private void NavActivate()
    {
        if (_navSelected is Button button && button.IsEnabled)
        {
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                invoke.Invoke();

            // The press may have opened a panel or closed the notch: keep the selection sensible
            Dispatcher.InvokeAsync(ValidateNavSelection, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void NavBack()
    {
        if (_openTool != null)
        {
            _openTool = null;
            RefreshSections();
            Dispatcher.InvokeAsync(ValidateNavSelection, System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            CloseFromAlly();
        }
    }

    private void ValidateNavSelection()
    {
        if (!_navActive) return;
        var items = NavItems();
        if (_navSelected == null || !items.Contains(_navSelected))
            _navSelected = items.FirstOrDefault(i => IsInside(i, ExpandedView)) ?? items.FirstOrDefault();
        UpdateNavRing();
    }

    // Draws the orange ring around the selected button
    private void UpdateNavRing()
    {
        if (!_navActive || _navSelected == null || !_navSelected.IsVisible)
        {
            NavRing.Visibility = Visibility.Collapsed;
            return;
        }

        var r = NavRect(_navSelected);
        if (r.IsEmpty)
        {
            NavRing.Visibility = Visibility.Collapsed;
            return;
        }

        r.Inflate(3, 3);
        Canvas.SetLeft(NavRing, r.Left);
        Canvas.SetTop(NavRing, r.Top);
        NavRing.Width = r.Width;
        NavRing.Height = r.Height;
        NavRing.Visibility = Visibility.Visible;
    }

    // ---------- XInput ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XINPUT_STATE state);
}
