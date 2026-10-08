using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinNotch;

// Themes: accent colour, notch style (black / graphite / glass / outline) and notch width.
public partial class MainWindow
{
    private Brush NotchBrush { get; set; } = Brushes.Black;

    public static readonly (string Name, string Hex)[] AccentChoices =
    {
        ("Orange", "#FF9F0A"), ("Blue", "#0A84FF"), ("Green", "#30D158"), ("Teal", "#64D2FF"),
        ("Purple", "#BF5AF2"), ("Pink", "#FF375F"), ("Red", "#FF453A"), ("Yellow", "#FFD60A"), ("White", "#FFFFFF"),
    };

    public static bool TryParseColour(string? hex, out Color colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            string h = hex.Trim();
            if (!h.StartsWith('#')) h = "#" + h;
            colour = (Color)ColorConverter.ConvertFromString(h);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTheme()
    {
        // Accent: open-tool buttons, timer, controller ring, and (optionally) the bars
        if (!TryParseColour(_settings.AccentColor, out var accent)) accent = Color.FromRgb(0xFF, 0x9F, 0x0A);
        AccentOrange.Color = accent;
        NavRing.BorderBrush = AccentOrange;
        CompactTimerIcon.Foreground = AccentOrange;
        TrackFill.Background = _settings.AccentBars ? AccentOrange : Brushes.White;
        HudFill.Background = _settings.AccentBars ? AccentOrange : Brushes.White;

        // Notch style
        NotchBrush = _settings.NotchStyle switch
        {
            "grey" => Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            "glass" => Frozen(Color.FromArgb(0xC8, 0x12, 0x12, 0x14)),
            _ => Brushes.Black,
        };
        Pill.Background = NotchBrush;
        bool outline = _settings.NotchStyle is "outline" or "glass";
        Pill.BorderBrush = outline ? Frozen(Color.FromArgb(_settings.NotchStyle == "outline" ? (byte)0x55 : (byte)0x33, 0xFF, 0xFF, 0xFF)) : null;
        Pill.BorderThickness = outline ? new Thickness(1, 0, 1, 1) : new Thickness(0);
        foreach (var b in _bubbles) b.Background = NotchBrush;

        // Notch width: re-settle to the new size
        if (!_hudShowing)
        {
            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(_expanded ? ExpandedW : CollapsedW, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (_expanded) AnimateExpandedHeight();
        }
    }
}
