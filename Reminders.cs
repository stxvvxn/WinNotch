using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinNotch;

/// <summary>One reminder. Repeating ones move their Due time forward each time they're done.</summary>
public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public string Repeat { get; set; } = "none";   // none / daily / weekdays / weekly / interval
    public int IntervalMinutes { get; set; }        // for "every 45 min"
    public DayOfWeek? WeeklyDay { get; set; }       // for "every monday"
    public TimeSpan? TimeOfDay { get; set; }        // for daily / weekdays / weekly
    public bool IsTimer { get; set; }               // "timer 10 min": counts down mm:ss on the notch
    public int TimerSeconds { get; set; }           // how long the timer was set for

    /// <summary>"10 min", "1 hr 30 min", "45 sec".</summary>
    public static string Duration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if ((int)t.TotalHours > 0) parts.Add($"{(int)t.TotalHours} hr");
        if (t.Minutes > 0) parts.Add($"{t.Minutes} min");
        if (t.Seconds > 0) parts.Add($"{t.Seconds} sec");
        return parts.Count == 0 ? "0 sec" : string.Join(" ", parts);
    }

    public bool Repeats => Repeat != "none";

    /// <summary>"every weekday at 09:00", "every 45 min" ... (empty for one-off reminders).</summary>
    public string RepeatText => Repeat switch
    {
        "daily" => $"every day at {Clock(TimeOfDay)}",
        "weekdays" => $"every weekday at {Clock(TimeOfDay)}",
        "weekly" => $"every {WeeklyDay} at {Clock(TimeOfDay)}",
        "interval" => IntervalMinutes % 60 == 0 ? $"every {(IntervalMinutes / 60 == 1 ? "hour" : $"{IntervalMinutes / 60} hours")}" : $"every {IntervalMinutes} min",
        _ => "",
    };

    private static string Clock(TimeSpan? t) => t is { } v ? $"{(int)v.TotalHours:00}:{v.Minutes:00}" : "";

    /// <summary>When it's due, in words: "today at 17:00", "tomorrow at 09:00", "Mon 13 Oct at 10:00".</summary>
    public string WhenText(DateTime now)
    {
        string time = Due.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (Due.Date == now.Date) return $"today at {time}";
        if (Due.Date == now.Date.AddDays(1)) return $"tomorrow at {time}";
        return $"{Due.ToString("ddd d MMM", CultureInfo.CurrentCulture)} at {time}";
    }

    /// <summary>The next time after this one, for repeating reminders.</summary>
    public DateTime NextAfter(DateTime from)
    {
        switch (Repeat)
        {
            case "interval":
                var next = Due;
                if (IntervalMinutes <= 0) return from.AddHours(1);
                while (next <= from) next = next.AddMinutes(IntervalMinutes);
                return next;
            case "daily":
            case "weekdays":
            case "weekly":
                var t = TimeOfDay ?? Due.TimeOfDay;
                var day = from.Date.Add(t);
                if (day <= from) day = day.AddDays(1);
                for (int i = 0; i < 8; i++, day = day.AddDays(1))
                {
                    if (Repeat == "weekdays" && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                    if (Repeat == "weekly" && WeeklyDay is { } wd && day.DayOfWeek != wd) continue;
                    return day;
                }
                return day;
            default:
                return Due;
        }
    }
}

/// <summary>Saves and loads reminders (%AppData%\WinNotch\reminders.json).</summary>
public static class ReminderStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "reminders.json");

    public static List<Reminder> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(List<Reminder> reminders)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(reminders, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>
/// Understands typed reminders:
///   remind me in 20 min to check the oven · remind me at 5pm call mum · remind me tomorrow 9am bins ·
///   remind me on friday at 3pm dentist · every weekday at 9am stand-up · every day 8pm meds ·
///   every monday at 10am bins out · every 45 min stretch · every hour drink water
/// </summary>
public static class ReminderParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // 5pm, 5:30pm, 17:00, 9am, noon, midnight
    private const string TimePattern = @"(?<time>noon|midday|midnight|\d{1,2}(?::\d{2})?\s*(?:am|pm)|\d{1,2}:\d{2})";

    public static bool TryParse(string input, DateTime now, out Reminder reminder)
    {
        reminder = new Reminder();
        string t = Regex.Replace(input.Trim().TrimEnd('.', '!'), @"\s+", " ");

        // Timers: "timer 10 min", "set a timer for 25 minutes pasta", "10 min timer", "countdown 90 sec"
        var tm = Regex.Match(t, @"^(?:set\s+(?:a\s+)?)?(?:timer|countdown)(?:\s+for)?\s+(?<rest>.+)$", Opts);
        if (tm.Success) return ParseTimer(tm.Groups["rest"].Value, "", now, reminder);
        var tm2 = Regex.Match(t, @"^(?:set\s+(?:a\s+)?)?(?<dur>.+?)\s+(?:timer|countdown)\b(?:\s+(?:for\s+)?(?<label>.*))?$", Opts);
        if (tm2.Success && ParseTimer(tm2.Groups["dur"].Value, tm2.Groups["label"].Value, now, reminder, wholeDuration: true)) return true;

        bool every = Regex.IsMatch(t, @"^every\b", Opts);
        var lead = Regex.Match(t, @"^(?:remind me|reminder|remind)\b[:,]?\s*", Opts);
        if (!every && !lead.Success) return false;
        if (lead.Success) t = t[lead.Length..];
        if (Regex.IsMatch(t, @"^every\b", Opts)) every = true;

        return every ? ParseRepeating(t, now, reminder) : ParseOnce(t, now, reminder);
    }

    // ---------- Timers ----------

    private static bool ParseTimer(string t, string label, DateTime now, Reminder r, bool wholeDuration = false)
    {
        var m = Regex.Match(t.Trim(), @"^(?:(?<half>half an hour)|(?<an>an?) hour|(?:(?<h>\d+(?:\.\d+)?)\s*(?:hours?|hrs?|h)\b)?\s*(?:(?<m>\d+(?:\.\d+)?)\s*(?:minutes?|mins?|m)\b)?\s*(?:(?<s>\d+)\s*(?:seconds?|secs?|s)\b)?)", Opts);
        if (!m.Success || m.Value.Trim().Length == 0) return false;
        string after = t.Trim()[m.Length..].Trim();
        if (wholeDuration && after.Length > 0) return false;

        double seconds = 0;
        if (m.Groups["half"].Success) seconds = 1800;
        else if (m.Groups["an"].Success) seconds = 3600;
        else
        {
            if (m.Groups["h"].Success) seconds += double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600;
            if (m.Groups["m"].Success) seconds += double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60;
            if (m.Groups["s"].Success) seconds += int.Parse(m.Groups["s"].Value);
        }
        if (seconds < 1 || seconds > 24 * 3600) return false;

        r.IsTimer = true;
        r.TimerSeconds = (int)Math.Round(seconds);
        r.Due = now.AddSeconds(r.TimerSeconds);
        string text = (label.Length > 0 ? label : after).Trim();
        r.Text = text.Length == 0 ? "Timer" : Tidy(text);
        return true;
    }

    // ---------- One-off ----------

    private static bool ParseOnce(string t, DateTime now, Reminder r)
    {
        // "in 20 min", "in 2 hours", "in 1h30", "in an hour", "in half an hour"
        var m = Regex.Match(t, @"\bin\s+(?:(?<half>half an hour)|(?<an>an?)\s+hour|(?<n>\d+(?:\.\d+)?)\s*(?<unit>minutes?|mins?|m|hours?|hrs?|h|days?|d|seconds?|secs?|s)(?:\s*(?<n2>\d+)\s*(?:minutes?|mins?|m)?)?)\b", Opts);
        if (m.Success)
        {
            TimeSpan span;
            if (m.Groups["half"].Success) span = TimeSpan.FromMinutes(30);
            else if (m.Groups["an"].Success) span = TimeSpan.FromHours(1);
            else
            {
                double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                string unit = m.Groups["unit"].Value.ToLowerInvariant();
                span = unit[0] switch
                {
                    'h' => TimeSpan.FromHours(n),
                    'd' => TimeSpan.FromDays(n),
                    's' => TimeSpan.FromSeconds(n),
                    _ => TimeSpan.FromMinutes(n),
                };
                if (m.Groups["n2"].Success && unit[0] == 'h') span += TimeSpan.FromMinutes(int.Parse(m.Groups["n2"].Value));
            }
            if (span <= TimeSpan.Zero || span > TimeSpan.FromDays(366)) return false;
            r.Due = now.Add(span);
            r.Text = Tidy(t.Remove(m.Index, m.Length));
            return true;
        }

        // A day: today / tonight / tomorrow / on friday / next monday
        DateTime? date = null;
        var d = Regex.Match(t, @"\b(?:(?<today>today|tonight|this evening)|(?<tomorrow>tomorrow|tmrw|tmr)|(?:on\s+|next\s+)?(?<dow>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun))\b", Opts);
        string rest = t;
        if (d.Success)
        {
            if (d.Groups["today"].Success) date = now.Date;
            else if (d.Groups["tomorrow"].Success) date = now.Date.AddDays(1);
            else
            {
                var target = DayFrom(d.Groups["dow"].Value);
                int ahead = ((int)target - (int)now.DayOfWeek + 7) % 7;
                date = now.Date.AddDays(ahead);
            }
            rest = rest.Remove(d.Index, d.Length);
        }

        // A time: at 5pm / 17:00 / 9am
        TimeSpan? time = null;
        var tm = Regex.Match(rest, @"(?:\bat\s+)?\b" + TimePattern + @"(?![\w:])", Opts);
        if (tm.Success && TryTime(tm.Groups["time"].Value, out var parsed))
        {
            time = parsed;
            rest = rest.Remove(tm.Index, tm.Length);
        }

        if (date == null && time == null) return false;
        if (time == null)
            time = d.Success && Regex.IsMatch(d.Value, "tonight|evening", Opts) ? new TimeSpan(19, 0, 0) : new TimeSpan(9, 0, 0);
        var due = (date ?? now.Date).Add(time.Value);
        if (date == null && due <= now) due = due.AddDays(1); // "at 9am" said in the afternoon means tomorrow
        if (d.Success && d.Groups["dow"].Success && due <= now) due = due.AddDays(7); // "on friday" late on a Friday means next week
        if (due <= now) return false;

        r.Due = due;
        r.Text = Tidy(rest);
        return true;
    }

    // ---------- Repeating ----------

    private static bool ParseRepeating(string t, DateTime now, Reminder r)
    {
        // every 45 min / every 2 hours / every hour
        var iv = Regex.Match(t, @"^every\s+(?:(?<hour>hour)|(?<n>\d+)\s*(?<unit>minutes?|mins?|m|hours?|hrs?|h))\b", Opts);
        if (iv.Success)
        {
            int minutes = iv.Groups["hour"].Success ? 60
                : int.Parse(iv.Groups["n"].Value) * (iv.Groups["unit"].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? 60 : 1);
            if (minutes < 1 || minutes > 7 * 24 * 60) return false;
            r.Repeat = "interval";
            r.IntervalMinutes = minutes;
            r.Due = now.AddMinutes(minutes);
            r.Text = Tidy(t[iv.Length..]);
            return true;
        }

        // every day / every weekday / every monday ... (at a time)
        var dm = Regex.Match(t, @"^every\s+(?:(?<daily>day|morning|evening|night)|(?<weekdays>weekday|work ?day)s?|(?<dow>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun))\b", Opts);
        if (!dm.Success) return false;
        string rest = t[dm.Length..];

        TimeSpan time;
        var tm = Regex.Match(rest, @"(?:\bat\s+)?\b" + TimePattern + @"(?![\w:])", Opts);
        if (tm.Success && TryTime(tm.Groups["time"].Value, out var parsed))
        {
            time = parsed;
            rest = rest.Remove(tm.Index, tm.Length);
        }
        else
        {
            string word = dm.Groups["daily"].Value.ToLowerInvariant();
            time = word switch { "evening" => new TimeSpan(18, 0, 0), "night" => new TimeSpan(21, 0, 0), _ => new TimeSpan(9, 0, 0) };
        }

        if (dm.Groups["daily"].Success) r.Repeat = "daily";
        else if (dm.Groups["weekdays"].Success) r.Repeat = "weekdays";
        else
        {
            r.Repeat = "weekly";
            r.WeeklyDay = DayFrom(dm.Groups["dow"].Value);
        }
        r.TimeOfDay = time;
        r.Due = now.AddSeconds(-1); // so NextAfter finds the first one from now
        r.Due = r.NextAfter(now);
        r.Text = Tidy(rest);
        return true;
    }

    // ---------- Helpers ----------

    /// <summary>"17:00", "5pm", "5:30pm", "noon" → a time of day.</summary>
    public static bool TryTime(string s, out TimeSpan time)
    {
        time = default;
        s = s.Trim().ToLowerInvariant();
        if (s is "noon" or "midday") { time = new TimeSpan(12, 0, 0); return true; }
        if (s == "midnight") { time = TimeSpan.Zero; return true; }
        var m = Regex.Match(s, @"^(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?$");
        if (!m.Success) return false;
        int h = int.Parse(m.Groups["h"].Value), min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        if (m.Groups["ap"].Success)
        {
            if (h < 1 || h > 12) return false;
            if (m.Groups["ap"].Value == "pm" && h != 12) h += 12;
            if (m.Groups["ap"].Value == "am" && h == 12) h = 0;
        }
        else if (!m.Groups["m"].Success) return false; // a bare "5" isn't a time
        if (h > 23 || min > 59) return false;
        time = new TimeSpan(h, min, 0);
        return true;
    }

    private static DayOfWeek DayFrom(string s) => s.ToLowerInvariant()[..3] switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    // "  to check the oven " → "Check the oven"
    private static string Tidy(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '-', ':');
        s = Regex.Replace(s, @"^(?:to|that|about|for)\s+", "", Opts);
        s = Regex.Replace(s, @"\s+(?:to|at|on)$", "", Opts);
        if (s.Length == 0) return "Reminder";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
