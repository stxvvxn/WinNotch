using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WinNotch;

public sealed record WeatherNow(double Temp, int Code, bool IsDay, double High, double Low, int RainChance, DateTime Fetched);

/// <summary>Weather from Open-Meteo (free, no account or key needed).</summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Finds a town: "Bristol", "Paris, France", "Portland, US". Returns null if nothing matches.</summary>
    public static async Task<(double Lat, double Lon, string Name)?> FindPlaceAsync(string query)
    {
        var parts = query.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string name = parts[0];
        string? hint = parts.Length > 1 ? parts[1] : null;

        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=10&language=en&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;

        JsonElement pick = results[0];
        if (hint != null)
        {
            foreach (var r in results.EnumerateArray())
            {
                string Str(string p) => r.TryGetProperty(p, out var v) ? v.GetString() ?? "" : "";
                if (new[] { Str("country"), Str("country_code"), Str("admin1"), Str("admin2") }
                    .Any(x => x.Length > 0 && (x.Equals(hint, StringComparison.OrdinalIgnoreCase) ||
                                               x.StartsWith(hint, StringComparison.OrdinalIgnoreCase) ||
                                               (hint.Equals("UK", StringComparison.OrdinalIgnoreCase) && x == "GB"))))
                {
                    pick = r;
                    break;
                }
            }
        }

        double lat = pick.GetProperty("latitude").GetDouble();
        double lon = pick.GetProperty("longitude").GetDouble();
        string label = pick.GetProperty("name").GetString() ?? name;
        if (pick.TryGetProperty("country_code", out var cc) && cc.GetString() is { Length: > 0 } code) label += $", {code}";
        return (lat, lon, label);
    }

    public static async Task<WeatherNow?> GetAsync(double lat, double lon, bool fahrenheit)
    {
        string url = "https://api.open-meteo.com/v1/forecast" +
                     $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                     "&current=temperature_2m,weather_code,is_day" +
                     "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                     "&timezone=auto&forecast_days=1" +
                     (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");

        static double First(JsonElement daily, string name) =>
            daily.TryGetProperty(name, out var arr) && arr.GetArrayLength() > 0 && arr[0].ValueKind == JsonValueKind.Number ? arr[0].GetDouble() : double.NaN;

        double rain = First(daily, "precipitation_probability_max");
        return new WeatherNow(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            !current.TryGetProperty("is_day", out var day) || day.GetInt32() == 1,
            First(daily, "temperature_2m_max"),
            First(daily, "temperature_2m_min"),
            double.IsNaN(rain) ? -1 : (int)rain,
            DateTime.Now);
    }

    /// <summary>A symbol (Segoe UI Symbol) and words for a WMO weather code.</summary>
    public static (string Symbol, string Text) Describe(int code, bool isDay) => code switch
    {
        0 => (isDay ? "☀" : "☾", isDay ? "Sunny" : "Clear"),
        1 => (isDay ? "☀" : "☾", "Mostly clear"),
        2 => ("⛅", "Partly cloudy"),
        3 => ("☁", "Cloudy"),
        45 or 48 => ("☁", "Fog"),
        51 or 53 or 55 => ("☂", "Drizzle"),
        56 or 57 => ("☂", "Freezing drizzle"),
        61 => ("☂", "Light rain"),
        63 => ("☂", "Rain"),
        65 => ("☂", "Heavy rain"),
        66 or 67 => ("☂", "Freezing rain"),
        71 => ("❄", "Light snow"),
        73 => ("❄", "Snow"),
        75 => ("❄", "Heavy snow"),
        77 => ("❄", "Snow grains"),
        80 => ("☂", "Showers"),
        81 => ("☂", "Heavy showers"),
        82 => ("☂", "Violent showers"),
        85 or 86 => ("❄", "Snow showers"),
        95 => ("⛈", "Thunderstorm"),
        96 or 99 => ("⛈", "Thunderstorm, hail"),
        _ => ("☁", "—"),
    };
}
