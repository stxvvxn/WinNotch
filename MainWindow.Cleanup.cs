using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinNotch;

// Clean-up button: click once to ask, click again to clear junk files
public partial class MainWindow
{
    private enum CleanState { Idle, Confirm, Running, Done }

    private CleanState _cleanState = CleanState.Idle;
    private DispatcherTimer? _cleanResetTimer;

    private static readonly Brush CleanAmber = FrozenBrush(0xFF, 0x9F, 0x0A);
    private static readonly Brush CleanGreen = FrozenBrush(0x34, 0xC7, 0x59);
    private static readonly Brush CleanGrey = FrozenBrush(0xA0, 0xA0, 0xA0);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_cleanState)
        {
            case CleanState.Idle:
            case CleanState.Done:
                // First click: ask, so a stray click never deletes anything
                _cleanState = CleanState.Confirm;
                SetTopMessage("Click again to clean", CleanAmber);
                ResetCleanAfter(TimeSpan.FromSeconds(4));
                break;

            case CleanState.Confirm:
                _ = RunCleanupAsync();
                break;
        }
    }

    private async Task RunCleanupAsync()
    {
        _cleanState = CleanState.Running;
        _cleanResetTimer?.Stop();
        SetTopMessage("Cleaning...", CleanGrey);

        // Spin a refresh icon while working
        CleanIcon.Text = "";
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        CleanIcon.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, spin);

        var result = await Task.Run(() => JunkCleaner.Run());

        CleanIcon.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        CleanIcon.Text = "";

        _cleanState = CleanState.Done;
        SetTopMessage(result.Bytes > 0 ? $"Freed {FormatBytes(result.Bytes)}" : "Already clean", CleanGreen);
        CleanButton.ToolTip = $"Last clean-up: removed {result.Files:N0} files ({FormatBytes(result.Bytes)}). " +
                              "Files in use were skipped.";
        ResetCleanAfter(TimeSpan.FromSeconds(5));
    }

    private void SetTopMessage(string text, Brush colour)
    {
        TopMessage.Text = text;
        TopMessage.Foreground = colour;
    }

    private void ResetCleanAfter(TimeSpan delay)
    {
        _cleanResetTimer?.Stop();
        _cleanResetTimer = new DispatcherTimer { Interval = delay };
        _cleanResetTimer.Tick += (_, _) =>
        {
            _cleanResetTimer?.Stop();
            if (_cleanState == CleanState.Running) return;
            _cleanState = CleanState.Idle;
            TopMessage.Text = "";
        };
        _cleanResetTimer.Start();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}

/// <summary>
/// Deletes junk that Windows and apps recreate on their own: temp files, crash dumps,
/// error reports and browser caches (not history, passwords, cookies or downloads).
/// Files that are in use are skipped. Temp folders only lose files older than 24 hours,
/// so installers or apps that are running right now aren't affected.
/// </summary>
internal static class JunkCleaner
{
    public sealed record Result(long Bytes, int Files);

    private sealed class Tally
    {
        public long Bytes;
        public int Files;
    }

    public static Result Run()
    {
        var tally = new Tally();
        foreach (var (path, minAge) in Targets())
        {
            try
            {
                if (Directory.Exists(path)) CleanFolder(path, DateTime.UtcNow - minAge, tally);
            }
            catch { }
        }
        return new Result(tally.Bytes, tally.Files);
    }

    private static IEnumerable<(string Path, TimeSpan MinAge)> Targets()
    {
        var day = TimeSpan.FromHours(24);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Temp folders (only if they really are temp folders, as a safety check)
        string userTemp = System.IO.Path.GetTempPath();
        if (LooksLikeTemp(userTemp)) yield return (userTemp, day);
        yield return (System.IO.Path.Combine(windows, "Temp"), day); // partly needs admin; the rest is skipped

        // Crash dumps and Windows error reports
        yield return (System.IO.Path.Combine(local, "CrashDumps"), TimeSpan.Zero);
        yield return (System.IO.Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), TimeSpan.Zero);
        yield return (System.IO.Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), TimeSpan.Zero);
        yield return (System.IO.Path.Combine(local, @"Microsoft\Windows\INetCache"), TimeSpan.Zero);

        // Chromium browsers: each profile's cache folders only
        foreach (var browser in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data" })
        {
            string root = System.IO.Path.Combine(local, browser);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in SafeDirectories(root))
            {
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    string dir = System.IO.Path.Combine(profile, cache);
                    if (Directory.Exists(dir)) yield return (dir, TimeSpan.Zero);
                }
            }
        }

        // Firefox: each profile's cache
        string firefox = System.IO.Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var profile in SafeDirectories(firefox))
                yield return (System.IO.Path.Combine(profile, "cache2"), TimeSpan.Zero);
        }
    }

    private static bool LooksLikeTemp(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    // Empties a folder's contents (never the folder itself). Doesn't follow shortcuts/junctions,
    // so it can never wander outside the folder it was given.
    private static void CleanFolder(string dir, DateTime cutoffUtc, Tally tally)
    {
        string[] files;
        try { files = Directory.GetFiles(dir); } catch { files = Array.Empty<string>(); }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoffUtc) continue;
                long size = info.Length;
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                tally.Bytes += size;
                tally.Files++;
            }
            catch { /* in use or no permission: skip */ }
        }

        foreach (var sub in SafeDirectories(dir))
        {
            try
            {
                var info = new DirectoryInfo(sub);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // junction/symlink: leave alone
                CleanFolder(sub, cutoffUtc, tally);
                if (info.CreationTimeUtc <= cutoffUtc && !info.EnumerateFileSystemInfos().Any())
                    info.Delete();
            }
            catch { }
        }
    }
}
