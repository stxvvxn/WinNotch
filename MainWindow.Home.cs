using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNotch;

// The default page's extras under the quote: weather and next calendar event side by side,
// a day-progress bar with the week number, and quick toggles (see MainWindow.Toggles.cs).
public partial class MainWindow
{
    private readonly DispatcherTimer _homeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private WeatherNow? _weather;
    private bool _weatherFailed, _weatherLoading;
    private List<CalEvent> _events = new();
    private DateTime _eventsFetched = DateTime.MinValue;
    private string _eventsSource = "";
    private bool _eventsLoading, _eventsFailed;

    private void InitHome()
    {
        // Weather every 20 minutes, calendar every 15 (checked once a minute)
        _homeTimer.Tick += (_, _) => RefreshHomeData();
        _homeTimer.Start();
        RefreshHomeData();
    }

    /// <summary>Settings changed: show / hide the pieces, and reload anything whose source changed.</summary>
    private void ApplyHome()
    {
        string source = string.Join("\n", _settings.CalendarUrls);
        if (source != _eventsSource)
        {
            _eventsSource = source;
            _events.Clear();
            _eventsFetched = DateTime.MinValue;
            _eventsFailed = false;
        }
        BuildQuickToggles();
        RefreshHomeData();
        UpdateHome(DateTime.Now);
    }

    private void RefreshHomeData()
    {
        if (_settings.WeatherEnabled && _settings.WeatherLat is not null &&
            (_weather == null || DateTime.Now - _weather.Fetched > TimeSpan.FromMinutes(20)))
            _ = LoadWeatherAsync();
        if (_settings.CalendarEnabled && _settings.CalendarUrls.Count > 0 &&
            DateTime.Now - _eventsFetched > TimeSpan.FromMinutes(15))
            _ = LoadEventsAsync();
    }

    // ---------- Weather ----------

    private async Task LoadWeatherAsync()
    {
        if (_weatherLoading || _settings.WeatherLat is not { } lat || _settings.WeatherLon is not { } lon) return;
        _weatherLoading = true;
        try
        {
            _weather = await WeatherService.GetAsync(lat, lon, _settings.WeatherFahrenheit);
            _weatherFailed = false;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"weather failed: {ex.Message}");
            _weatherFailed = true;
        }
        finally
        {
            _weatherLoading = false;
        }
        UpdateHome(DateTime.Now);
    }

    /// <summary>Settings → Home page → Find: look up the town and start showing its weather.</summary>
    public async Task<string?> SetWeatherPlaceAsync(string query)
    {
        try
        {
            var place = await WeatherService.FindPlaceAsync(query);
            if (place is not { } p) return null;
            _settings.WeatherPlace = p.Name;
            _settings.WeatherLat = p.Lat;
            _settings.WeatherLon = p.Lon;
            _settings.Save();
            _weather = null;
            await LoadWeatherAsync();
            return p.Name;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"weather place lookup failed: {ex.Message}");
            return null;
        }
    }

    public void ReloadWeather()
    {
        _weather = null;
        _ = LoadWeatherAsync();
    }

    private void ShowWeather()
    {
        if (_settings.WeatherLat is null)
        {
            WeatherIcon.Text = "⛅";
            WeatherMain.Text = "Weather";
            WeatherSub.Text = "Set your town in Settings";
            WeatherTile.ToolTip = "Settings → Home page → Weather";
            return;
        }
        if (_weather is not { } w)
        {
            WeatherIcon.Text = "⛅";
            WeatherMain.Text = _weatherFailed ? "Weather unavailable" : "Loading weather…";
            WeatherSub.Text = _weatherFailed ? "Trying again soon" : _settings.WeatherPlace;
            WeatherTile.ToolTip = null;
            return;
        }
        var (symbol, text) = WeatherService.Describe(w.Code, w.IsDay);
        WeatherIcon.Text = symbol;
        WeatherMain.Text = $"{Math.Round(w.Temp):0}°  {text}";
        string highLow = double.IsNaN(w.High) ? "" : $"H {Math.Round(w.High):0}° · L {Math.Round(w.Low):0}°";
        string rain = w.RainChance > 0 ? $" · {w.RainChance}% rain" : "";
        WeatherSub.Text = highLow + rain;
        WeatherTile.ToolTip = $"{_settings.WeatherPlace} · updated {w.Fetched:HH:mm} · click for the forecast";
    }

    private void WeatherTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (_settings.WeatherLat is null)
        {
            OpenSettings();
            return;
        }
        string place = _settings.WeatherPlace.Split(',')[0];
        Run(() => WebTools.SearchWeb(_settings, $"weather {place}"));
    }

    // ---------- Calendar ----------

    private async Task LoadEventsAsync()
    {
        if (_eventsLoading) return;
        _eventsLoading = true;
        try
        {
            var now = DateTime.Now;
            var urls = _settings.CalendarUrls.ToList();
            _events = await IcsCalendar.FetchAsync(urls, now.Date.AddDays(-1), now.Date.AddDays(8));
            _eventsFetched = DateTime.Now;
            _eventsFailed = false;
            DebugLog.Write($"calendar: {_events.Count} events in the next week");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"calendar failed: {ex.Message}");
            _eventsFailed = true;
            _eventsFetched = DateTime.Now.AddMinutes(-10); // try again in 5 minutes
        }
        finally
        {
            _eventsLoading = false;
        }
        UpdateHome(DateTime.Now);
    }

    public void ReloadCalendar()
    {
        _eventsFetched = DateTime.MinValue;
        _ = LoadEventsAsync();
    }

    /// <summary>What the calendar tile shows: something on now, else the next thing coming up.</summary>
    private CalEvent? FeaturedEvent(DateTime now)
    {
        var timed = _events.Where(e => !e.AllDay).ToList();
        return timed.FirstOrDefault(e => e.Start <= now && e.End > now)                    // on now
               ?? timed.FirstOrDefault(e => e.Start > now && e.Start.Date == now.Date)      // later today
               ?? _events.FirstOrDefault(e => e.AllDay && e.Start.Date == now.Date)         // all day today
               ?? _events.FirstOrDefault(e => e.Start > now);                               // later on
    }

    /// <summary>The next timed event's start, for the countdown on the closed notch.</summary>
    private CalEvent? NextCalendarEvent(DateTime now) =>
        _settings.CalendarEnabled ? _events.FirstOrDefault(e => !e.AllDay && e.Start > now) : null;

    private void ShowCalendar(DateTime now)
    {
        if (_eventsFetched == DateTime.MinValue && !_eventsFailed)
        {
            CalendarMain.Text = "Calendar";
            CalendarSub.Text = "Loading…";
            CalendarTile.ToolTip = null;
            return;
        }
        if (_eventsFailed && _events.Count == 0)
        {
            CalendarMain.Text = "Calendar unavailable";
            CalendarSub.Text = "Check the link in Settings";
            CalendarTile.ToolTip = null;
            return;
        }
        var e = FeaturedEvent(now);
        if (e == null)
        {
            CalendarMain.Text = "Nothing coming up";
            CalendarSub.Text = "Your next 7 days are clear";
            CalendarTile.ToolTip = null;
            return;
        }

        CalendarMain.Text = e.Title;
        CalendarSub.Text = EventWhen(e, now);
        var upcoming = _events.Where(x => x.End > now).Take(5)
            .Select(x => $"{EventWhen(x, now, short_: true)}   {x.Title}{(x.Location.Length > 0 ? $"  ({x.Location})" : "")}");
        CalendarTile.ToolTip = string.Join("\n", upcoming);
    }

    private static string EventWhen(CalEvent e, DateTime now, bool short_ = false)
    {
        string Day(DateTime d) => d.Date == now.Date ? "Today" : d.Date == now.Date.AddDays(1) ? "Tomorrow" : d.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        if (e.AllDay) return $"{Day(e.Start)} · all day";
        if (e.Start <= now && e.End > now) return short_ ? $"Now – {e.End:HH:mm}" : $"Now · until {e.End:HH:mm}";
        string when = $"{Day(e.Start)} {e.Start:HH:mm}";
        if (short_ || e.Start.Date != now.Date) return when;
        var left = e.Start - now;
        string inText = left.TotalMinutes < 60 ? $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min" : $"in {(int)Math.Round(left.TotalHours)} hr";
        return $"{when} · {inText}";
    }

    private void CalendarTile_Click(object sender, MouseButtonEventArgs e)
    {
        // Open the calendar the link came from
        string url = _settings.CalendarUrls.FirstOrDefault() ?? "";
        string? open = url.Contains("google.com", StringComparison.OrdinalIgnoreCase) ? "https://calendar.google.com"
            : url.Contains("outlook.live.com", StringComparison.OrdinalIgnoreCase) ? "https://outlook.live.com/calendar"
            : url.Contains("outlook.office", StringComparison.OrdinalIgnoreCase) || url.Contains("office365", StringComparison.OrdinalIgnoreCase) ? "https://outlook.office.com/calendar"
            : url.Contains("icloud.com", StringComparison.OrdinalIgnoreCase) ? "https://www.icloud.com/calendar"
            : null;
        if (open != null) Run(() => WebTools.Open(open));
    }

    // ---------- Day progress ----------

    private void ShowDayProgress(DateTime now)
    {
        int week = ISOWeek.GetWeekOfYear(now);
        string weekText = _settings.ShowWeekNumber ? $"Week {week} · " : "";
        double fraction;
        string right;

        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (_settings.DayProgressMode == "work" && !weekend
            && ReminderParser.TryTime(_settings.WorkStart, out var startT) && ReminderParser.TryTime(_settings.WorkEnd, out var endT) && endT > startT)
        {
            var start = now.Date + startT;
            var end = now.Date + endT;
            if (now < start)
            {
                fraction = 0;
                right = $"Work starts at {start:HH:mm}";
            }
            else if (now >= end)
            {
                fraction = 1;
                right = "Done for the day";
            }
            else
            {
                fraction = (now - start) / (end - start);
                right = $"{fraction:0%} · {Span(end - now)} left";
            }
            DayProgressLeft.Text = $"{weekText}Work day";
        }
        else
        {
            fraction = now.TimeOfDay.TotalHours / 24;
            right = $"{fraction:0%} of the day";
            DayProgressLeft.Text = $"{weekText}{now:dddd}";
        }

        DayProgressRight.Text = right;
        DayDoneCol.Width = new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star);
        DayLeftCol.Width = new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star);
    }

    private static string Span(TimeSpan t)
    {
        int mins = (int)Math.Ceiling(t.TotalMinutes);
        if (mins < 60) return $"{mins} min";
        return mins % 60 == 0 ? $"{mins / 60} hr" : $"{mins / 60} hr {mins % 60} min";
    }

    // ---------- Putting it together ----------

    /// <summary>Refresh what the home extras show (called every second while the notch is open).</summary>
    private void UpdateHome(DateTime now)
    {
        bool weather = _settings.WeatherEnabled;
        bool calendar = _settings.CalendarEnabled && _settings.CalendarUrls.Count > 0;
        bool before = GlanceRow.Visibility == Visibility.Visible;
        bool beforeWeather = WeatherTile.Visibility == Visibility.Visible, beforeCal = CalendarTile.Visibility == Visibility.Visible;
        bool beforeDay = DayProgressArea.Visibility == Visibility.Visible;

        WeatherTile.Visibility = Vis(weather);
        CalendarTile.Visibility = Vis(calendar);
        GlanceRow.Visibility = Vis(weather || calendar);
        // One on its own takes the full width
        Grid.SetColumn(CalendarTile, weather ? 1 : 0);
        Grid.SetColumnSpan(CalendarTile, weather ? 1 : 2);
        Grid.SetColumnSpan(WeatherTile, calendar ? 1 : 2);

        if (weather) ShowWeather();
        if (calendar) ShowCalendar(now);

        DayProgressArea.Visibility = Vis(_settings.DayProgress);
        if (_settings.DayProgress) ShowDayProgress(now);

        if (before != (weather || calendar) || beforeWeather != weather || beforeCal != calendar || beforeDay != _settings.DayProgress)
            AnimateHeight();
    }
}
