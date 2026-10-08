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
    private readonly Dictionary<Button, (double X, double Y, double FromX, double FromY)> _bubbleSlots = new();

    /// <summary>(Re)creates the bubbles from Settings.</summary>
    private void BuildBubbles()
    {
        BubbleLayer.Children.Clear();
        _bubbles.Clear();
        _bubbleSlots.Clear();
        if (!_settings.ShowBubbles) return;

        foreach (var bubble in _settings.Bubbles.Where(b => (b.Enabled || b.ShortEnabled || b.MiniEnabled) && !string.IsNullOrWhiteSpace(b.Target)))
        {
            var scale = new ScaleTransform(0.4, 0.4);
            var move = new TranslateTransform();
            var transforms = new TransformGroup();
            transforms.Children.Add(scale);
            transforms.Children.Add(move);
            var button = new Button
            {
                Style = (Style)FindResource("BubbleButton"),
                Content = IconGlyph(bubble.Icon),
                ToolTip = bubble.Label,
                Tag = bubble,
                Background = NotchBrush,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transforms,
            };
            button.Click += Bubble_Click;
            // Moving onto a bubble counts as still hovering the notch
            button.MouseEnter += Pill_MouseEnter;
            button.MouseLeave += Pill_MouseLeave;

            BubbleLayer.Children.Add(button);
            _bubbles.Add(button);
        }

        if (_expanded) ShowBubbles(true);
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
        double notchLeft = (WindowW - ExpandedW) / 2;

        // Tall, short and mini open notches each have their own layout
        _bubbleLayout = BubbleLayout.For(notchHeight);
        var visible = _bubbles.Select(b => (BubbleSetting)b.Tag).Where(b => b.IsShown(_bubbleLayout));
        var arranged = BubbleLayout.Arrange(visible, BubbleLayout.SideCapacity(notchHeight), _bubbleLayout);
        int bottomCount = arranged.Count(a => a.Side == BubbleSide.Bottom);

        foreach (var (bubble, side, index, _) in arranged)
        {
            var button = _bubbles.First(b => ReferenceEquals(b.Tag, bubble));
            var (x, y) = BubbleLayout.Position(side, index, bottomCount, notchLeft, ExpandedW, notchHeight);
            var (fromX, fromY) = BubbleLayout.PopFrom(side);
            _bubbleSlots[button] = (x, y, fromX, fromY);
        }
    }

    // ---------- Pop out / tuck away ----------

    private void ShowBubbles(bool show)
    {
        if (_bubbles.Count == 0) return;
        if (show) PlanBubbles(ExpandedTargetHeight);
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
            ShowTopMessage($"Couldn't open {bubble.Label} — check it in Settings → Bubbles", LowRed, 4);

        if (KeepOpen) CloseFromAlly(); // touch / controller: get out of the way
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
