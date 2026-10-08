using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNotch;

// Song progress bar: shows elapsed / remaining time and lets you click or drag to seek
public partial class MainWindow
{
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Last timeline reported by the media app. Apps only report occasionally,
    // so while playing we work out the current position from the time since then.
    private TimeSpan _tlStart, _tlEnd, _tlPosition;
    private DateTimeOffset _tlUpdated = DateTimeOffset.Now;
    private bool _tlPlaying, _canSeek, _scrubbing;
    private double _scrubFraction;

    private TimeSpan Duration => _tlEnd - _tlStart;
    private bool HasTimeline => Duration > TimeSpan.Zero;

    private void InitProgress()
    {
        _progressTimer.Tick += (_, _) => UpdateProgressUi();
        _progressTimer.Start();
    }

    private void RefreshTimeline(bool force)
    {
        var session = _session;
        if (session == null)
        {
            _tlStart = _tlEnd = _tlPosition = TimeSpan.Zero;
            _canSeek = false;
            UpdateProgressUi();
            return;
        }

        try
        {
            var tl = session.GetTimelineProperties();
            // Some apps report a blank "last updated" time; treat that as "just now"
            var updated = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : tl.LastUpdatedTime;

            // Ignore stale reports (e.g. an app that doesn't update its position when paused)
            if (force || updated >= _tlUpdated || tl.EndTime != _tlEnd)
            {
                _tlStart = tl.StartTime;
                _tlEnd = tl.EndTime;
                _tlPosition = tl.Position;
                _tlUpdated = updated;
            }

            _canSeek = session.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled == true;
        }
        catch
        {
            // Leave the last known timeline in place
        }

        ProgressTrack.Cursor = _canSeek && HasTimeline ? Cursors.Hand : Cursors.Arrow;
        UpdateProgressUi();
    }

    // Called when play/pause changes, so the bar freezes and resumes in the right place
    private void SetPlayingForTimeline(bool playing)
    {
        if (playing == _tlPlaying) return;
        _tlPosition = CurrentPosition();
        _tlUpdated = DateTimeOffset.Now;
        _tlPlaying = playing;
        UpdateProgressUi();
        UpdateSoundBars();
    }

    private TimeSpan CurrentPosition()
    {
        var pos = _tlPosition;
        if (_tlPlaying) pos += DateTimeOffset.Now - _tlUpdated;
        if (pos < _tlStart) pos = _tlStart;
        if (pos > _tlEnd) pos = _tlEnd;
        return pos;
    }

    private void UpdateProgressUi()
    {
        ProgressRow.Opacity = HasTimeline ? 1 : 0.35;
        if (_scrubbing) return; // don't fight the user while they drag

        if (!HasTimeline)
        {
            SetProgressVisual(0, TimeSpan.Zero, TimeSpan.Zero);
            return;
        }

        var elapsed = CurrentPosition() - _tlStart;
        SetProgressVisual(elapsed.TotalSeconds / Duration.TotalSeconds, elapsed, Duration);
    }

    private void SetProgressVisual(double fraction, TimeSpan elapsed, TimeSpan duration)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double width = ProgressTrack.ActualWidth * fraction;
        TrackFill.Width = width;
        TrackThumb.Margin = new Thickness(Math.Max(0, width - TrackThumb.Width / 2), 0, 0, 0);
        ElapsedText.Text = FormatTime(elapsed);
        RemainingText.Text = "-" + FormatTime(duration - elapsed);
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---------- Click / drag to seek ----------

    private void ProgressTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_canSeek || !HasTimeline) return;
        _scrubbing = true;
        ProgressTrack.CaptureMouse();
        ScrubTo(e);
        e.Handled = true;
    }

    private void ProgressTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing) ScrubTo(e);
    }

    private async void ProgressTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ProgressTrack.ReleaseMouseCapture();
        if (!ProgressTrack.IsMouseOver) TrackThumb.Opacity = 0;

        var session = _session;
        if (session == null) return;

        var target = _tlStart + TimeSpan.FromTicks((long)(Duration.Ticks * _scrubFraction));
        _tlPosition = target;              // show the new spot straight away
        _tlUpdated = DateTimeOffset.Now;
        UpdateProgressUi();

        try { await session.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
    }

    private void ProgressTrack_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false; // drag cancelled (e.g. window lost the mouse)
        UpdateProgressUi();
    }

    private void ScrubTo(MouseEventArgs e)
    {
        double w = ProgressTrack.ActualWidth;
        if (w <= 0) return;
        _scrubFraction = Math.Clamp(e.GetPosition(ProgressTrack).X / w, 0, 1);
        SetProgressVisual(_scrubFraction, TimeSpan.FromTicks((long)(Duration.Ticks * _scrubFraction)), Duration);
    }

    private void ProgressTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_canSeek && HasTimeline) TrackThumb.Opacity = 1;
    }

    private void ProgressTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) TrackThumb.Opacity = 0;
    }
}
