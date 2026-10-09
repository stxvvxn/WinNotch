using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinNotch;

/// <summary>A worked-out answer: what to show, what Enter copies, and a short explanation.</summary>
public readonly record struct CalcAnswer(string Result, string Copy, string Detail);

/// <summary>
/// The quick calculator: sums (12*4+3, 2^10, sqrt(81), 15% of 80, 200 + 20%),
/// unit conversions (5 ft in cm, 70 f to c, 2 gb in mb) and currencies (20 usd to gbp, £50 in eur).
/// </summary>
public static class Calculator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static CalcAnswer? Evaluate(string input, int decimals)
    {
        string text = Wordy(input.Trim());
        if (text.Length == 0) return null;

        if (TryConvertUnits(text, decimals) is { } converted) return converted;

        if (TryArithmetic(text, out double value))
            return Answer(value, decimals, "", "");

        return null;
    }

    /// <summary>
    /// Turns typed-out maths into symbols: "what is 12 times 4?" → "12 * 4",
    /// "square root of 144" → "sqrt(144)", "5 squared" → "5^2", "2 to the power of 8" → "2^8".
    /// </summary>
    public static string Wordy(string text)
    {
        string t = " " + text.Trim().TrimEnd('?', '=', '.', '!').ToLowerInvariant() + " ";
        foreach (var lead in new[] { " what is ", " what's ", " whats ", " calculate ", " work out ", " solve ", " how much is ", " = " })
            if (t.StartsWith(lead)) t = " " + t[lead.Length..];
        t = Regex.Replace(t, @"\bsquare root of\s+([\d.,]+|\([^)]*\))", "sqrt($1)");
        t = Regex.Replace(t, @"\bcube root of\s+([\d.,]+|\([^)]*\))", "cbrt($1)");
        t = Regex.Replace(t, @"\bsqrt\s+of\s+", "sqrt ");
        t = Regex.Replace(t, @"\s+squared\b", "^2");
        t = Regex.Replace(t, @"\s+cubed\b", "^3");
        t = Regex.Replace(t, @"\s+to the power of\s+", "^");
        t = Regex.Replace(t, @"\s+(multiplied by|times)\s+", " * ");
        t = Regex.Replace(t, @"\s+(divided by|over)\s+", " / ");
        t = Regex.Replace(t, @"\s+plus\s+", " + ");
        t = Regex.Replace(t, @"\s+(minus|take away)\s+", " - ");
        t = Regex.Replace(t, @"\s+percent\b", "%");
        t = Regex.Replace(t, @"(\d)\s*x\s*(\d)", "$1*$2"); // 3x4 or 3 x 4
        return t.Trim();
    }

    private static CalcAnswer Answer(double value, int decimals, string unit, string detail)
    {
        double rounded = Math.Round(value, Math.Clamp(decimals, 0, 10));
        string shown = rounded.ToString("#,0." + new string('#', Math.Clamp(decimals, 0, 10)), CultureInfo.CurrentCulture);
        string copy = rounded.ToString("0." + new string('#', Math.Clamp(decimals, 0, 10)), Inv);
        if (unit.Length > 0) shown += " " + unit;
        return new CalcAnswer(shown, copy, detail);
    }

    // ================= Arithmetic =================

    public static bool TryArithmetic(string text, out double value)
    {
        value = 0;
        try
        {
            var parser = new Parser(Clean(text));
            value = parser.ParseExpression();
            if (!parser.AtEnd || double.IsNaN(value) || double.IsInfinity(value)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Clean(string text) =>
        text.ToLowerInvariant()
            .Replace("£", "").Replace("$", "").Replace("€", "").Replace("¥", "")
            .Replace(",", "")
            .Replace("**", "^")
            .Replace("×", "*").Replace("÷", "/").Replace("−", "-");

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public Parser(string s) => _s = s;
        public bool AtEnd { get { Skip(); return _i >= _s.Length; } }

        private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private bool Eat(string token)
        {
            Skip();
            if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0) return false;
            _i += token.Length;
            return true;
        }

        // a + b, a - b (with "200 + 20%" meaning 200 plus 20% of 200)
        public double ParseExpression()
        {
            double left = ParseTerm(out _);
            while (true)
            {
                if (Eat("+")) { double r = ParseTerm(out bool pct); left = pct ? left * (1 + r) : left + r; }
                else if (Eat("-")) { double r = ParseTerm(out bool pct); left = pct ? left * (1 - r) : left - r; }
                else return left;
            }
        }

        // a * b, a / b, a x b, "15% of 80", a mod b
        private double ParseTerm(out bool lonePercent)
        {
            double left = ParsePower(out lonePercent);
            while (true)
            {
                if (Eat("*") || EatWord("x")) { left *= ParsePower(out _); lonePercent = false; }
                else if (Eat("/")) { left /= ParsePower(out _); lonePercent = false; }
                else if (EatWord("of")) { left *= ParsePower(out _); lonePercent = false; }
                else if (EatWord("mod")) { left %= ParsePower(out _); lonePercent = false; }
                else return left;
            }
        }

        private bool EatWord(string word)
        {
            Skip();
            int save = _i;
            if (!Eat(word)) return false;
            if (_i < _s.Length && char.IsLetter(_s[_i])) { _i = save; return false; }
            return true;
        }

        private double ParsePower(out bool percent)
        {
            double b = ParseUnary(out percent);
            if (Eat("^")) { b = Math.Pow(b, ParsePower(out _)); percent = false; }
            return b;
        }

        private double ParseUnary(out bool percent)
        {
            if (Eat("-")) return -ParseUnary(out percent);
            if (Eat("+")) return ParseUnary(out percent);
            double v = ParsePrimary();
            percent = false;
            if (Eat("%")) { v /= 100; percent = true; }
            else if (Eat("!")) v = Factorial(v);
            return v;
        }

        private double ParsePrimary()
        {
            Skip();
            if (Eat("("))
            {
                double v = ParseExpression();
                if (!Eat(")")) throw new FormatException();
                return v;
            }

            foreach (var (name, fn) in Functions)
            {
                int save = _i;
                if (EatWord(name))
                {
                    if (Eat("(")) { double v = ParseExpression(); if (!Eat(")")) throw new FormatException(); return fn(v); }
                    return fn(ParseUnary(out _));
                }
                _i = save;
            }
            if (EatWord("pi") || Eat("π")) return Math.PI;
            if (EatWord("e")) return Math.E;

            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException();
            double num = double.Parse(_s[start.._i], Inv);
            // 5k, 2m
            if (_i < _s.Length && (_s[_i] == 'k') && (_i + 1 >= _s.Length || !char.IsLetter(_s[_i + 1]))) { _i++; num *= 1_000; }
            return num;
        }

        private static readonly (string, Func<double, double>)[] Functions =
        {
            ("sqrt", Math.Sqrt), ("cbrt", Math.Cbrt), ("abs", Math.Abs), ("round", Math.Round),
            ("floor", Math.Floor), ("ceil", Math.Ceiling), ("log", Math.Log10), ("ln", Math.Log),
            ("sin", d => Math.Sin(d * Math.PI / 180)), ("cos", d => Math.Cos(d * Math.PI / 180)), ("tan", d => Math.Tan(d * Math.PI / 180)),
        };

        private static double Factorial(double n)
        {
            if (n < 0 || n > 170 || n != Math.Floor(n)) throw new FormatException();
            double r = 1;
            for (int k = 2; k <= n; k++) r *= k;
            return r;
        }
    }

    // ================= Units =================

    private sealed record Unit(string Kind, string Name, double Factor);

    private static readonly Dictionary<string, Unit> Units = BuildUnits();

    private static Dictionary<string, Unit> BuildUnits()
    {
        var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string name, double factor, params string[] aliases)
        {
            var u = new Unit(kind, name, factor);
            d[name] = u;
            foreach (var a in aliases) d[a] = u;
        }

        // Length (metres)
        Add("length", "mm", 0.001, "millimetre", "millimetres", "millimeter", "millimeters");
        Add("length", "cm", 0.01, "centimetre", "centimetres", "centimeter", "centimeters");
        Add("length", "m", 1, "metre", "metres", "meter", "meters");
        Add("length", "km", 1000, "kilometre", "kilometres", "kilometer", "kilometers", "kms");
        Add("length", "in", 0.0254, "inch", "inches", "\"");
        Add("length", "ft", 0.3048, "foot", "feet", "'");
        Add("length", "yd", 0.9144, "yard", "yards", "yds");
        Add("length", "mi", 1609.344, "mile", "miles");
        Add("length", "nmi", 1852, "nautical mile", "nautical miles");

        // Weight (kilograms)
        Add("weight", "mg", 1e-6, "milligram", "milligrams");
        Add("weight", "g", 0.001, "gram", "grams", "gr");
        Add("weight", "kg", 1, "kilo", "kilos", "kilogram", "kilograms", "kgs");
        Add("weight", "t", 1000, "tonne", "tonnes", "ton", "tons");
        Add("weight", "oz", 0.028349523125, "ounce", "ounces");
        Add("weight", "lb", 0.45359237, "lbs", "pound", "pounds");
        Add("weight", "st", 6.35029318, "stone", "stones");

        // Volume (litres) - pints and gallons are UK unless you say US
        Add("volume", "ml", 0.001, "millilitre", "millilitres", "milliliter", "milliliters");
        Add("volume", "cl", 0.01, "centilitre", "centilitres");
        Add("volume", "L", 1, "litre", "litres", "liter", "liters", "ltr");
        Add("volume", "tsp", 0.00492892, "teaspoon", "teaspoons");
        Add("volume", "tbsp", 0.0147868, "tablespoon", "tablespoons");
        Add("volume", "fl oz", 0.0284131, "floz", "fluid ounce", "fluid ounces");
        Add("volume", "cup", 0.2365882, "cups");
        Add("volume", "pint", 0.568261, "pints", "pt");
        Add("volume", "US pint", 0.473176, "us pints", "us pt");
        Add("volume", "gallon", 4.54609, "gallons", "gal");
        Add("volume", "US gallon", 3.785411784, "us gallons", "us gal");

        // Speed (metres per second)
        Add("speed", "m/s", 1, "mps");
        Add("speed", "km/h", 1 / 3.6, "kmh", "kph", "kmph");
        Add("speed", "mph", 0.44704, "miles per hour");
        Add("speed", "knots", 0.514444, "knot", "kn", "kt");

        // Data (bytes; KB/MB/GB are 1000s, KiB/MiB/GiB are 1024s)
        Add("data", "bit", 0.125, "bits");
        Add("data", "bytes", 1, "byte", "b");
        Add("data", "KB", 1e3, "kilobyte", "kilobytes");
        Add("data", "MB", 1e6, "megabyte", "megabytes");
        Add("data", "GB", 1e9, "gigabyte", "gigabytes", "gig", "gigs");
        Add("data", "TB", 1e12, "terabyte", "terabytes");
        Add("data", "KiB", 1024, "kibibyte");
        Add("data", "MiB", 1048576, "mebibyte");
        Add("data", "GiB", 1073741824, "gibibyte");
        Add("data", "TiB", 1099511627776, "tebibyte");
        Add("data", "Mbit", 125000, "megabit", "megabits");
        Add("data", "Gbit", 125000000, "gigabit", "gigabits");

        // Time (seconds)
        Add("time", "ms", 0.001, "millisecond", "milliseconds");
        Add("time", "s", 1, "sec", "secs", "second", "seconds");
        Add("time", "min", 60, "mins", "minute", "minutes");
        Add("time", "h", 3600, "hr", "hrs", "hour", "hours");
        Add("time", "days", 86400, "day", "d");
        Add("time", "weeks", 604800, "week", "wk", "wks");
        Add("time", "years", 31557600, "year", "yr", "yrs");

        // Temperature (handled separately)
        Add("temp", "°C", 0, "c", "celsius", "centigrade", "degc", "deg c");
        Add("temp", "°F", 0, "f", "fahrenheit", "degf", "deg f");
        Add("temp", "K", 0, "k", "kelvin");
        return d;
    }

    private static readonly Regex ConvertPattern = new(
        @"^(?<amount>.*?[\d)πie])\s*(?<from>[a-z°""'][a-z°/ ""']*?)\s+(?:in|to|into|as|=|->)\s+(?<to>[a-z°""'][a-z°/ ""']*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Unit? FindUnit(string raw)
    {
        string key = Regex.Replace(raw.Trim().Replace("°", "").Replace("degrees ", "").Replace("degree ", ""), @"\s+", " ");
        return Units.TryGetValue(key, out var u) ? u : null;
    }

    private static CalcAnswer? TryConvertUnits(string text, int decimals)
    {
        var m = ConvertPattern.Match(text);
        if (!m.Success) return null;
        var from = FindUnit(m.Groups["from"].Value);
        var to = FindUnit(m.Groups["to"].Value);
        if (from == null || to == null) return null;
        if (!TryArithmetic(m.Groups["amount"].Value, out double amount)) return null;
        if (from.Kind != to.Kind)
            return new CalcAnswer("", "", $"Can't turn {from.Kind} into {to.Kind}");

        double result = from.Kind == "temp"
            ? FromKelvin(ToKelvin(amount, from.Name), to.Name)
            : amount * from.Factor / to.Factor;

        string kind = from.Kind switch { "temp" => "temperature", _ => from.Kind };
        string amountText = amount.ToString("#,0.####", CultureInfo.CurrentCulture);
        return Answer(result, decimals, to.Name, $"{amountText} {from.Name} in {to.Name} · {kind}");
    }

    private static double ToKelvin(double v, string unit) => unit switch
    {
        "°C" => v + 273.15,
        "°F" => (v - 32) * 5 / 9 + 273.15,
        _ => v,
    };

    private static double FromKelvin(double k, string unit) => unit switch
    {
        "°C" => k - 273.15,
        "°F" => (k - 273.15) * 9 / 5 + 32,
        _ => k,
    };

    // ================= Currency (rates from the European Central Bank via frankfurter) =================

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, (DateTime Fetched, Dictionary<string, double> Rates)> RateCache = new();

    private static readonly Dictionary<string, string> CurrencyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["£"] = "GBP", ["pound"] = "GBP", ["pounds"] = "GBP", ["quid"] = "GBP", ["sterling"] = "GBP", ["gbp"] = "GBP",
        ["$"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["bucks"] = "USD", ["usd"] = "USD",
        ["€"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR", ["eur"] = "EUR",
        ["¥"] = "JPY", ["yen"] = "JPY",
    };

    private static readonly Regex CurrencyPattern = new(
        @"^(?<sym>[£$€¥])?\s*(?<amount>[\d.,]+\s*k?)\s*(?<from>[a-z£$€¥]{1,9})?\s+(?:in|to|into|as|=|->)\s+(?<to>[a-z£$€¥]{1,9})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? CurrencyCode(string word)
    {
        word = word.Trim();
        if (CurrencyWords.TryGetValue(word, out var code)) return code;
        return Regex.IsMatch(word, "^[a-zA-Z]{3}$") ? word.ToUpperInvariant() : null;
    }

    private static bool TryParseCurrency(string text, out double amount, out string from, out string to)
    {
        amount = 0; from = to = "";
        var m = CurrencyPattern.Match(text.Trim());
        if (!m.Success) return false;
        string? f = m.Groups["from"].Success ? CurrencyCode(m.Groups["from"].Value)
                  : m.Groups["sym"].Success ? CurrencyCode(m.Groups["sym"].Value) : null;
        string? t = CurrencyCode(m.Groups["to"].Value);
        if (f == null || t == null) return false;
        if (!TryArithmetic(m.Groups["amount"].Value, out amount)) return false;
        from = f; to = t;
        return true;
    }

    public static bool LooksLikeCurrency(string text) => TryParseCurrency(text, out _, out _, out _);

    public static async Task<CalcAnswer?> ConvertCurrencyAsync(string text, int decimals)
    {
        if (!TryParseCurrency(text, out double amount, out string from, out string to)) return null;
        if (from == to) return Answer(amount, 2, to, "Same currency");

        var rates = await GetRatesAsync(from);
        if (rates == null) return new CalcAnswer("", "", "Couldn't get exchange rates (offline?)");
        if (!rates.TryGetValue(to, out double rate)) return new CalcAnswer("", "", $"Unknown currency: {to}");

        var answer = Answer(amount * rate, Math.Min(decimals, 2), to, $"1 {from} = {rate.ToString("0.####", CultureInfo.CurrentCulture)} {to} · ECB rate");
        return answer;
    }

    private static async Task<Dictionary<string, double>?> GetRatesAsync(string from)
    {
        if (RateCache.TryGetValue(from, out var cached) && DateTime.UtcNow - cached.Fetched < TimeSpan.FromHours(6))
            return cached.Rates;

        foreach (var url in new[] { $"https://api.frankfurter.dev/v1/latest?base={from}", $"https://api.frankfurter.app/latest?from={from}" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [from] = 1 };
                foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject())
                    rates[p.Name] = p.Value.GetDouble();
                RateCache[from] = (DateTime.UtcNow, rates);
                return rates;
            }
            catch { }
        }
        return null;
    }
}
