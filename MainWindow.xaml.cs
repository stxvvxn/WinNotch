using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinNotch;

// The notch: date on the left and time on the right. Hover to open it: the full date, a search box,
// and system info underneath.
public partial class MainWindow : Window
{
    private const double CollapsedH = 32;
    private double CollapsedW => _settings.NotchWidth switch { "narrow" => 250, "wide" => 320, _ => 280 };
    private double ExpandedW => _settings.NotchWidth switch { "narrow" => 500, "wide" => 640, _ => 560 };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hoverTimer = new();
    private IntPtr _hwnd;
    private bool _expanded, _hovering, _typing;
    private bool _closedByKey;
    private bool _menuOpen;    // a right-click menu is showing // closed with Esc while the mouse is over it: don't pop open again until the mouse leaves
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings => _settings;

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_dragOver || _menuOpen) return; // a file is being dragged over the notch, or a right-click menu is open
            if (_reminderShowing && !_hovering) return; // a due reminder stays until Done or Snooze
            DebugLog.Write($"hover timer: hovering={_hovering} typing={_typing} text='{SearchBox.Text}' expanded={_expanded}");
            if (_hovering) SetExpanded(true);
            // Mouse has gone: close, unless you're in the middle of typing something
            else if (!_typing || (SearchBox.Text.Trim().Length == 0 && !ScratchBox.IsKeyboardFocusWithin)) SetExpanded(false);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            MakeToolWindow();
            HwndSource.FromHwnd(_hwnd)?.AddHook(ActivationHook);
            InitTray();
        };

        Loaded += (_, _) =>
        {
            ApplySettings();
            InitScreens();
            InitStats();
            InitReminders();
            InitHome();
            if (App.TakeOpenSettingsFlag()) OpenSettings(); // after a debug hard restart
        };

        // Clicked into another app while typing: close up
        Activated += (_, _) => DebugLog.Write("window activated");
        Deactivated += (_, _) =>
        {
            DebugLog.Write($"window deactivated: hovering={_hovering} expanded={_expanded}");
            _typing = false;
            if (!_hovering && !_menuOpen && !_reminderShowing) SetExpanded(false);
        };

        Closed += (_, _) =>
        {
            if (_scratchSave.IsEnabled) SaveScratch();
            StopSearchHotkey();
            DisposeTray();
        };
    }

    // ---------- Settings ----------

    public void SettingsChanged()
    {
        _settings.Save();
        ApplySettings();
    }

    private void ApplySettings()
    {
        double scale = _settings.Size switch { "small" => 0.9, "large" => 1.15, _ => 1.0 };
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        StartupRegistration.Apply(_settings.StartWithWindows);
        SearchPlaceholder.Text = $"Search files, the web, or ask {WebTools.Ai(_settings).Name}";
        ResultsHint.Text = $"Enter opens · Shift+Enter searches {WebTools.Engine(_settings).Name} · Ctrl+Enter asks {WebTools.Ai(_settings).Name} · Esc clears";
        UpdateClock();
        ApplyTheme();
        BuildStatTiles();
        UpdateQuote();
        ApplyHome();
        ApplyTidy();
        BuildBubbles();
        ApplyScratch();
        ApplyScreenshots();
        if (_media != null) PickMediaSession(); // the chosen music service may have changed
        if (!EnabledPages().Contains(_page)) ShowPage(0, animate: false);
        BuildPageDots();
        Pill.AllowDrop = _settings.ShelfPage;
        RegisterSearchHotkey();
        InitUpdates();
        PositionWindow();
        if (_expanded) AnimateHeight();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---------- Date and time ----------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        if (now.Date != _quoteDay) UpdateQuote(); // new day, new quote
        // Closed: 09/10/2026 · Open: Friday 9 October 2026
        // A reminder coming up soon swaps the closed date for a bell and "in 12 min"
        string? tip = null;
        string glyph = "\uEA8F";
        string? soon = _expanded ? null : HeaderReminderText(now, out tip, out glyph);
        DateText.Text = soon ?? (_expanded
            ? now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)
            : now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        ReminderBell.Visibility = Vis(soon != null);
        ReminderBell.Text = glyph;
        DateText.ToolTip = tip;
        UpdateCountdownRow(now);
        if (_expanded)
        {
            UpdateHome(now);
            if (now.Second % 3 == 0) RefreshToggles(); // mic / dark mode changed somewhere else
        }
        TimeText.Text = now.ToString(_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
    }

    // ---------- Quote of the day ----------

    private DateTime _quoteDay;

    private void UpdateQuote()
    {
        _quoteDay = DateTime.Today;
        var (text, author) = Quotes.Today(_quoteDay);
        QuoteText.Text = $"\u201C{text}\u201D";
        QuoteAuthor.Text = $"\u2014 {author}";
        // The quote and the home extras step aside while you're searching
        bool searching = SearchBox.Text.Length > 0;
        bool show = _settings.ShowQuote && !searching;
        bool changed = (QuoteArea.Visibility == Visibility.Visible) != show
                       || (GlanceArea.Visibility == Visibility.Visible) == searching;
        QuoteArea.Visibility = Vis(show);
        GlanceArea.Visibility = Vis(!searching);
        if (changed) AnimateHeight();
    }

    // ---------- Opening and closing ----------

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        DebugLog.Write($"mouse enter (closedByKey={_closedByKey})");
        _hovering = true;
        if (_closedByKey) return;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_expanded ? 0 : 90);
        _hoverTimer.Start();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        DebugLog.Write("mouse leave");
        _hovering = false;
        _closedByKey = false;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(380);
        _hoverTimer.Start();
    }

    private void SetExpanded(bool expand)
    {
        DebugLog.Write($"SetExpanded({expand}) currently {_expanded}, pill {Pill.ActualWidth:0}x{Pill.ActualHeight:0}");
        if (_expanded == expand || (expand && NotchHidden)) return;
        if (!expand && _reminderShowing) return; // a due reminder keeps the notch open until it's handled
        _expanded = expand;
        UpdateClock();

        var springy = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
        var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(expand ? 440 : 260);

        double fromW = Pill.ActualWidth, fromH = Pill.ActualHeight;
        var widthAnim = new DoubleAnimation(fromW, expand ? ExpandedW : CollapsedW, time) { EasingFunction = expand ? springy : smooth };
        var heightAnim = new DoubleAnimation(fromH, expand ? ExpandedHeight : CollapsedH, time) { EasingFunction = expand ? springy : smooth };
        heightAnim.Completed += (_, _) => DebugLog.Write($"size animation done: expanded={_expanded}, pill {Pill.ActualWidth:0}x{Pill.ActualHeight:0}");
        Pill.BeginAnimation(WidthProperty, widthAnim);
        Pill.BeginAnimation(HeightProperty, heightAnim);
        Pill.CornerRadius = expand ? new CornerRadius(0, 0, 28, 28) : new CornerRadius(0, 0, 14, 14);
        ShowBubbles(expand);

        // The header grows a little when open
        double size = expand ? 14 : 12.5;
        DateText.FontSize = TimeText.FontSize = size;
        HeaderRow.Margin = expand ? new Thickness(24, 4, 24, 0) : new Thickness(18, 0, 18, 0);

        if (expand)
        {
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            { BeginTime = TimeSpan.FromMilliseconds(120) });
        }
        else
        {
            // Straight away, so a closed notch never shows the search box
            Body.BeginAnimation(OpacityProperty, null);
            Body.Opacity = 0;
        }
        Body.IsHitTestVisible = expand;

        if (expand)
        {
            UpdateStats();
            RefreshToggles();
            RefreshHomeData(); // weather / calendar if they're getting old
            // Reopening on volume or shelf: bring that page up to date
            if (_page != 0) ShowPage(_page, animate: false);
        }
        else
        {
            // Closing (mouse away, Esc, hidden) always goes back to system info for next time
            ShowPage(0, animate: false);
            SearchBox.Text = "";
            // Give the keyboard back after this key press has finished being handled
            Dispatcher.BeginInvoke(new Action(EndTyping), DispatcherPriority.Background);
        }
        Root.InvalidateVisual();
    }

    private double ExpandedHeight
    {
        get
        {
            // Something inside may have just appeared or gone (e.g. search results cleared); WPF only
            // notices that on its next layout pass, so mark everything for re-measuring first
            InvalidateMeasureDeep(Body);
            Body.Measure(new Size(ExpandedW, double.PositiveInfinity));
            return Body.DesiredSize.Height; // includes the space above for the date and time
        }
    }

    private static void InvalidateMeasureDeep(DependencyObject element)
    {
        if (element is UIElement ui) ui.InvalidateMeasure();
        int n = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < n; i++) InvalidateMeasureDeep(VisualTreeHelper.GetChild(element, i));
    }

    /// <summary>Resize the open notch when results or tiles appear / disappear.</summary>
    private void AnimateHeight()
    {
        if (!_expanded) return;
        double height = ExpandedHeight;
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        MoveBubbles(height);
    }

    /// <summary>Close with the keyboard (Esc), and stay closed while the mouse is still over the notch.</summary>
    private void CloseFromKeyboard()
    {
        DebugLog.Write($"close from keyboard: hovering={_hovering} expanded={_expanded}");
        if (_reminderShowing) return; // stays open until Done or Snooze
        _closedByKey = _hovering;
        _hoverTimer.Stop();
        SetExpanded(false);
    }

    // Esc anywhere in the open notch closes it
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DebugLog.Write($"Esc pressed: expanded={_expanded} typing={_typing}");
        if (e.Key == Key.Escape && _expanded)
        {
            e.Handled = true;
            // First Esc clears the search (results go away, notch stays open); the next one closes the notch.
            // In the scratch pad, Esc just closes (your notes are kept).
            if (SearchBox.Text.Length > 0 && !ScratchBox.IsKeyboardFocusWithin)
            {
                ClearSearchKeepOpen();
                return;
            }
            CloseFromKeyboard();
        }
    }

    // ---------- Typing ----------
    // The notch never takes the keyboard on its own (so it doesn't steal it from your apps).
    // Clicking the search box, or the shortcut, borrows it until the notch closes.

    private IntPtr _previousWindow;

    private void BeginTyping() => BeginTyping(SearchBox);

    private void BeginTyping(System.Windows.Controls.TextBox box)
    {
        if (!_typing || GetForegroundWindow() != _hwnd)
        {
            _typing = true;
            var current = GetForegroundWindow();
            if (current != _hwnd) _previousWindow = current;
            bool fg = current == _hwnd || ForceForeground();
            bool act = Activate();
            DebugLog.Write($"begin typing: foreground={fg} Activate={act}");
        }
        box.Focus();
        Keyboard.Focus(box);
    }

    private bool ForceForeground()
    {
        if (SetForegroundWindow(_hwnd)) return true;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint ours = GetCurrentThreadId();
        if (fgThread == 0 || fgThread == ours) return false;
        AttachThreadInput(ours, fgThread, true);
        try { return SetForegroundWindow(_hwnd); }
        finally { AttachThreadInput(ours, fgThread, false); }
    }

    private void EndTyping()
    {
        DebugLog.Write($"end typing (typing={_typing})");
        if (!_typing) return;
        _typing = false;
        Keyboard.ClearFocus();
        if (GetForegroundWindow() == _hwnd && _previousWindow != IntPtr.Zero) SetForegroundWindow(_previousWindow);
    }

    // ---------- Window plumbing ----------

    // Hidden from Alt+Tab
    private void MakeToolWindow()
    {
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    // Clicking the notch normally leaves the keyboard with your other app - except a click on the
    // search box, which lets Windows switch the keyboard to the notch the normal way.
    private IntPtr ActivationHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_ACTIVATE = 1, MA_NOACTIVATE = 3;
        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        bool onSearch = _expanded && (SearchBox.IsMouseOver || ScratchBox.IsMouseOver);
        DebugLog.Write($"mouse activate: on search box={onSearch}");
        return (IntPtr)(onSearch ? MA_ACTIVATE : MA_NOACTIVATE);
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
