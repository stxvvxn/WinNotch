using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinNotch;

// Themes (same choices as WinNotch v1): accent colour, notch style and notch width.
public partial class MainWindow
{
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

    /// <summary>The notch's background for a style (also used by the Settings preview).</summary>
    public static Brush NotchBrushFor(string style) => style switch
    {
        "grey" => Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E)),
        "glass" => Frozen(Color.FromArgb(0xC8, 0x12, 0x12, 0x14)),
        _ => Brushes.Black,
    };

    private void ApplyTheme()
    {
        if (!TryParseColour(_settings.AccentColor, out var accent)) accent = Color.FromRgb(0xFF, 0x9F, 0x0A);
        Resources["Accent"] = new SolidColorBrush(accent);

        // Notch background, and the boxes inside it to match
        string style = _settings.NotchStyle;
        Pill.Background = NotchBrushFor(style);
        Resources["Surface"] = new SolidColorBrush(style switch
        {
            "grey" => Color.FromRgb(0x2C, 0x2C, 0x2E),
            "glass" => Color.FromArgb(0x99, 0x2C, 0x2C, 0x2E),
            _ => Color.FromRgb(0x1C, 0x1C, 0x1E),
        });
        Resources["SurfaceHover"] = new SolidColorBrush(style switch
        {
            "grey" => Color.FromRgb(0x3A, 0x3A, 0x3C),
            "glass" => Color.FromArgb(0xBB, 0x3A, 0x3A, 0x3C),
            _ => Color.FromRgb(0x2C, 0x2C, 0x2E),
        });
        bool outline = style is "outline" or "glass";
        Pill.BorderBrush = outline ? Frozen(Color.FromArgb(style == "outline" ? (byte)0x55 : (byte)0x33, 0xFF, 0xFF, 0xFF)) : null;
        Pill.BorderThickness = outline ? new Thickness(1, 0, 1, 1) : new Thickness(0);
        if (_results.Count > 0) Highlight();

        // Width: settle at the new size
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(_expanded ? ExpandedW : CollapsedW, TimeSpan.FromMilliseconds(260))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
