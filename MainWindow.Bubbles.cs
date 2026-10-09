using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinNotch;

// Bubbles: round shortcut buttons that pop out from the sides and bottom of the open notch.
// What they open is set in Settings → Bubbles.
public partial class MainWindow
{
    private readonly List<Button> _bubbles = new();

    /// <summary>Tag on the bubbles that switch the notch to a page (system info, music, volume, shelf).</summary>
    private sealed record PageBubble(int Page);
    private readonly Dictionary<Button, (double X, double Y, double FromX, double FromY)> _bubbleSlots = new();

    /// <summary>(Re)creates the bubbles from Settings.</summary>
    private void BuildBubbles()
    {
        BubbleLayer.Children.Clear();
        _bubbles.Clear();
        _bubbleSlots.Clear();

        // Page bubbles first: they sit in the middle of the row under the notch
        var pages = EnabledPages();
        if (pages.Count > 1)
        {
            foreach (int page in pages)
            {
                var (glyph, name) = PageInfo(page);
                var button = MakeBubble(glyph, name, new PageBubble(page));
                int target = page;
                button.Click += (_, _) => ShowPage(target);
            }
            UpdatePageBubbles();
        }

        if (_settings.ShowBubbles)
        {
            foreach (var bubble in _settings.Bubbles.Where(b => (b.Enabled || b.ShortEnabled || b.MiniEnabled) && !string.IsNullOrWhiteSpace(b.Target)))
                MakeBubble(IconGlyph(bubble.Icon), bubble.Label, bubble).Click += Bubble_Click;
        }

        if (_expanded) ShowBubbles(true);
    }

    private static (string Glyph, string Name) PageInfo(int page) => page switch
    {
        1 => ("\uE767", "Volume"),
        2 => ("\uE7B8", "Shelf"),
        3 => ("\uE8D6", "Music"),
        _ => ("\uE9D9", "System info"),
    };

    /// <summary>The page you're on gets a filled bubble in the accent colour.</summary>
    private void UpdatePageBubbles()
    {
        foreach (var b in _bubbles)
        {
            if (b.Tag is not PageBubble pb) continue;
            if (pb.Page == _page)
            {
                b.SetResourceReference(BackgroundProperty, "Accent");
                b.Foreground = Brushes.Black;
            }
            else
            {
                b.Background = Pill.Background;
                b.Foreground = Brushes.White;
            }
        }
    }

    private Button MakeBubble(string glyph, string tip, object tag)
    {
        var scale = new ScaleTransform(0.4, 0.4);
        var move = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(move);
        var button = new Button
        {
            Style = (Style)FindResource("BubbleButton"),
            Content = glyph,
            ToolTip = tip,
            Tag = tag,
            Background = Pill.Background,
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transforms,
        };
        // Moving onto a bubble counts as still hovering the notch
        button.MouseEnter += Pill_MouseEnter;
        button.MouseLeave += Pill_MouseLeave;

        BubbleLayer.Children.Add(button);
        _bubbles.Add(button);
        return button;
    }

    private static string IconGlyph(string icon) =>
        int.TryParse(icon, System.Globalization.NumberStyles.HexNumber, null, out int code)
            ? char.ConvertFromUtf32(code)
            : ""; // "open in new window" as a fallback

    // ---------- Where each bubble goes ----------

    // Uses the arrangement from Settings → Bubbles (see BubbleLayout)
    private NotchLayout _bubbleLayout;

    private void PlanBubbles(double notchHeight)
    {
        _bubbleSlots.Clear();
        double notchLeft = (Root.ActualWidth - ExpandedW) / 2; // the notch is centred in the window

        // Tall, short and mini open notches each have their own layout
        _bubbleLayout = BubbleLayout.For(notchHeight);
        var visible = _bubbles.Select(b => b.Tag).OfType<BubbleSetting>().Where(b => b.IsShown(_bubbleLayout));
        var arranged = BubbleLayout.Arrange(visible, BubbleLayout.SideCapacity(notchHeight), _bubbleLayout);
        Button ButtonFor(BubbleSetting s) => _bubbles.First(b => ReferenceEquals(b.Tag, s));

        // Left and right columns
        foreach (var (bubble, side, index, _) in arranged.Where(a => a.Side != BubbleSide.Bottom))
        {
            var (x, y) = BubbleLayout.Position(side, index, 0, notchLeft, ExpandedW, notchHeight);
            var (fromX, fromY) = BubbleLayout.PopFrom(side);
            _bubbleSlots[ButtonFor(bubble)] = (x, y, fromX, fromY);
        }

        // Row underneath: the page bubbles in the middle, then your own bottom bubbles after a small gap
        var row = _bubbles.Where(b => b.Tag is PageBubble).ToList();
        int pageCount = row.Count;
        row.AddRange(arranged.Where(a => a.Side == BubbleSide.Bottom).OrderBy(a => a.Index).Select(a => ButtonFor(a.Bubble)));
        double split = pageCount > 0 && row.Count > pageCount ? 14 : 0;
        double width = row.Count * BubbleLayout.Size + Math.Max(0, row.Count - 1) * BubbleLayout.Gap + split;
        double left = notchLeft + ExpandedW / 2 - (pageCount > 0 ? (pageCount * BubbleLayout.Size + (pageCount - 1) * BubbleLayout.Gap) / 2 : width / 2);
        var (popX, popY) = BubbleLayout.PopFrom(BubbleSide.Bottom);
        for (int i = 0; i < row.Count; i++)
        {
            double x = left + i * (BubbleLayout.Size + BubbleLayout.Gap) + (i >= pageCount ? split : 0);
            _bubbleSlots[row[i]] = (x, notchHeight + BubbleLayout.Gap, popX, popY);
        }
    }

    // ---------- Pop out / tuck away ----------

    private void ShowBubbles(bool show)
    {
        if (_bubbles.Count == 0) return;
        if (show) PlanBubbles(ExpandedHeight);
        PopBubbles(show);
    }

    // Pops out every bubble in the current plan and tucks away the rest
    private void PopBubbles(bool show = true)
    {

        for (int i = 0; i < _bubbles.Count; i++)
        {
            var b = _bubbles[i];
            var group = (TransformGroup)b.RenderTransform;
            var scale = (ScaleTransform)group.Children[0];
            var move = (TranslateTransform)group.Children[1];

            if (show && _bubbleSlots.TryGetValue(b, out var slot))
            {
                Canvas.SetLeft(b, slot.X);
                Canvas.SetTop(b, slot.Y);
                b.BeginAnimation(Canvas.LeftProperty, null);
                b.BeginAnimation(Canvas.TopProperty, null);

                // Start tucked against the notch, then spring outwards, one after another
                var delay = TimeSpan.FromMilliseconds(130 + i * 40);
                var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
                var time = TimeSpan.FromMilliseconds(380);

                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slot.FromX, 0, time) { BeginTime = delay, EasingFunction = spring });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slot.FromY, 0, time) { BeginTime = delay, EasingFunction = spring });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, time) { BeginTime = delay, EasingFunction = spring });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, time) { BeginTime = delay, EasingFunction = spring });
                b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = delay });
                b.IsHitTestVisible = true;
            }
            else
            {
                // Tuck back into the notch quickly
                var time = TimeSpan.FromMilliseconds(150);
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                if (_bubbleSlots.TryGetValue(b, out var s))
                {
                    move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(s.FromX, time) { EasingFunction = ease });
                    move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(s.FromY, time) { EasingFunction = ease });
                }
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, time) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, time) { EasingFunction = ease });
                b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
                b.IsHitTestVisible = false;
            }
        }
    }

    // The notch grew or shrank while open: glide the bubbles to their new spots
    private void MoveBubbles(double notchHeight)
    {
        if (!_expanded || _bubbles.Count == 0) return;

        // Switched to a different layout (tall / short / mini): swap the bubbles over
        if (BubbleLayout.For(notchHeight) != _bubbleLayout)
        {
            PlanBubbles(notchHeight);
            PopBubbles();
            return;
        }

        PlanBubbles(notchHeight);
        var time = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var b in _bubbles)
        {
            if (!_bubbleSlots.TryGetValue(b, out var slot)) continue;
            b.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(slot.X, time) { EasingFunction = ease });
            b.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(slot.Y, time) { EasingFunction = ease });
        }
    }

    // ---------- Opening things ----------

    private void Bubble_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BubbleSetting bubble) return;
        string target = Environment.ExpandEnvironmentVariables(bubble.Target.Trim().Trim('"'));

        if (!TryOpen(target) && !(bubble.Key == "terminal" && TryOpen("powershell.exe")))
            DebugLog.Write($"couldn't open bubble {bubble.Label}: {target}");

        // Get out of the way of whatever just opened
        _hovering = false;
        SetExpanded(false);
    }

    private static bool TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
