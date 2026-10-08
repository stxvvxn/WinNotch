using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Timer, stopwatch and Pomodoro. The countdown shows in the collapsed notch while it runs.
public partial class MainWindow
{
    private enum TimerMode { Timer, Stopwatch, Pomodoro }

    private readonly DispatcherTimer _timerTick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private TimerMode _timerMode = TimerMode.Timer;
    private bool _timerRunning;
    private TimeSpan _timerDuration = TimeSpan.FromMinutes(5);
    private TimeSpan _timerAccumulated;   // time counted before the last pause
    private DateTime _timerStartedAt;     // when the current run started
    private bool _pomodoroBreak;
    private int _pomodoroRound = 1;
    private bool _wasTimerActive;

    private static readonly Brush TimerGreen = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush TimerGrey = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));

    /// <summary>True while a timer is running or paused part-way through.</summary>
    private bool TimerActive => _timerRunning || _timerAccumulated > TimeSpan.Zero;

    private TimeSpan TimerElapsed => _timerAccumulated + (_timerRunning ? DateTime.Now - _timerStartedAt : TimeSpan.Zero);
    private TimeSpan TimerRemaining => _timerDuration - TimerElapsed;

    private void InitTimer()
    {
        _timerTick.Tick += (_, _) => TimerTick();
        UpdateTimerUi();
    }

    private void TimerTick()
    {
        if (_timerRunning && _timerMode != TimerMode.Stopwatch && TimerRemaining <= TimeSpan.Zero)
            TimerFinished();
        UpdateTimerUi();
    }

    // ---------- Buttons ----------

    private void TimerToolButton_Click(object sender, RoutedEventArgs e) => ToggleTool("timer");

    private void TimerMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse(tag, out TimerMode mode)) return;
        if (mode == _timerMode) return;

        ResetTimer();
        _timerMode = mode;
        _timerDuration = mode switch
        {
            TimerMode.Pomodoro => TimeSpan.FromMinutes(25),
            TimerMode.Stopwatch => TimeSpan.Zero,
            _ => TimeSpan.FromMinutes(5)
        };
        UpdateTimerUi();
        AnimateExpandedHeight(); // presets row shows/hides
    }

    // Preset buttons (1m, 5m...) set the timer and start it straight away
    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !int.TryParse(tag, out int minutes)) return;
        ResetTimer();
        _timerDuration = TimeSpan.FromMinutes(minutes);
        StartTimer();
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        if (_timerRunning) PauseTimer();
        else StartTimer();
    }

    private void TimerAdd_Click(object sender, RoutedEventArgs e)
    {
        _timerDuration += TimeSpan.FromMinutes(1);
        UpdateTimerUi();
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        ResetTimer();
        if (_timerMode == TimerMode.Pomodoro) _timerDuration = TimeSpan.FromMinutes(25);
        UpdateTimerUi();
    }

    // ---------- State ----------

    private void StartTimer()
    {
        StopAlarm();
        if (_timerRunning) return;
        if (_timerMode != TimerMode.Stopwatch && TimerRemaining <= TimeSpan.Zero) return;
        _timerStartedAt = DateTime.Now;
        _timerRunning = true;
        _timerTick.Start();
        UpdateTimerUi();
    }

    private void PauseTimer()
    {
        if (!_timerRunning) return;
        _timerAccumulated = TimerElapsed;
        _timerRunning = false;
        UpdateTimerUi();
    }

    private void ResetTimer()
    {
        _timerRunning = false;
        _timerAccumulated = TimeSpan.Zero;
        _pomodoroBreak = false;
        _pomodoroRound = 1;
        _timerTick.Stop();
        UpdateTimerUi();
    }

    private void TimerFinished()
    {
        if (_timerMode == TimerMode.Pomodoro)
        {
            // Focus → break → focus... every 4th break is a long one
            if (_pomodoroBreak) { _pomodoroBreak = false; _pomodoroRound++; }
            else _pomodoroBreak = true;

            _timerDuration = !_pomodoroBreak ? TimeSpan.FromMinutes(25)
                           : _pomodoroRound % 4 == 0 ? TimeSpan.FromMinutes(15)
                           : TimeSpan.FromMinutes(5);
            _timerAccumulated = TimeSpan.Zero;
            _timerStartedAt = DateTime.Now; // carries straight on
            TimerAlert(_pomodoroBreak ? "Break time" : "Back to focus", rings: 1);
        }
        else
        {
            _timerRunning = false;
            _timerAccumulated = TimeSpan.Zero;
            _timerTick.Stop();
            TimerAlert("Time's up!", rings: 4);
        }
    }

    // ---------- Alarm sound ----------

    private DispatcherTimer? _alarmTimer;
    private int _alarmRingsLeft;

    // Rings the chime a few times; hovering the notch silences it
    private void RingAlarm(int rings)
    {
        StopAlarm();
        if (!_settings.TimerSound) return;

        _alarmRingsLeft = rings - 1;
        Chime.Play();
        if (_alarmRingsLeft <= 0) return;

        _alarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _alarmTimer.Tick += (_, _) =>
        {
            if (_alarmRingsLeft-- <= 0) { StopAlarm(); return; }
            Chime.Play();
        };
        _alarmTimer.Start();
    }

    private void StopAlarm()
    {
        _alarmTimer?.Stop();
        _alarmTimer = null;
        _alarmRingsLeft = 0;
        Chime.Stop();
    }

    private void TimerAlert(string message, int rings)
    {
        RingAlarm(rings);

        if (_expanded)
        {
            _openTool = "timer";
            RefreshSections();
            ShowTopMessage(message, AccentOrange, 6);
        }
        else
        {
            ShowHud("", null, message, "", TimeSpan.FromSeconds(6), AccentOrange);
        }
    }

    // ---------- Display ----------

    private void UpdateTimerUi()
    {
        bool stopwatch = _timerMode == TimerMode.Stopwatch;
        // Countdowns round up (so 5:00 shows as 5:00, not 4:59, the moment it starts)
        TimeSpan shown = stopwatch ? TimerElapsed : TimerRemaining + TimeSpan.FromMilliseconds(999);

        TimerBigText.Text = FormatTimer(shown, tenths: stopwatch);
        TimerStartButton.Content = _timerRunning ? "" : "";
        TimerPresets.Visibility = Vis(_timerMode == TimerMode.Timer);
        TimerAddButton.Visibility = Vis(!stopwatch);

        TimerPhaseText.Text = _timerMode switch
        {
            TimerMode.Pomodoro => $"{(_pomodoroBreak ? "Break" : "Focus")} · round {_pomodoroRound}",
            _ when TimerActive && !_timerRunning => "Paused",
            _ => ""
        };

        foreach (var button in new[] { ModeTimerButton, ModeStopwatchButton, ModePomodoroButton })
            button.Foreground = (string)button.Tag == _timerMode.ToString() ? AccentOrange : Brushes.White;

        // Collapsed notch
        CompactTimerText.Text = FormatTimer(shown, tenths: false);
        CompactTimerText.Foreground = _timerRunning ? Brushes.White : TimerGrey;
        CompactTimerIcon.Foreground = _timerMode == TimerMode.Pomodoro && _pomodoroBreak ? TimerGreen : AccentOrange;

        if (TimerActive != _wasTimerActive)
        {
            _wasTimerActive = TimerActive;
            UpdateCompactView();
        }
    }

    private static string FormatTimer(TimeSpan t, bool tenths)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string main = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        return tenths ? $"{main}.{t.Milliseconds / 100}" : main;
    }
}
