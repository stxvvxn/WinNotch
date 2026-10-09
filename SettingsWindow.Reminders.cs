using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinNotch;

// Settings → Reminders: the list of what's coming up, each with a delete button
public partial class SettingsWindow
{
    public void BuildReminderList()
    {
        ReminderList.Children.Clear();
        var now = DateTime.Now;
        var list = _owner.Reminders.OrderBy(r => r.Due).ToList();
        ReminderListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var r in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            var delete = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                Padding = new Thickness(10, 5, 10, 5),
                ToolTip = "Delete this reminder",
                VerticalAlignment = VerticalAlignment.Center,
            };
            var id = r.Id;
            delete.Click += (_, _) =>
            {
                _owner.DeleteReminder(id);
                BuildReminderList();
            };
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);

            var icon = new TextBlock
            {
                Text = r.IsTimer ? "\uE916" : r.Repeats ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = r.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            string when = r.Due <= now ? "due now" : r.WhenText(now);
            if (r.IsTimer) when = $"{Reminder.Duration(r.TimerSeconds)} timer · ends {r.Due:HH:mm:ss}";
            if (r.Repeats) when += " · " + r.RepeatText;
            text.Children.Add(new TextBlock { Text = when, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), FontSize = 11 });
            row.Children.Add(text);

            ReminderList.Children.Add(row);
        }
    }

    // ---------- Adding one from here ----------

    private string NewRepeat => (NewReminderRepeat.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";

    private void NewReminderRepeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NewReminderDay == null) return; // still loading
        FillNewReminderDays();
    }

    /// <summary>Day choices to suit the repeat: dates for "Once", weekdays for "Every week", none otherwise.</summary>
    private void FillNewReminderDays()
    {
        string repeat = NewRepeat;
        NewReminderDay.Items.Clear();
        if (repeat == "none")
        {
            var today = DateTime.Today;
            for (int i = 0; i < 28; i++)
            {
                var d = today.AddDays(i);
                string label = i == 0 ? "Today" : i == 1 ? "Tomorrow" : d.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
                NewReminderDay.Items.Add(new ComboBoxItem { Content = label, Tag = d });
            }
        }
        else if (repeat == "weekly")
        {
            foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                NewReminderDay.Items.Add(new ComboBoxItem { Content = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d), Tag = d });
        }
        if (NewReminderDay.Items.Count > 0)
            NewReminderDay.SelectedIndex = repeat == "weekly" ? ((int)DateTime.Today.DayOfWeek + 6) % 7 : 0;

        NewReminderDayRow.Visibility = repeat is "none" or "weekly" ? Visibility.Visible : Visibility.Collapsed;
        NewReminderTimeRow.Visibility = repeat == "interval" ? Visibility.Collapsed : Visibility.Visible;
        NewReminderEveryRow.Visibility = repeat == "interval" ? Visibility.Visible : Visibility.Collapsed;
        NewReminderStatus.Text = "";
    }

    private void NewReminder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddReminder_Click(sender, e);
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        string repeat = NewRepeat;
        string text = NewReminderText.Text.Trim();
        var r = new Reminder { Text = text.Length == 0 ? "Reminder" : char.ToUpperInvariant(text[0]) + text[1..], Repeat = repeat };

        if (repeat == "interval")
        {
            int unit = int.TryParse((NewReminderEveryUnit.SelectedItem as ComboBoxItem)?.Tag as string, out int u) ? u : 1;
            if (!double.TryParse(NewReminderEvery.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
            {
                Status("How often? Type a number, like 45.", error: true);
                return;
            }
            int minutes = (int)Math.Round(n * unit);
            if (minutes < 1 || minutes > 7 * 24 * 60)
            {
                Status("Pick something between 1 minute and a week.", error: true);
                return;
            }
            r.IntervalMinutes = minutes;
            r.Due = now.AddMinutes(minutes);
        }
        else
        {
            if (!ReminderParser.TryTime(NewReminderTime.Text, out var time))
            {
                Status("That time doesn't look right - try 17:30 or 5:30pm.", error: true);
                return;
            }
            if (repeat == "none")
            {
                var day = (NewReminderDay.SelectedItem as ComboBoxItem)?.Tag as DateTime? ?? DateTime.Today;
                r.Due = day.Date.Add(time);
                if (r.Due <= now)
                {
                    Status("That time has already gone - pick a later time or another day.", error: true);
                    return;
                }
            }
            else
            {
                r.TimeOfDay = time;
                if (repeat == "weekly") r.WeeklyDay = (NewReminderDay.SelectedItem as ComboBoxItem)?.Tag as DayOfWeek? ?? DateTime.Today.DayOfWeek;
                r.Due = r.NextAfter(now);
            }
        }

        _owner.AddReminder(r);
        NewReminderText.Text = "";
        string when = r.WhenText(now);
        Status(r.Repeats ? $"Added · {r.RepeatText}, first one {when}" : $"Added · {when}", error: false);
        NewReminderText.Focus();
    }

    private void Status(string text, bool error)
    {
        NewReminderStatus.Text = text;
        NewReminderStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : (Brush)FindResource("Accent");
    }
}
