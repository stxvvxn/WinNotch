using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WinNotch;

/// <summary>Everything in the Settings window. Saved to %AppData%\WinNotch\settings.json.</summary>
public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; }
    public bool HideInFullscreen { get; set; } = false;
    public string Monitor { get; set; } = "primary";   // primary / mouse / a display name like \\.\DISPLAY2
    public string Size { get; set; } = "normal";       // small / normal / large
    public bool ShowSeconds { get; set; } = false;
    public bool AutoUpdate { get; set; } = true;               // check GitHub for new versions
    public string UpdateRepo { get; set; } = "stxvvxn/WinNotch";
    public string SkippedVersion { get; set; } = "";
    public bool ShowQuote { get; set; } = true;       // quote of the day under the search box

    // Home page extras (Settings → Home page)
    public bool WeatherEnabled { get; set; } = true;
    public string WeatherPlace { get; set; } = "";     // "Bristol, GB" - found with Settings → Find
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public bool WeatherFahrenheit { get; set; } = false;
    public bool CalendarEnabled { get; set; } = true;
    public List<string> CalendarUrls { get; set; } = new(); // ICS links (Outlook / Google / iCloud)
    public bool CalendarCountdown { get; set; } = true;    // closed notch counts down to the next event too
    public bool DayProgress { get; set; } = true;
    public string DayProgressMode { get; set; } = "work";  // work / day
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "17:30";
    public bool ShowWeekNumber { get; set; } = true;
    public bool QuickToggles { get; set; } = true;
    public bool ToggleMic { get; set; } = true;
    public bool ToggleDnd { get; set; } = true;
    public bool ToggleTheme { get; set; } = true;
    public bool ToggleBluetooth { get; set; } = true;
    public bool ToggleAwake { get; set; } = true;
    public bool ToggleLock { get; set; } = true;

    // Theme (same options as WinNotch v1)
    public string AccentColor { get; set; } = "#FF9F0A";
    public string NotchStyle { get; set; } = "black";  // black / grey / glass / outline
    public string NotchWidth { get; set; } = "normal"; // narrow / normal / wide
    public bool AccentBars { get; set; } = false;      // colour the usage bars with the accent

    // Search
    public string SearchHotkey { get; set; } = "Ctrl+Alt+Space";
    public bool UseWindowsSearch { get; set; } = true;  // the whole PC, through the Windows Search index
    public List<string> SearchFolders { get; set; } = new()
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };
    public string WebEngine { get; set; } = "google";   // see WebTools.Engines
    public string CustomWebUrl { get; set; } = "https://www.google.com/search?q={query}";
    public string AiTool { get; set; } = "claude";      // see WebTools.AiTools
    public string CustomAiUrl { get; set; } = "https://claude.ai/new?q={query}";
    public bool ClaudeDesktopApp { get; set; } = true;  // open Claude in the desktop app when it's installed

    // System info
    public bool ShowSystemInfo { get; set; } = true;
    public bool CompactStats { get; set; } = false;  // one line instead of tiles
    public bool VolumePage { get; set; } = true;     // scroll down in the open notch for app volumes

    // Bubbles: round shortcuts that pop out around the open notch (arranged in Settings → Bubbles)
    public bool ShowBubbles { get; set; } = true;
    public List<BubbleSetting> Bubbles { get; set; } = BubbleSetting.Defaults();

    // Music page (second page)
    public bool MusicPage { get; set; } = true;
    public string MusicService { get; set; } = "auto"; // auto / spotify / applemusic / youtubemusic / amazon / tidal / deezer
    public bool MusicExtras { get; set; } = true;      // service buttons like Spotify's Like and Smart Shuffle

    // Shelf (the page after volume)
    public bool ShelfPage { get; set; } = true;
    public bool ShelfAutoOpen { get; set; } = true;        // dragging a file onto the notch opens the shelf
    public bool ShelfRemember { get; set; } = true;        // keep the shelf between restarts
    public bool ShelfThumbnails { get; set; } = true;      // pictures show a preview instead of an icon
    public bool ShelfRemoveAfterDrag { get; set; } = false; // take things off the shelf once dragged out
    public bool ShelfScratch { get; set; } = true;          // scratch pad under the shelf
    public bool ShelfScreenshots { get; set; } = true;      // screenshots (Snipping Tool, ShareX...) go on the shelf
    public bool ShelfScreenshotPeek { get; set; } = true;   // ...and the notch pops open to show it
    public bool RemindersEnabled { get; set; } = true;      // "remind me in 20 min ..." in the search box
    public bool ReminderSound { get; set; } = true;         // play a sound when one is due
    public bool ReminderOverFullscreen { get; set; } = true; // show due reminders over games / full-screen video
    public int ReminderCountdownMinutes { get; set; } = 60; // closed notch shows "in 12 min" this close to one (0 = off)
    public bool StatCpu { get; set; } = true;
    public bool StatMemory { get; set; } = true;
    public bool StatGpu { get; set; } = true;
    public bool StatDisk { get; set; } = true;
    public List<string> DiskDrives { get; set; } = new() { "C" }; // click the disk tile to switch between them
    public bool StatNetwork { get; set; } = true;
    public bool StatBattery { get; set; } = true;
    public bool StatUptime { get; set; } = true;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                BubbleSetting.Normalize(loaded.Bubbles);
                return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Adds or removes WinNotch from "start when I sign in" (also tidies away the old test copy's entry).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinNotch";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue("WinNotch" + "V2", throwOnMissingValue: false); // the version 2 test copy's old entry
        }
        catch { }
    }
}

/// <summary>Turns "Ctrl+Alt+Space" into RegisterHotKey modifiers and a key.</summary>
internal static class Hotkeys
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B,
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C, ["`"] = 0xC0, ["/"] = 0xBF, ["."] = 0xBE, [","] = 0xBC,
    };

    public static bool TryParse(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint vk;
            if (Named.TryGetValue(raw, out uint named)) vk = named;
            else if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
            else if (raw.Length > 1 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out int f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
            else return false;

            switch (vk)
            {
                case 0x12: modifiers |= 1; break;
                case 0x11: modifiers |= 2; break;
                case 0x10: modifiers |= 4; break;
                case 0x5B: modifiers |= 8; break;
                default: key = vk; break;
            }
        }
        return key != 0;
    }
}
