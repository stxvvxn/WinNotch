namespace WinNotch;

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
