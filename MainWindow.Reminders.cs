using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinNotch;

// Reminders: type "remind me in 20 min to check the oven" (or "every weekday at 9am stand-up") in the search box.
// When one is due the notch drops open with Done / Snooze and stays until you pick one.
// When one is coming up soon, the closed notch shows a bell and "in 12 min" instead of the date.
public partial class MainWindow
{
    private List<Reminder> _reminders = new();
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<Guid> _dueQueue = new();   // due and waiting to be shown, oldest first
    private Reminder? _showingReminder;
    private bool _reminderShowing;
    private string? _headerFlash;                     // "Set for 17:00" on the closed notch for a moment
    private DateTime _headerFlashUntil;

    public IReadOnlyList<Reminder> Reminders => _reminders;

    private void InitReminders()
    {
        _reminders = ReminderStore.Load();
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();
        // Anything missed while the PC was off shows straight away
        Dispatcher.BeginInvoke(new Action(CheckReminders), DispatcherPriority.Background);
    }

    // ---------- Typing one in ----------

    /// <summary>"Remind me: Check the oven" as the top search row, or null if the text isn't a reminder.</summary>
    private Result? ReminderRow(string q)
    {
        if (!_settings.RemindersEnabled || !ReminderParser.TryParse(q, DateTime.Now, out var r)) return null;
        if (r.IsTimer)
            return new Result($"Timer: {Reminder.Duration(r.TimerSeconds)}{(r.Text == "Timer" ? "" : " · " + r.Text)}",
                $"Ends at {r.Due:HH:mm:ss} · Enter starts it", "Timer", "\uE916", "", () => AddReminder(r));
        string when = r.Repeats ? $"{r.RepeatText} · first one {r.WhenText(DateTime.Now)}" : r.WhenText(DateTime.Now);
        return new Result($"Remind me: {r.Text}", $"{when} · Enter sets it", "Reminder", "", "", () => AddReminder(r));
    }

    public void AddReminder(Reminder r)
    {
        _reminders.Add(r);
        SaveReminders();
        DebugLog.Write($"reminder set: '{r.Text}' due {r.Due:g} repeat={r.Repeat}");

        var now = DateTime.Now;
        string time = r.Due.ToString("HH:mm");
        _headerFlash = r.IsTimer ? null
            : r.Due.Date == now.Date ? $"Set for {time}"
            : r.Due.Date == now.Date.AddDays(1) ? $"Set for tomorrow {time}"
            : $"Set for {r.Due:ddd} {time}";
        _headerFlashUntil = now.AddSeconds(4);
        UpdateClock();
    }

    public void DeleteReminder(Guid id)
    {
        _reminders.RemoveAll(r => r.Id == id);
        _dueQueue.Remove(id);
        if (_showingReminder?.Id == id) ShowNextReminder();
        SaveReminders();
    }

    private void SaveReminders()
    {
        ReminderStore.Save(_reminders);
        UpdateClock();
        if (_settingsWindow is { IsLoaded: true }) _settingsWindow.BuildReminderList();
    }

    // ---------- When one is due ----------

    private void CheckReminders()
    {
        var now = DateTime.Now;
        foreach (var r in _reminders.Where(r => r.Due <= now).OrderBy(r => r.Due))
            if (!_dueQueue.Contains(r.Id) && _showingReminder?.Id != r.Id) _dueQueue.Add(r.Id);

        if (_showingReminder == null && _dueQueue.Count > 0) ShowNextReminder();
        else if (_showingReminder != null) UpdateAlertText();
    }

    private void ShowNextReminder()
    {
        _showingReminder = null;
        while (_dueQueue.Count > 0 && _showingReminder == null)
        {
            var id = _dueQueue[0];
            _dueQueue.RemoveAt(0);
            _showingReminder = _reminders.FirstOrDefault(r => r.Id == id);
        }

        if (_showingReminder == null)
        {
            HideReminderAlert();
            return;
        }

        bool wasShowing = _reminderShowing;
        // Over a game or full-screen video only if Settings allows it; otherwise it waits until you're back
        if (_hiddenForFullscreen && !_settings.ReminderOverFullscreen && !_userHidden)
        {
            _dueQueue.Insert(0, _showingReminder.Id);
            _showingReminder = null;
            HideReminderAlert();
            return;
        }

        _reminderShowing = true;
        ReminderAlertText.Text = _showingReminder.IsTimer && _showingReminder.Text == "Timer" ? "Time's up" : _showingReminder.Text;
        ReminderAlertBell.Text = _showingReminder.IsTimer ? "\uE916" : "\uEA8F";
        UpdateAlertText();
        BuildReminderButtons();
        ReminderAlert.Visibility = Visibility.Visible;
        DebugLog.Write($"reminder due: '{_showingReminder.Text}'");

        if (!wasShowing) ApplyNotchVisibility(); // brings the notch back if it was hidden
        if (_expanded) AnimateHeight();
        else SetExpanded(true);
        // Always open on the first page so the search box (and the alert above it) are where you expect
        if (_page != 0) ShowPage(0);

        RingBell();
    }

    private void UpdateAlertText()
    {
        if (_showingReminder is not { } r) return;
        var now = DateTime.Now;
        string when = r.IsTimer ? $"{Reminder.Duration(r.TimerSeconds)} timer finished at {r.Due:HH:mm}"
            : now - r.Due < TimeSpan.FromMinutes(2) ? "Now"
            : r.Due.Date == now.Date ? $"Was due at {r.Due:HH:mm}"
            : $"Missed · was due {r.Due:ddd d MMM} at {r.Due:HH:mm}";
        if (r.Repeats) when += " · " + r.RepeatText;
        if (_dueQueue.Count > 0) when += $" · {_dueQueue.Count} more";
        ReminderAlertWhen.Text = when;
    }

    private void BuildReminderButtons()
    {
        ReminderButtons.Children.Clear();
        if (_showingReminder?.IsTimer == true)
        {
            ReminderButtons.Children.Add(AlertButton("+1 min", () => Snooze(TimeSpan.FromMinutes(1)), false));
            ReminderButtons.Children.Add(AlertButton("+5 min", () => Snooze(TimeSpan.FromMinutes(5)), false));
        }
        else
        {
            ReminderButtons.Children.Add(AlertButton("Snooze 5 min", () => Snooze(TimeSpan.FromMinutes(5)), false));
            ReminderButtons.Children.Add(AlertButton("Snooze 1 hr", () => Snooze(TimeSpan.FromHours(1)), false));
        }
        ReminderButtons.Children.Add(AlertButton("Done", ReminderDone, true));
    }

    private Button AlertButton(string text, Action click, bool main)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("AlertButton") };
        if (main)
        {
            b.SetResourceReference(BackgroundProperty, "Accent");
            b.Foreground = Brushes.Black;
        }
        b.Click += (_, _) => click();
        return b;
    }

    private void ReminderDone()
    {
        if (_showingReminder is not { } r) return;
        if (r.Repeats) r.Due = r.NextAfter(DateTime.Now); // next one in the series
        else _reminders.Remove(r);
        SaveReminders();
        ShowNextReminder();
    }

    private void Snooze(TimeSpan by)
    {
        if (_showingReminder is not { } r) return;
        r.Due = DateTime.Now.Add(by);
        SaveReminders();
        ShowNextReminder();
    }

    private void HideReminderAlert()
    {
        if (!_reminderShowing) return;
        _reminderShowing = false;
        ReminderBellSwing.BeginAnimation(RotateTransform.AngleProperty, null);
        ReminderAlert.Visibility = Visibility.Collapsed;
        ApplyNotchVisibility(); // back to hidden if a full-screen app is still in front
        if (!_expanded) return;
        AnimateHeight();
        if (!_hovering)
        {
            // Handled from somewhere other than the notch (e.g. Settings): tuck away
            _hoverTimer.Stop();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(600);
            _hoverTimer.Start();
        }
    }

    private void RingBell()
    {
        // Swing the bell a few times
        var swing = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900), RepeatBehavior = new RepeatBehavior(3) };
        double[] angles = { 0, 18, -16, 12, -8, 4, 0 };
        for (int i = 0; i < angles.Length; i++)
            swing.KeyFrames.Add(new EasingDoubleKeyFrame(angles[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 100))));
        ReminderBellSwing.BeginAnimation(RotateTransform.AngleProperty, swing);

        if (!_settings.ReminderSound) return;
        try
        {
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            string? wav = new[] { "Windows Notify Calendar.wav", "Alarm01.wav", "Windows Notify System Generic.wav" }
                .Select(f => Path.Combine(media, f)).FirstOrDefault(File.Exists);
            if (wav != null) new SoundPlayer(wav).Play();
            else SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    // ---------- Countdown on the closed notch ----------

    /// <summary>
    /// The reminder to count down to: the soonest running timer, or the next reminder inside the countdown window.
    /// </summary>
    private Reminder? CountdownReminder(DateTime now)
    {
        if (!_settings.RemindersEnabled) return null;
        var window = TimeSpan.FromMinutes(Math.Max(0, _settings.ReminderCountdownMinutes));
        return _reminders
            .Where(r => r.Due > now && (r.IsTimer || (window > TimeSpan.Zero && r.Due - now <= window)))
            .OrderBy(r => r.Due)
            .FirstOrDefault();
    }

    /// <summary>"in 12 min" for a reminder, "09:59" for a timer.</summary>
    private static string CountdownText(Reminder r, DateTime now)
    {
        var left = r.Due - now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (r.IsTimer || left < TimeSpan.FromMinutes(1))
        {
            int secs = (int)Math.Ceiling(left.TotalSeconds);
            var t = TimeSpan.FromSeconds(secs);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        }
        int mins = (int)Math.Ceiling(left.TotalMinutes);
        if (mins < 60) return $"in {mins} min";
        return mins % 60 == 0 ? $"in {mins / 60} hr" : $"in {mins / 60} hr {mins % 60} min";
    }

    /// <summary>What to show instead of the date on the closed notch ("in 12 min" / "09:59"), or null for the date.</summary>
    private string? HeaderReminderText(DateTime now, out string? tooltip, out string glyph)
    {
        tooltip = null;
        glyph = "\uEA8F"; // bell
        if (_headerFlash != null)
        {
            if (now < _headerFlashUntil) return _headerFlash;
            _headerFlash = null;
        }

        var next = CountdownReminder(now);
        // A calendar event starting soon counts down too (Settings → Home page)
        var window = TimeSpan.FromMinutes(Math.Max(0, _settings.ReminderCountdownMinutes));
        var ev = _settings.CalendarCountdown && window > TimeSpan.Zero ? NextCalendarEvent(now) : null;
        if (ev != null && ev.Start - now > window) ev = null;

        if (ev != null && (next == null || ev.Start < next.Due))
        {
            glyph = "\uE787"; // calendar
            tooltip = $"{ev.Title} · {ev.Start:HH:mm}{(ev.Location.Length > 0 ? $" · {ev.Location}" : "")}";
            return CountdownText(new Reminder { Due = ev.Start }, now);
        }
        if (next == null) return null;
        if (next.IsTimer) glyph = "\uE916"; // stopwatch
        tooltip = next.IsTimer ? $"{next.Text} · ends at {next.Due:HH:mm:ss}" : $"{next.Text} · {next.Due:HH:mm}";
        return CountdownText(next, now);
    }

    // ---------- Countdown row in the open notch ----------

    private Guid? _countdownRowId;

    private void UpdateCountdownRow(DateTime now)
    {
        var next = _expanded && SearchBox.Text.Length == 0 && !_reminderShowing ? CountdownReminder(now) : null;
        bool show = next != null;
        bool changed = show != (NextReminder.Visibility == Visibility.Visible) || next?.Id != _countdownRowId;
        _countdownRowId = next?.Id;
        NextReminder.Visibility = Vis(show);
        if (next != null)
        {
            NextReminderIcon.Text = next.IsTimer ? "\uE916" : "\uEA8F";
            NextReminderText.Text = next.Text;
            NextReminderWhen.Text = next.IsTimer ? $"{Reminder.Duration(next.TimerSeconds)} timer · ends {next.Due:HH:mm:ss}"
                : next.Repeats ? $"{next.Due:HH:mm} · {next.RepeatText}" : $"at {next.Due:HH:mm}";
            NextReminderCountdown.Text = CountdownText(next, now);
            NextReminderCancel.ToolTip = next.IsTimer ? "Stop this timer" : "Delete this reminder";
        }
        if (changed && _expanded) AnimateHeight();
    }

    private void NextReminderCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_countdownRowId is { } id) DeleteReminder(id);
    }
}
