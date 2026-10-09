using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace WinNotch;

/// <summary>One calendar event, in local time.</summary>
public sealed record CalEvent(string Title, DateTime Start, DateTime End, bool AllDay, string Location);

/// <summary>
/// Reads calendars from ICS links (Outlook "publish calendar", Google "secret address in iCal format", iCloud...).
/// Handles time zones, all-day events, repeating events (daily / weekly / monthly / yearly), skipped and moved ones.
/// </summary>
public static class IcsCalendar
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<List<CalEvent>> FetchAsync(IEnumerable<string> urls, DateTime from, DateTime to)
    {
        var all = new List<CalEvent>();
        foreach (var raw in urls)
        {
            string url = raw.Trim();
            if (url.Length == 0) continue;
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            try
            {
                string ics = await Http.GetStringAsync(url);
                all.AddRange(Parse(ics, from, to));
            }
            catch (Exception ex)
            {
                DebugLog.Write($"calendar fetch failed: {ex.Message}");
            }
        }
        return all.OrderBy(e => e.Start).ToList();
    }

    // ---------- Parsing ----------

    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    /// <summary>A date/time as written in the file: wall-clock time plus how to read it.</summary>
    private readonly record struct IcsTime(DateTime Wall, bool Utc, TimeZoneInfo? Zone, bool DateOnly)
    {
        public DateTime ToLocal()
        {
            if (DateOnly) return Wall.Date;
            if (Utc) return DateTime.SpecifyKind(Wall, DateTimeKind.Utc).ToLocalTime();
            if (Zone != null)
            {
                try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(Wall, DateTimeKind.Unspecified), Zone, TimeZoneInfo.Local); }
                catch { return Wall; }
            }
            return Wall; // "floating" time: same clock time wherever you are
        }

        public IcsTime At(DateTime wall) => this with { Wall = wall };
    }

    public static List<CalEvent> Parse(string ics, DateTime from, DateTime to)
    {
        var events = new List<Dictionary<string, List<Prop>>>();
        Dictionary<string, List<Prop>>? current = null;
        int depth = 0; // skip VALARM etc. inside an event

        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new(StringComparer.OrdinalIgnoreCase); depth = 0; continue; }
            if (current == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0) { depth--; continue; }
                events.Add(current);
                current = null;
                continue;
            }
            if (depth > 0) continue;
            var p = ParseLine(line);
            if (p == null) continue;
            if (!current.TryGetValue(p.Name, out var list)) current[p.Name] = list = new();
            list.Add(p);
        }

        // Moved / changed single occurrences of a repeating event: UID + RECURRENCE-ID
        var overrides = new HashSet<(string, DateTime)>();
        foreach (var e in events)
        {
            if (Get(e, "RECURRENCE-ID") is { } rid && TryTime(rid, out var t))
                overrides.Add((Get(e, "UID")?.Value ?? "", Minute(t.ToLocal())));
        }

        var result = new List<CalEvent>();
        foreach (var e in events)
        {
            try
            {
                if (string.Equals(Get(e, "STATUS")?.Value, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                if (Get(e, "DTSTART") is not { } ds || !TryTime(ds, out var start)) continue;

                // How long it lasts
                TimeSpan length;
                if (Get(e, "DTEND") is { } de && TryTime(de, out var end)) length = end.ToLocal() - start.ToLocal();
                else if (Get(e, "DURATION") is { } du && TryDuration(du.Value, out var d)) length = d;
                else length = start.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero;
                if (length < TimeSpan.Zero) length = TimeSpan.Zero;

                string title = Unescape(Get(e, "SUMMARY")?.Value ?? "");
                if (title.Length == 0) title = "Busy";
                string location = Unescape(Get(e, "LOCATION")?.Value ?? "");
                string uid = Get(e, "UID")?.Value ?? "";
                bool isOverride = Get(e, "RECURRENCE-ID") != null;

                var excluded = new HashSet<DateTime>();
                if (e.TryGetValue("EXDATE", out var exdates))
                    foreach (var ex in exdates)
                        foreach (var v in ex.Value.Split(','))
                            if (TryTime(new Prop { Name = "EXDATE", Params = ex.Params, Value = v }, out var xt))
                                excluded.Add(Minute(xt.ToLocal()));

                IEnumerable<IcsTime> starts = Get(e, "RRULE") is { } rr && !isOverride
                    ? Expand(start, rr.Value, to)
                    : new[] { start };

                foreach (var s in starts)
                {
                    var localStart = s.ToLocal();
                    if (localStart > to) break;
                    var localEnd = localStart + length;
                    if (localEnd <= from && !(length == TimeSpan.Zero && localStart >= from)) continue;
                    if (localEnd < from) continue;
                    if (excluded.Contains(Minute(localStart))) continue;
                    if (!isOverride && Get(e, "RRULE") != null && overrides.Contains((uid, Minute(localStart)))) continue;
                    result.Add(new CalEvent(title, localStart, localEnd, start.DateOnly, location));
                }
            }
            catch { /* one odd event shouldn't stop the rest */ }
        }
        return result.OrderBy(r => r.Start).ToList();
    }

    private static DateTime Minute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

    private static Prop? Get(Dictionary<string, List<Prop>> e, string name) =>
        e.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;

    private static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        // NAME;PARAM=x;PARAM="y:z":value  (colons can appear inside quoted params)
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var p = new Prop { Name = head[0].Trim(), Value = line[(colon + 1)..] };
        foreach (var part in head.Skip(1))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) p.Params[part[..eq]] = part[(eq + 1)..].Trim('"');
        }
        return p;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static bool TryTime(Prop p, out IcsTime time)
    {
        time = default;
        string v = p.Value.Trim();
        bool dateOnly = v.Length == 8 || string.Equals(p.Params.GetValueOrDefault("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase);
        if (dateOnly)
        {
            if (!DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
            time = new IcsTime(d, false, null, true);
            return true;
        }
        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return false;
        TimeZoneInfo? zone = null;
        if (!utc && p.Params.TryGetValue("TZID", out var tzid)) zone = FindZone(tzid);
        time = new IcsTime(t, utc, zone, false);
        return true;
    }

    private static readonly Dictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    private static TimeZoneInfo? FindZone(string id)
    {
        id = id.Trim().Trim('"').TrimStart('/');
        lock (Zones)
        {
            if (Zones.TryGetValue(id, out var cached)) return cached;
            TimeZoneInfo? zone = null;
            // Windows names ("GMT Standard Time") and IANA names ("Europe/London") both work on Windows
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            if (zone == null)
            {
                // Some calendars use names like "(UTC+00:00) Dublin, Edinburgh, Lisbon, London"
                zone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
                    string.Equals(z.DisplayName, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(z.StandardName, id, StringComparison.OrdinalIgnoreCase));
            }
            Zones[id] = zone; // null = treat as local time
            return zone;
        }
    }

    private static bool TryDuration(string s, out TimeSpan d)
    {
        d = TimeSpan.Zero;
        var m = Regex.Match(s.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return false;
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        d = new TimeSpan(N(2) * 7 + N(3), N(4), N(5), N(6));
        if (m.Groups[1].Value == "-") d = -d;
        return true;
    }

    // ---------- Repeating events ----------

    private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    private static IEnumerable<IcsTime> Expand(IcsTime start, string rrule, DateTime to)
    {
        var rule = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].ToUpperInvariant(), kv => kv[1].ToUpperInvariant());

        string freq = rule.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out int iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out int c) ? c : null;
        DateTime? until = null;
        if (rule.TryGetValue("UNTIL", out var u) && TryTime(new Prop { Value = u }, out var ut))
            until = ut.DateOnly ? ut.Wall.Date.AddDays(1).AddTicks(-1) : ut.ToLocal();

        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',') : Array.Empty<string>();
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd)
            ? bmd.Split(',').Select(x => int.TryParse(x, out int n) ? n : 0).Where(n => n != 0).ToArray()
            : Array.Empty<int>();
        var byMonth = rule.TryGetValue("BYMONTH", out var bm)
            ? bm.Split(',').Select(x => int.TryParse(x, out int n) ? n : 0).Where(n => n is >= 1 and <= 12).ToArray()
            : Array.Empty<int>();

        var wall = start.Wall;
        var timeOfDay = wall.TimeOfDay;
        int produced = 0;
        int guard = 0;

        IEnumerable<DateTime> Candidates()
        {
            switch (freq)
            {
                case "DAILY":
                    for (var d = wall.Date; d <= to.Date.AddDays(2); d = d.AddDays(interval))
                    {
                        if (byDay.Length > 0 && !byDay.Any(x => x.EndsWith(DayCodes[(int)d.DayOfWeek]))) continue;
                        if (byMonth.Length > 0 && !byMonth.Contains(d.Month)) continue;
                        yield return d + timeOfDay;
                    }
                    yield break;
                case "WEEKLY":
                    var days = byDay.Length > 0
                        ? byDay.Select(x => Array.IndexOf(DayCodes, x[^2..])).Where(i => i >= 0).Distinct().ToList()
                        : new List<int> { (int)wall.DayOfWeek };
                    // Weeks start on Monday (the ICS default)
                    var weekStart = wall.Date.AddDays(-(((int)wall.DayOfWeek + 6) % 7));
                    for (var w = weekStart; w <= to.AddDays(8); w = w.AddDays(7 * interval))
                        foreach (int dow in days.OrderBy(x => (x + 6) % 7))
                            yield return w.AddDays((dow + 6) % 7) + timeOfDay;
                    yield break;
                case "MONTHLY":
                    for (var m = new DateTime(wall.Year, wall.Month, 1); m <= to.AddMonths(1); m = m.AddMonths(interval))
                        foreach (var d in MonthDays(m, byDay, byMonthDay.Length > 0 ? byMonthDay : (byDay.Length == 0 ? new[] { wall.Day } : Array.Empty<int>())))
                            yield return d + timeOfDay;
                    yield break;
                case "YEARLY":
                    for (int y = wall.Year; y <= to.Year + 1; y += interval)
                    {
                        var months = byMonth.Length > 0 ? byMonth : new[] { wall.Month };
                        foreach (int month in months)
                        {
                            var m = new DateTime(y, month, 1);
                            var md = byMonthDay.Length > 0 ? byMonthDay : (byDay.Length == 0 ? new[] { wall.Day } : Array.Empty<int>());
                            foreach (var d in MonthDays(m, byDay, md))
                                yield return d + timeOfDay;
                        }
                    }
                    yield break;
                default:
                    yield return wall;
                    yield break;
            }
        }

        foreach (var candidate in Candidates())
        {
            if (++guard > 20000) yield break;
            if (candidate < wall) continue; // before the first one
            var t = start.At(candidate);
            var local = t.ToLocal();
            if (until is { } un && local > un) yield break;
            if (count is { } n && produced >= n) yield break;
            if (local > to) yield break;
            produced++;
            yield return t;
        }
    }

    /// <summary>Days in a month matching BYDAY (e.g. 2TU, -1FR, MO) and/or BYMONTHDAY (e.g. 15, -1).</summary>
    private static IEnumerable<DateTime> MonthDays(DateTime month, string[] byDay, int[] byMonthDay)
    {
        int daysIn = DateTime.DaysInMonth(month.Year, month.Month);
        var set = new SortedSet<DateTime>();
        foreach (int md in byMonthDay)
        {
            int day = md > 0 ? md : daysIn + md + 1;
            if (day >= 1 && day <= daysIn) set.Add(new DateTime(month.Year, month.Month, day));
        }
        foreach (var code in byDay)
        {
            var m = Regex.Match(code, @"^([+-]?\d+)?(SU|MO|TU|WE|TH|FR|SA)$");
            if (!m.Success) continue;
            int dow = Array.IndexOf(DayCodes, m.Groups[2].Value);
            var matches = Enumerable.Range(1, daysIn).Select(d => new DateTime(month.Year, month.Month, d))
                .Where(d => (int)d.DayOfWeek == dow).ToList();
            if (m.Groups[1].Success)
            {
                int nth = int.Parse(m.Groups[1].Value);
                int idx = nth > 0 ? nth - 1 : matches.Count + nth;
                if (idx >= 0 && idx < matches.Count) set.Add(matches[idx]);
            }
            else
            {
                foreach (var d in matches)
                    if (byMonthDay.Length == 0 || byMonthDay.Contains(d.Day)) set.Add(d);
            }
        }
        return set;
    }
}
