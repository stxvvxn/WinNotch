using System.Management;

namespace WinNotch;

/// <summary>A gaming handheld that handheld mode knows about.</summary>
public sealed record HandheldDevice(string Key, string Name, string ButtonTip, string[] Matches);

/// <summary>The handhelds WinNotch knows, and working out which one it's running on.</summary>
public static class Handhelds
{
    public static readonly HandheldDevice[] All =
    {
        new("rogally", "ASUS ROG Ally / Ally X",
            "Map a back button to Ctrl+Alt+N in Armoury Crate SE → Control Mode → Gamepad → Back buttons.",
            new[] { "ROG Ally", "RC71L", "RC72L" }),
        new("xboxally", "ASUS ROG Xbox Ally / Ally X",
            "Map a back button to Ctrl+Alt+N in Armoury Crate SE → Control Mode → Back buttons.",
            new[] { "Xbox Ally", "RC73" }),
        new("steamdeck", "Valve Steam Deck (Windows)",
            "With Steam open, set a back grip button to Ctrl+Alt+N in Steam → Controller settings → Desktop layout. View + Menu works once a controller driver is running.",
            new[] { "Jupiter", "Galileo", "Steam Deck" }),
        new("legiongo", "Lenovo Legion Go / Go S",
            "Map a back paddle to Ctrl+Alt+N in Legion Space → Controller → Button remapping.",
            new[] { "Legion Go", "83E1", "83L3", "83N6", "83Q2", "83Q3" }),
        new("claw", "MSI Claw",
            "Map an M button to Ctrl+Alt+N in MSI Center M → Gaming mode → Macro keys.",
            new[] { "Claw" }),
        new("other", "Another handheld",
            "Map any spare button to Ctrl+Alt+N in your handheld's controller app, or hold View + Menu on an Xbox-style controller.",
            Array.Empty<string>()),
    };

    public static HandheldDevice ByKey(string key) =>
        All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? All[^1];

    private static HandheldDevice? _detected;
    private static bool _detectedChecked;

    /// <summary>The handheld this PC appears to be, or null if it doesn't look like one.</summary>
    public static HandheldDevice? Detect()
    {
        if (_detectedChecked) return _detected;
        _detectedChecked = true;
        try
        {
            var text = new List<string>();
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (var o in s.Get()) { text.Add($"{o["Manufacturer"]}"); text.Add($"{o["Model"]}"); }
            using (var s = new ManagementObjectSearcher("SELECT Name, Version FROM Win32_ComputerSystemProduct"))
                foreach (var o in s.Get()) { text.Add($"{o["Name"]}"); text.Add($"{o["Version"]}"); }
            string all = string.Join(" | ", text);

            // Xbox Ally first: its model name also contains "ROG ... Ally"
            _detected = All.Where(d => d.Matches.Length > 0)
                           .OrderBy(d => d.Key == "xboxally" ? 0 : 1)
                           .FirstOrDefault(d => d.Matches.Any(m => all.Contains(m, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            _detected = null;
        }
        return _detected;
    }

    /// <summary>The device chosen in Settings, or the detected one when set to automatic.</summary>
    public static HandheldDevice Current(AppSettings settings) =>
        settings.HandheldDevice == "auto" ? Detect() ?? ByKey("other") : ByKey(settings.HandheldDevice);
}
