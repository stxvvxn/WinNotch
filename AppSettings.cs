using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WinNotch;

/// <summary>Everything you can change in the Settings window. Saved to %AppData%\WinNotch\settings.json.</summary>
public sealed class AppSettings
{
    // Handheld mode (ROG Ally, Steam Deck, Legion Go...). Saved as "RogAlly" so older settings still load.
    [JsonPropertyName("RogAlly")]
    public bool Handheld { get; set; }
    public string HandheldDevice { get; set; } = "auto";   // auto / rogally / xboxally / steamdeck / legiongo / claw / other
    public string AllyHotkey { get; set; } = "Ctrl+Alt+N"; // opens the notch; map a back button to it
    public bool AutoProfile { get; set; } = true;          // switch Hz + power mode when plugging in / unplugging
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // General
    public bool StartWithWindows { get; set; }
    public bool HideInFullscreen { get; set; } = false; // off = always on top, even over full screen
    public string Size { get; set; } = "normal";       // small / normal / large
    public string Monitor { get; set; } = "primary";   // primary / mouse / a display name like \\.\DISPLAY2

    // Notch
    public bool ShowNowPlaying { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowSoundBars { get; set; } = true;
    public bool PeekOnTrackChange { get; set; } = true;

    // Pop-ups
    public bool VolumePopup { get; set; } = true;
    public bool BrightnessPopup { get; set; } = true;
    public bool ChargingPopup { get; set; } = true;
    public bool PrivacyDots { get; set; } = true;

    // Productivity
    public bool ClipboardHistory { get; set; } = true;
    public bool TimerSound { get; set; } = true;
    public string ScreenshotMethod { get; set; } = "snipping";              // snipping / sharex
    public string ScreenshotKeys { get; set; } = "Ctrl+PrintScreen";        // shortcut sent to ShareX

    // Bubbles around the open notch
    public bool ShowBubbles { get; set; } = true;
    public List<BubbleSetting> Bubbles { get; set; } = BubbleSetting.Defaults();

    // Theme
    public string AccentColor { get; set; } = "#FF9F0A";
    public string NotchStyle { get; set; } = "black";  // black / grey / glass / outline
    public bool AccentBars { get; set; } = false;      // colour the progress and volume bars too
    public string NotchWidth { get; set; } = "normal"; // narrow / normal / wide

    // Shelf image actions
    public bool ImageActions { get; set; } = true;
    public int ImageResizeWidth { get; set; } = 1080;
    public int JpegQuality { get; set; } = 80;
    public string ImageOutput { get; set; } = "beside"; // beside / folder (Pictures\WinNotch)

    // Mic mute
    public bool MicButton { get; set; } = true;
    public string MicHotkey { get; set; } = "Ctrl+Alt+Shift+M";
    public bool MicShowDot { get; set; } = true;
    public bool MicPopup { get; set; } = true;

    // Quick notes
    public bool NotesEnabled { get; set; } = true;
    public string NotesTextSize { get; set; } = "normal"; // small / normal / large

    // Calculator / converter
    public bool CalcEnabled { get; set; } = true;
    public bool CalcCurrency { get; set; } = true;
    public int CalcDecimals { get; set; } = 4;
    public bool CalcCopyOnEnter { get; set; } = true;

    // System stats
    public bool ShowStats { get; set; } = false;
    public bool StatsCpu { get; set; } = true;
    public bool StatsRam { get; set; } = true;
    public bool StatsNet { get; set; } = true;

    // Updates (new versions come from GitHub releases)
    public bool AutoUpdate { get; set; } = true;
    public string UpdateRepo { get; set; } = "stxvvxn/WinNotch";
    public string SkippedVersion { get; set; } = "";

    // Housekeeping
    public bool ClaudeActionsAdded { get; set; }
    public bool AllyActionsAdded { get; set; }
    public bool ExplorerActionsAdded { get; set; }
    public bool WorkAppActionsAdded { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // The old default was wrong; update anyone still on it
                if (loaded.ScreenshotKeys == "Ctrl+Shift+PrintScreen") loaded.ScreenshotKeys = "Ctrl+PrintScreen";
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>One bubble around the open notch: a folder, app or link to open.</summary>
public sealed class BubbleSetting
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";   // Segoe Fluent Icons code
    public string Target { get; set; } = ""; // folder, program or link
    public bool Enabled { get; set; } = true;
    // Tall layout (open notch with lots showing)
    public string Side { get; set; } = "";   // left / right / bottom
    public int Slot { get; set; }            // position on that side, from the top (or left)

    // Short layout (open notch with a little showing) - arranged separately
    public bool ShortEnabled { get; set; } = true;
    public string ShortSide { get; set; } = "";
    public int ShortSlot { get; set; }

    // Mini layout (open notch showing just the date - one bubble per side) - arranged separately
    public bool MiniEnabled { get; set; } = true;
    public string MiniSide { get; set; } = "";
    public int MiniSlot { get; set; }

    public bool IsShown(NotchLayout layout) => layout switch
    {
        NotchLayout.Short => ShortEnabled,
        NotchLayout.Mini => MiniEnabled,
        _ => Enabled,
    };

    public string SideIn(NotchLayout layout) => layout switch
    {
        NotchLayout.Short => ShortSide,
        NotchLayout.Mini => MiniSide,
        _ => Side,
    };

    public int SlotIn(NotchLayout layout) => layout switch
    {
        NotchLayout.Short => ShortSlot,
        NotchLayout.Mini => MiniSlot,
        _ => Slot,
    };

    public void SetShown(NotchLayout layout, bool shown)
    {
        switch (layout)
        {
            case NotchLayout.Short: ShortEnabled = shown; break;
            case NotchLayout.Mini: MiniEnabled = shown; break;
            default: Enabled = shown; break;
        }
    }

    public void Place(NotchLayout layout, string side, int slot)
    {
        switch (layout)
        {
            case NotchLayout.Short: ShortSide = side; ShortSlot = slot; break;
            case NotchLayout.Mini: MiniSide = side; MiniSlot = slot; break;
            default: Side = side; Slot = slot; break;
        }
    }

    /// <summary>Makes one layout the same as another.</summary>
    public void CopyLayout(NotchLayout from, NotchLayout to)
    {
        SetShown(to, IsShown(from));
        Place(to, SideIn(from), SlotIn(from));
    }

    public static List<BubbleSetting> Defaults() => new()
    {
        new() { Key = "camera",    Label = "Camera",    Icon = "E722", Side = "left",   Slot = 0, ShortSide = "left",   ShortSlot = 0, MiniSide = "left",   MiniSlot = 0, Target = "microsoft.windows.camera:" },
        new() { Key = "terminal",  Label = "Terminal",  Icon = "E756", Side = "right",  Slot = 0, ShortSide = "right",  ShortSlot = 0, MiniSide = "right",  MiniSlot = 0, Target = "wt.exe" },
        new() { Key = "pictures",  Label = "Pictures",  Icon = "E8B9", Side = "left",   Slot = 1, ShortSide = "left",   ShortSlot = 1, MiniSide = "left",   MiniSlot = 1, Target = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
        new() { Key = "documents", Label = "Documents", Icon = "E8A5", Side = "right",  Slot = 1, ShortSide = "right",  ShortSlot = 1, MiniSide = "right",  MiniSlot = 1, Target = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
        new() { Key = "music",     Label = "Music",     Icon = "E8D6", Side = "bottom", Slot = 0, ShortSide = "bottom", ShortSlot = 0, MiniSide = "bottom", MiniSlot = 0, Target = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
    };

    /// <summary>Gives a place to bubbles saved before they could be arranged.</summary>
    public static void Normalize(List<BubbleSetting> bubbles)
    {
        var defaults = Defaults().ToDictionary(d => d.Key);
        foreach (var b in bubbles.Where(b => string.IsNullOrEmpty(b.Side)))
        {
            if (defaults.TryGetValue(b.Key, out var d))
            {
                b.Side = d.Side;
                b.Slot = d.Slot;
            }
            else
            {
                b.Side = "bottom";
                b.Slot = 99;
            }
        }
        // Short starts as a copy of Tall, and Mini as a copy of Short
        foreach (var b in bubbles.Where(b => string.IsNullOrEmpty(b.ShortSide)))
            b.CopyLayout(NotchLayout.Tall, NotchLayout.Short);
        foreach (var b in bubbles.Where(b => string.IsNullOrEmpty(b.MiniSide)))
            b.CopyLayout(NotchLayout.Short, NotchLayout.Mini);

        foreach (var layout in BubbleLayout.All)
            BubbleLayout.Renumber(bubbles, layout);
    }
}

/// <summary>Refresh rate and power mode remembered for one game.</summary>
public sealed class GameProfile
{
    public string Name { get; set; } = "";
    public int RefreshRate { get; set; }
    public int PowerMode { get; set; } = 1; // 0 Efficiency, 1 Balanced, 2 Performance
}

/// <summary>Adds or removes WinNotch from the "start when I sign in" list for the current user.</summary>
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

            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\""); // refreshed every launch, so it follows the exe if you move it
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
