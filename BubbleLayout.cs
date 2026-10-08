namespace WinNotch;

public enum BubbleSide { Left, Right, Bottom }

/// <summary>The open notch comes in three heights, and each has its own bubble arrangement.</summary>
public enum NotchLayout { Tall, Short, Mini }

/// <summary>
/// Where bubbles sit around the open notch. Used by the notch itself and by the Settings preview,
/// so what you arrange in Settings is exactly what you get.
/// </summary>
public static class BubbleLayout
{
    public const double Size = 42, Gap = 10, Step = 48, Top = 10;

    public static readonly NotchLayout[] All = { NotchLayout.Tall, NotchLayout.Short, NotchLayout.Mini };

    /// <summary>Which layout to use: decided by how many bubbles fit down each side.</summary>
    public static NotchLayout For(double notchHeight) => SideCapacity(notchHeight) switch
    {
        >= 3 => NotchLayout.Tall,   // lots showing (music, shelf…)
        2 => NotchLayout.Short,     // a little showing
        _ => NotchLayout.Mini,      // just the date
    };

    /// <summary>How many bubbles fit down each side of an open notch this tall (at least 1).</summary>
    public static int SideCapacity(double notchHeight) =>
        Math.Max(1, (int)((notchHeight - Top - Size) / Step) + 1);

    public static BubbleSide SideOf(BubbleSetting b, NotchLayout layout) => b.SideIn(layout) switch
    {
        "right" => BubbleSide.Right,
        "bottom" => BubbleSide.Bottom,
        _ => BubbleSide.Left,
    };

    /// <summary>
    /// Places the visible bubbles. Each side takes as many as fit; any that don't fit
    /// (when the notch is short) move to the row underneath, after the ones that live there.
    /// </summary>
    public static List<(BubbleSetting Bubble, BubbleSide Side, int Index, bool Overflow)> Arrange(
        IEnumerable<BubbleSetting> visible, int perSide, NotchLayout layout)
    {
        var list = visible.ToList();
        List<BubbleSetting> On(BubbleSide side) =>
            list.Where(b => SideOf(b, layout) == side).OrderBy(b => b.SlotIn(layout)).ToList();

        var left = On(BubbleSide.Left);
        var right = On(BubbleSide.Right);
        var bottom = On(BubbleSide.Bottom);

        var result = new List<(BubbleSetting, BubbleSide, int, bool)>();
        for (int i = 0; i < Math.Min(perSide, left.Count); i++) result.Add((left[i], BubbleSide.Left, i, false));
        for (int i = 0; i < Math.Min(perSide, right.Count); i++) result.Add((right[i], BubbleSide.Right, i, false));

        int row = 0;
        foreach (var b in bottom) result.Add((b, BubbleSide.Bottom, row++, false));
        foreach (var b in left.Skip(perSide)) result.Add((b, BubbleSide.Bottom, row++, true));
        foreach (var b in right.Skip(perSide)) result.Add((b, BubbleSide.Bottom, row++, true));
        return result;
    }

    /// <summary>Top-left corner of a bubble, in the same units as the notch measurements.</summary>
    public static (double X, double Y) Position(BubbleSide side, int index, int bottomCount,
                                               double notchLeft, double notchWidth, double notchHeight)
    {
        switch (side)
        {
            case BubbleSide.Left:
                return (notchLeft - Gap - Size, Top + index * Step);
            case BubbleSide.Right:
                return (notchLeft + notchWidth + Gap, Top + index * Step);
            default:
                double rowWidth = bottomCount * Size + Math.Max(0, bottomCount - 1) * Gap;
                double rowLeft = notchLeft + notchWidth / 2 - rowWidth / 2;
                return (rowLeft + index * (Size + Gap), notchHeight + Gap);
        }
    }

    /// <summary>Offset a bubble starts from when it pops out (tucked against the notch).</summary>
    public static (double X, double Y) PopFrom(BubbleSide side) => side switch
    {
        BubbleSide.Left => (Gap + Size, 0),
        BubbleSide.Right => (-(Gap + Size), 0),
        _ => (0, -(Gap + Size)),
    };

    /// <summary>Tidies slot numbers so each side counts 0, 1, 2… in its current order.</summary>
    public static void Renumber(List<BubbleSetting> all, NotchLayout layout)
    {
        foreach (var side in new[] { "left", "right", "bottom" })
        {
            int slot = 0;
            foreach (var b in all.Where(b => b.SideIn(layout) == side).OrderBy(b => b.SlotIn(layout)).ToList())
                b.Place(layout, side, slot++);
        }
    }
}
