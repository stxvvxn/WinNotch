using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinNotch;

// Hovering a button along the top drops a small label down underneath it saying what it does.
public partial class MainWindow
{
    private readonly HashSet<Button> _hintButtons = new();
    private readonly DispatcherTimer _hintDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _hintDelayReady;
    private Button? _hintFor;

    private void AttachTopHint(Button button)
    {
        if (!_hintButtons.Add(button)) return;
        ToolTipService.SetIsEnabled(button, false); // our own label instead of the Windows tooltip
        button.MouseEnter += (_, _) => ShowTopHintSoon(button);
        button.MouseLeave += (_, _) => HideTopHint(button);
        button.Click += (_, _) => HideTopHint(button);
    }

    private void ShowTopHintSoon(Button button)
    {
        if (!_hintDelayReady)
        {
            _hintDelayReady = true;
            _hintDelay.Tick += (_, _) =>
            {
                _hintDelay.Stop();
                if (_hintFor is { IsMouseOver: true } b) ShowTopHint(b);
            };
        }
        _hintFor = button;
        // Already showing one: switch straight away while you slide along the row
        if (TopHint.Opacity > 0.5) ShowTopHint(button);
        else { _hintDelay.Stop(); _hintDelay.Start(); }
    }

    private void ShowTopHint(Button button)
    {
        if (!_expanded || button.ToolTip is not string text || text.Length == 0) return;
        TopHintText.Text = text;

        // Under the button, kept inside the notch
        TopHint.Measure(new Size(280, double.PositiveInfinity));
        double w = TopHint.DesiredSize.Width;
        var pos = button.TranslatePoint(new Point(0, 0), PillRoot);
        double x = pos.X + button.ActualWidth / 2 - w / 2;
        x = Math.Clamp(x, 8, Math.Max(8, Pill.ActualWidth - w - 8));
        double y = pos.Y + button.ActualHeight + 1;
        TopHint.Margin = new Thickness(x, y, 0, 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TopHintMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        TopHint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }

    private void HideTopHint(Button button)
    {
        if (_hintFor == button) _hintFor = null;
        _hintDelay.Stop();
        // Leaving one button for the next: the new one will take over
        Dispatcher.BeginInvoke(() =>
        {
            if (_hintFor != null) return;
            TopHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            TopHintMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, TimeSpan.FromMilliseconds(120)));
        }, DispatcherPriority.Input);
    }
}
