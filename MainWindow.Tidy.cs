using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Tidy up page (the brush bubble): clear out temp folders and the Recycle Bin, and run your own scripts.
public partial class MainWindow
{
    private sealed class ScriptState
    {
        public string Status = "";
        public string Output = "";
        public bool Running;
        public bool? Ok;
        public CancellationTokenSource? Cancel;
    }

    private const string RecycleBinKey = "::recycle-bin";
    private readonly Dictionary<string, long> _cleanSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptState> _scriptStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _cleaning, _measuring;
    private string? _cleaningKey;          // the row being cleaned (or "all")
    private DateTime _confirmCleanUntil;   // "Clean all" asks once before going
    private string _cleanSummary = "";

    /// <summary>The page was opened: rebuild it and measure what can be freed.</summary>
    private void RefreshTidy()
    {
        BuildTidyRows();
        if (!_cleaning) _ = MeasureCleanAsync();
    }

    private IEnumerable<(string Key, string Name, string Detail)> CleanTargets()
    {
        foreach (var p in _settings.CleanPaths.Where(p => p.Enabled && p.Path.Trim().Length > 0))
            yield return (p.Path, FriendlyName(p.Path), Tidy.Expand(p.Path));
        if (_settings.CleanRecycleBin) yield return (RecycleBinKey, "Recycle Bin", "Everything in the Recycle Bin");
    }

    private static string FriendlyName(string path)
    {
        string p = path.Trim().TrimEnd('\\');
        if (p.Equals("%TEMP%", StringComparison.OrdinalIgnoreCase) || p.Equals("%TMP%", StringComparison.OrdinalIgnoreCase))
            return "Your temp files";
        if (p.Equals(@"C:\Windows\Temp", StringComparison.OrdinalIgnoreCase) || p.Equals(@"%WINDIR%\Temp", StringComparison.OrdinalIgnoreCase))
            return "Windows temp files";
        string name = System.IO.Path.GetFileName(Tidy.Expand(p));
        return name.Length > 0 ? name : p;
    }

    // ---------- Measuring ----------

    private async Task MeasureCleanAsync()
    {
        if (_measuring) return;
        _measuring = true;
        try
        {
            int hours = _settings.CleanOlderThanHours;
            foreach (var (key, _, _) in CleanTargets().ToList())
            {
                long size = await Task.Run(() => key == RecycleBinKey ? Tidy.RecycleBinSize() : Tidy.Measure(key, hours));
                _cleanSizes[key] = size;
                if (_page == 4) BuildTidyRows();
            }
        }
        finally
        {
            _measuring = false;
        }
        if (_page == 4) BuildTidyRows();
    }

    // ---------- Cleaning ----------

    private async Task CleanAsync(string? onlyKey)
    {
        if (_cleaning) return;
        _cleaning = true;
        _cleaningKey = onlyKey ?? "all";
        _cleanSummary = "Cleaning…";
        BuildTidyRows();

        long freed = 0;
        int skipped = 0;
        int hours = _settings.CleanOlderThanHours;
        foreach (var (key, _, _) in CleanTargets().Where(t => onlyKey == null || t.Key == onlyKey).ToList())
        {
            if (key == RecycleBinKey)
            {
                freed += await Task.Run(Tidy.EmptyRecycleBin);
            }
            else
            {
                var result = await Task.Run(() => Tidy.Clean(key, hours));
                freed += result.BytesFreed;
                skipped += result.FilesSkipped;
            }
            _cleanSizes.Remove(key);
        }

        _cleaning = false;
        _cleaningKey = null;
        _cleanSummary = $"Freed {Tidy.Size(freed)}" + (skipped > 0 ? $" · {skipped} file{(skipped == 1 ? "" : "s")} in use or too new were left" : "");
        DebugLog.Write($"tidy up: {_cleanSummary}");

        // Let you know on the closed notch too
        _headerFlash = $"Freed {Tidy.Size(freed)}";
        _headerFlashGlyph = "\uEA99";
        _headerFlashUntil = DateTime.Now.AddSeconds(5);
        UpdateClock();

        BuildTidyRows();
        _ = MeasureCleanAsync();
    }

    private void CleanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaning) return;
        // First click asks, second click cleans
        if (DateTime.Now > _confirmCleanUntil)
        {
            _confirmCleanUntil = DateTime.Now.AddSeconds(4);
            BuildTidyRows();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.1) };
            timer.Tick += (_, _) => { timer.Stop(); if (_page == 4) BuildTidyRows(); };
            timer.Start();
            return;
        }
        _confirmCleanUntil = DateTime.MinValue;
        _ = CleanAsync(null);
    }

    // ---------- Scripts ----------

    private ScriptState StateFor(TidyScript s)
    {
        if (!_scriptStates.TryGetValue(s.Path, out var state)) _scriptStates[s.Path] = state = new ScriptState();
        return state;
    }

    private async Task RunScriptAsync(TidyScript script)
    {
        var state = StateFor(script);
        if (state.Running)
        {
            state.Cancel?.Cancel(); // the button says Stop while it's running
            return;
        }
        state.Running = true;
        state.Ok = null;
        state.Status = "Running…";
        state.Cancel = new CancellationTokenSource();
        BuildTidyRows();

        var result = await Tidy.RunAsync(script, state.Cancel.Token);
        state.Running = false;
        state.Ok = result.Ok;
        state.Output = result.Output;
        string name = script.Name.Length > 0 ? script.Name : System.IO.Path.GetFileNameWithoutExtension(script.Path);
        state.Status = result.Ok ? $"Done at {DateTime.Now:HH:mm}"
            : result.ExitCode == -1 ? $"{(result.Output.Length > 0 ? result.Output : "Didn't run")}"
            : $"Finished with an error (code {result.ExitCode}) at {DateTime.Now:HH:mm}";
        DebugLog.Write($"script '{name}': {state.Status}");

        _headerFlash = result.Ok ? $"{name}: done" : $"{name}: failed";
        _headerFlashGlyph = result.Ok ? "\uE73E" : "\uEA39";
        _headerFlashUntil = DateTime.Now.AddSeconds(5);
        UpdateClock();
        BuildTidyRows();
    }

    // ---------- Drawing the page ----------

    private void BuildTidyRows()
    {
        var secondary = (Brush)FindResource("Secondary");
        var accent = (Brush)FindResource("Accent");

        // Clean-up rows
        CleanRows.Children.Clear();
        long total = 0;
        bool anyUnknown = false;
        foreach (var (key, name, detail) in CleanTargets())
        {
            bool known = _cleanSizes.TryGetValue(key, out long size);
            if (known) total += size; else anyUnknown = true;
            bool busy = _cleaning && (_cleaningKey == "all" || _cleaningKey == key);
            string sizeText = busy ? "Cleaning…" : known ? (size > 0 ? Tidy.Size(size) : "Clean") : "Checking…";
            (string, Action)? clean = null;
            if (!_cleaning)
            {
                string k = key;
                clean = ("Clean", new Action(() => _ = CleanAsync(k)));
            }
            var row = TidyRow(key == RecycleBinKey ? "\uE74D" : "\uE8B7", name, detail, sizeText,
                clean, known && size > 0 ? Brushes.White : secondary);
            CleanRows.Children.Add(row);
        }
        CleanEmpty.Visibility = Vis(CleanRows.Children.Count == 0);

        bool confirming = DateTime.Now <= _confirmCleanUntil;
        CleanAllButton.Content = _cleaning ? "Cleaning…" : confirming ? $"Sure? Clean {Tidy.Size(total)}" : "Clean all";
        CleanAllButton.IsEnabled = !_cleaning && CleanRows.Children.Count > 0;
        CleanAllButton.Visibility = Vis(CleanRows.Children.Count > 0);
        CleanSummary.Text = _cleanSummary.Length > 0 && !_cleaning ? _cleanSummary
            : _cleaning ? "Cleaning…"
            : anyUnknown ? "Working out how much can be freed…"
            : total > 0 ? $"{Tidy.Size(total)} can be freed" : "All tidy";
        if (_settings.CleanOlderThanHours > 0 && !_cleaning && _cleanSummary.Length == 0)
            CleanSummary.ToolTip = $"Files changed in the last {(_settings.CleanOlderThanHours >= 24 ? $"{_settings.CleanOlderThanHours / 24} day(s)" : $"{_settings.CleanOlderThanHours} hour(s)")} are left alone (Settings → Tidy up)";

        // Script rows
        ScriptRows.Children.Clear();
        foreach (var script in _settings.TidyScripts.Where(s => s.Path.Trim().Length > 0))
        {
            var state = StateFor(script);
            string name = script.Name.Length > 0 ? script.Name : System.IO.Path.GetFileNameWithoutExtension(Tidy.Expand(script.Path));
            string detail = state.Status.Length > 0 ? state.Status : Tidy.Expand(script.Path);
            var s = script;
            (string, Action)? run = (state.Running ? "Stop" : "Run", new Action(() => _ = RunScriptAsync(s)));
            var row = TidyRow("\uE756", name, detail, "", run, Brushes.White, accentButton: !state.Running);
            if (state.Ok == false && row.Child is DockPanel dp && dp.Children.OfType<StackPanel>().FirstOrDefault()?.Children[1] is TextBlock sub)
                sub.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            if (state.Output.Length > 0)
            {
                var lines = state.Output.Split('\n').Select(l => l.TrimEnd('\r'));
                row.ToolTip = string.Join("\n", lines.TakeLast(15));
            }
            ScriptRows.Children.Add(row);
        }
        ScriptArea.Visibility = Vis(true);
        ScriptEmpty.Visibility = Vis(ScriptRows.Children.Count == 0);

        AnimateHeight();
    }

    private Border TidyRow(string glyph, string title, string detail, string right, (string Text, Action Click)? button,
                           Brush rightColour, bool accentButton = false)
    {
        var row = new Border { Padding = new Thickness(8, 5, 6, 5), CornerRadius = new CornerRadius(9) };
        var dock = new DockPanel();

        if (button is { } b)
        {
            var btn = new Button
            {
                Content = b.Text,
                Style = (Style)FindResource("AlertButton"),
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (accentButton)
            {
                btn.SetResourceReference(BackgroundProperty, "Accent");
                btn.Foreground = Brushes.Black;
            }
            btn.Click += (_, _) => b.Click();
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);
        }
        if (right.Length > 0)
        {
            var size = new TextBlock
            {
                Text = right,
                Foreground = rightColour,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            DockPanel.SetDock(size, Dock.Right);
            dock.Children.Add(size);
        }

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = (Brush)FindResource("Secondary"),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        dock.Children.Add(text);
        row.Child = dock;
        return row;
    }

    private void TidyRescan_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _cleanSummary = "";
        _cleanSizes.Clear();
        RefreshTidy();
    }

    /// <summary>Settings changed what's on the page.</summary>
    private void ApplyTidy()
    {
        _cleanSizes.Clear();
        if (_page == 4) RefreshTidy();
    }
}
