using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNotch;

// The search box: apps and files on the PC (through the Windows Search index), a web search,
// or asking the AI tool chosen in Settings.
public partial class MainWindow
{
    private sealed record Result(string Title, string Subtitle, string Kind, string Glyph, string Path, Action Run);

    private const int SearchHotkeyId = 0x5632;
    private const int WM_HOTKEY = 0x0312;
    private bool _hotkeyHooked, _hotkeyRegistered;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private bool _searchDelayReady;
    private int _searchVersion;
    private List<Result> _results = new();
    private int _selected;
    private List<(string Name, string Path)> _apps = new();
    private DateTime _appsLoadedAt = DateTime.MinValue;

    // ---------- Shortcut ----------

    private void RegisterSearchHotkey()
    {
        StopSearchHotkey();
        if (_hwnd == IntPtr.Zero || !Hotkeys.TryParse(_settings.SearchHotkey, out uint mods, out uint key)) return;
        if (!_hotkeyHooked)
        {
            _hotkeyHooked = true;
            HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && w.ToInt32() == SearchHotkeyId)
                {
                    handled = true;
                    Dispatcher.InvokeAsync(OpenSearchFromHotkey);
                }
                return IntPtr.Zero;
            });
        }
        _hotkeyRegistered = RegisterHotKey(_hwnd, SearchHotkeyId, mods | 0x4000 /* no repeat */, key);
    }

    private void StopSearchHotkey()
    {
        if (_hotkeyRegistered) UnregisterHotKey(_hwnd, SearchHotkeyId);
        _hotkeyRegistered = false;
    }

    private void OpenSearchFromHotkey()
    {
        if (_expanded && _typing)
        {
            SetExpanded(false); // pressed again: close
            return;
        }
        SetExpanded(true);
        Dispatcher.InvokeAsync(BeginTyping, DispatcherPriority.Input);
    }

    // ---------- Typing ----------

    private void SearchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool reallyOurs = GetForegroundWindow() == _hwnd;
        DebugLog.Write($"search box clicked: focused={SearchBox.IsKeyboardFocusWithin} typing={_typing} active={IsActive} foreground={reallyOurs}");
        // Looking focused isn't enough: the keyboard only comes here if Windows has the notch in front
        if (!reallyOurs || !SearchBox.IsKeyboardFocusWithin) BeginTyping();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = Vis(SearchBox.Text.Length == 0);
        UpdateCountdownRow(DateTime.Now); // the countdown row and quote make way for results
        UpdateQuote();
        if (!_searchDelayReady)
        {
            _searchDelayReady = true;
            _searchDelay.Tick += (_, _) =>
            {
                _searchDelay.Stop();
                _ = RunSearchAsync(SearchBox.Text.Trim());
            };
        }
        _searchDelay.Stop();
        if (SearchBox.Text.Trim().Length == 0)
        {
            _searchVersion++; // a search still running from before mustn't bring its results back
            ShowResults(new List<Result>());
            // Cleared it with the mouse elsewhere: close
            if (_expanded && !_hovering && !_clearingByKey)
            {
                _hoverTimer.Stop();
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(600);
                _hoverTimer.Start();
            }
            return;
        }
        if (_page != 0) ShowPage(0); // typing brings back the search results
        // Web and AI rows straight away; files follow a moment later
        ShowResults(ExtraRows(SearchBox.Text.Trim()));
        _searchDelay.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (SearchBox.Text.Length > 0) ClearSearchKeepOpen(); // clear first, close on the next Esc
                else CloseFromKeyboard();
                break;
            case Key.Down:
            case Key.Up:
                e.Handled = true;
                if (_results.Count == 0) break;
                _selected = (_selected + (e.Key == Key.Down ? 1 : -1) + _results.Count) % _results.Count;
                Highlight();
                break;
            case Key.Enter when q.Length > 0:
                e.Handled = true;
                if (ctrl) Run(() => AskAi(q));
                else if (shift) Run(() => WebTools.SearchWeb(_settings, q));
                else if (_selected >= 0 && _selected < _results.Count) Run(_results[_selected].Run);
                break;
        }
    }

    private bool _clearingByKey;

    /// <summary>Esc with text in the box: empty it and hide the results, but leave the notch open.</summary>
    private void ClearSearchKeepOpen()
    {
        _clearingByKey = true;
        SearchBox.Text = "";
        _clearingByKey = false;
        _searchVersion++; // ignore any search still running
    }

    private void AskAi(string q)
    {
        if (!WebTools.AskAi(_settings, q))
        {
            // That tool can't take text in a link: put it on the clipboard to paste in
            try { Clipboard.SetText(q); } catch { }
        }
    }

    /// <summary>Runs a result and gets out of the way (letting the app that opens have the keyboard).</summary>
    private void Run(Action action)
    {
        _typing = false;
        _hovering = false;
        SetExpanded(false);
        try { action(); } catch { }
    }

    // ---------- Finding things ----------

    private async Task RunSearchAsync(string q)
    {
        int version = ++_searchVersion;
        if (q.Length == 0) return;

        await EnsureAppsAsync();
        var apps = _apps
            .Select(a => (a, score: Score(a.Name, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.a.Name.Length)
            .Take(2)
            .Select(x => new Result(x.a.Name, "App", "App", "", x.a.Path, () => WebTools.Open(x.a.Path)))
            .ToList();

        var settings = _settings;
        var files = await Task.Run(() => FindFiles(q, settings));
        // You've typed more (or cleared it, or the notch closed) since this search started
        if (version != _searchVersion || SearchBox.Text.Trim() != q || !_expanded) return;

        var results = new List<Result>();
        if (ReminderRow(q) is { } reminder) results.Add(reminder);
        else if (MathRow(q) is { } math) results.Add(math);
        results.AddRange(apps);
        results.AddRange(files.Where(f => !apps.Any(a => a.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))));
        results.AddRange(ExtraRows(q, withMath: false));
        ShowResults(results);
    }

    /// <summary>The answer to a sum or conversion as the top row (Enter copies it), or null if it isn't one.</summary>
    private Result? MathRow(string q)
    {
        if (!q.Any(char.IsDigit) || Calculator.Evaluate(q, 6) is not { Result.Length: > 0 } calc) return null;
        string detail = calc.Detail.Length > 0 ? calc.Detail : Calculator.Wordy(q);
        return new Result($"= {calc.Result}", $"{detail} · Enter copies the answer", "Answer", "\uE8EF", "",
            () => { try { Clipboard.SetText(calc.Copy); } catch { } });
    }

    private List<Result> ExtraRows(string q, bool withMath = true)
    {
        var ai = WebTools.Ai(_settings);
        var engine = WebTools.Engine(_settings);
        var rows = new List<Result>();
        if (withMath && ReminderRow(q) is { } reminder) rows.Add(reminder);
        else if (withMath && MathRow(q) is { } math) rows.Add(math);
        rows.AddRange(new List<Result>
        {
            new($"Ask {ai.Name}", q, "Ctrl+Enter", "", "", () => AskAi(q)),
            new($"Search {engine.Name}", q, "Shift+Enter", "", "", () => WebTools.SearchWeb(_settings, q)),
        });
        return rows;
    }

    private static List<Result> FindFiles(string q, AppSettings settings)
    {
        var found = new List<Result>();
        if (settings.UseWindowsSearch)
        {
            try { found.AddRange(SearchIndex(q)); }
            catch { /* Windows Search turned off or unavailable: the folder search below still works */ }
        }
        if (found.Count < 5)
        {
            foreach (var r in SearchFolders(q, settings.SearchFolders, 5 - found.Count))
                if (!found.Any(f => f.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase))) found.Add(r);
        }
        return found.Take(5).ToList();
    }

    // Windows' own file index - the same one the Start menu searches - so it covers the whole PC quickly
    private static IEnumerable<Result> SearchIndex(string q)
    {
        string term = q.Replace("'", "''").Replace("\"", "");
        using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        connection.Open();
        string sql =
            "SELECT TOP 30 System.ItemPathDisplay, System.ItemNameDisplay, System.ItemFolderPathDisplay FROM SystemIndex " +
            $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '\"{term}*\"') " +
            "ORDER BY System.DateModified DESC";
        using var command = new OleDbCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var list = new List<Result>();
        while (reader.Read())
        {
            string path = reader.GetValue(0) as string ?? "";
            if (path.Length == 0 || IsClutter(path)) continue;
            string name = reader.GetValue(1) as string ?? Path.GetFileName(path);
            string folder = reader.GetValue(2) as string ?? Path.GetDirectoryName(path) ?? "";
            list.Add(FileResult(path, name, folder));
        }
        return list;
    }

    // Fallback / extra: walk your chosen folders a few levels deep
    private static IEnumerable<Result> SearchFolders(string q, List<string> folders, int max)
    {
        int count = 0;
        foreach (var folder in folders)
        {
            string root = Environment.ExpandEnvironmentVariables(folder);
            if (!Directory.Exists(root)) continue;
            foreach (var entry in Walk(root, 3))
            {
                string name = Path.GetFileName(entry);
                if (name.Contains(q, StringComparison.OrdinalIgnoreCase) && !IsClutter(entry))
                {
                    yield return FileResult(entry, name, Path.GetDirectoryName(entry) ?? "");
                    if (++count >= max) yield break;
                }
            }
        }
    }

    // Build output, caches and system bits nobody is looking for
    private static readonly string[] Clutter =
    {
        "\\bin\\", "\\obj\\", "\\node_modules\\", "\\.git\\", "\\.vs\\", "\\__pycache__\\",
        "\\AppData\\", "\\$Recycle.Bin\\", "\\Windows\\WinSxS\\",
    };

    private static bool IsClutter(string path) =>
        Clutter.Any(c => (path + "\\").Contains(c, StringComparison.OrdinalIgnoreCase));

    private static Result FileResult(string path, string name, string folder)
    {
        bool isDir = Directory.Exists(path);
        return new Result(name, folder, isDir ? "Folder" : "File", isDir ? "" : "", path, () => WebTools.Open(path));
    }

    private static IEnumerable<string> Walk(string root, int depth)
    {
        var pending = new Queue<(string, int)>();
        pending.Enqueue((root, 0));
        int seen = 0;
        while (pending.Count > 0 && seen < 6000)
        {
            var (dir, d) = pending.Dequeue();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }
            foreach (var e in entries)
            {
                seen++;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(e); } catch { continue; }
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                string name = Path.GetFileName(e);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
                yield return e;
                if ((attrs & FileAttributes.Directory) != 0 && d + 1 < depth) pending.Enqueue((e, d + 1));
            }
        }
    }

    // Apps from the Start menu (refreshed every 10 minutes)
    private async Task EnsureAppsAsync()
    {
        if (DateTime.Now - _appsLoadedAt < TimeSpan.FromMinutes(10)) return;
        _appsLoadedAt = DateTime.Now;
        _apps = await Task.Run(() =>
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     })
            {
                foreach (var file in Walk(Path.Combine(root, "Programs"), 4))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(name)) list.Add((name, file));
                }
            }
            foreach (var (name, target) in new[]
                     {
                         ("Settings", "ms-settings:"), ("Calculator", "calc.exe"), ("Task Manager", "taskmgr.exe"),
                         ("File Explorer", "explorer.exe"), ("Notepad", "notepad.exe"), ("Control Panel", "control.exe"),
                     })
                if (seen.Add(name)) list.Add((name, target));
            return list;
        });
    }

    private static int Score(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        int at = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at > 0 && !char.IsLetterOrDigit(name[at - 1])) return 80;
        if (at > 0) return 50;
        if (q.Length < 3) return 0;
        int i = 0;
        foreach (char c in name)
            if (i < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length ? 20 : 0; // letters in order, e.g. "vsc" → Visual Studio Code
    }

    // ---------- Showing results ----------

    private void ShowResults(List<Result> results)
    {
        _results = results;
        _selected = 0;
        Results.Children.Clear();
        for (int i = 0; i < results.Count; i++) Results.Children.Add(BuildRow(results[i], i));
        ResultsPanel.Visibility = Vis(results.Count > 0);
        Highlight();
        AnimateHeight();
    }

    private FrameworkElement BuildRow(Result r, int index)
    {
        var row = new Border { Style = (Style)FindResource("ResultRow"), Tag = index };
        var dock = new DockPanel();

        var kind = new TextBlock
        {
            Text = r.Kind,
            Foreground = (Brush)FindResource("Secondary"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(kind, Dock.Right);
        dock.Children.Add(kind);

        FrameworkElement icon = ShellIcon(r.Path) is { } img
            ? new Image { Source = img, Width = 20, Height = 20 }
            : new TextBlock
            {
                Text = r.Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Brushes.White,
                Width = 20,
                TextAlignment = TextAlignment.Center,
            };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Title, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (r.Subtitle.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = r.Subtitle,
                Foreground = (Brush)FindResource("Secondary"),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        dock.Children.Add(text);
        row.Child = dock;

        row.MouseEnter += (_, _) => { _selected = index; Highlight(); };
        row.MouseLeftButtonUp += (_, _) => Run(r.Run);
        // Right-click a file: show it in its folder
        row.MouseRightButtonUp += (_, _) =>
        {
            if (r.Path.Length > 0 && (File.Exists(r.Path) || Directory.Exists(r.Path)))
                Run(() => Process.Start("explorer.exe", $"/select,\"{r.Path}\""));
        };
        if (r.Path.Length > 0) row.ToolTip = r.Path;
        return row;
    }

    private void Highlight()
    {
        var on = (Brush)FindResource("SurfaceHover");
        foreach (var row in Results.Children.OfType<Border>())
            row.Background = Equals(row.Tag, _selected) ? on : Brushes.Transparent;
    }

    // ---------- File icons ----------

    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? ShellIcon(string path)
    {
        if (path.Length == 0 || !(File.Exists(path) || Directory.Exists(path))) return null;
        if (_icons.TryGetValue(path, out var cached)) return cached;

        ImageSource? icon = null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* icon */) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
            }
            catch { }
            finally { DestroyIcon(info.hIcon); }
        }
        if (_icons.Count > 400) _icons.Clear();
        _icons[path] = icon;
        return icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
