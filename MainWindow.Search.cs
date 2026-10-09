using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinNotch;

// Command bar: a shortcut (Ctrl+Alt+Space by default) opens a search box in the notch.
// Finds apps from the Start menu, files and folders in your chosen folders, and your own scripts;
// also does sums, and can hand the text to Claude or a web search.
public partial class MainWindow
{
    private sealed record SearchItem(string Name, string Path, string Kind, string Glyph, Action? Run = null);

    private const int SearchHotkeyId = 0x5345;
    private bool _searchHooked, _searchHotkeyRegistered;
    private List<SearchItem> _searchIndex = new();
    private DateTime _searchIndexedAt = DateTime.MinValue;
    private bool _indexing;
    private List<SearchItem> _searchResults = new();
    private int _searchSelected;

    public static string ScriptsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "Scripts");

    // ---------- Opening it ----------

    private void ApplySearch()
    {
        if (_searchHotkeyRegistered) UnregisterHotKey(_hwnd, SearchHotkeyId);
        _searchHotkeyRegistered = false;
        if (_hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(_settings.SearchHotkey)) return;
        if (!KeySender.TryParseHotkey(_settings.SearchHotkey, out uint mods, out uint key)) return;

        if (!_searchHooked)
        {
            _searchHooked = true;
            HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && w.ToInt32() == SearchHotkeyId)
                {
                    handled = true;
                    Dispatcher.InvokeAsync(OpenSearch);
                }
                return IntPtr.Zero;
            });
        }
        _searchHotkeyRegistered = RegisterHotKey(_hwnd, SearchHotkeyId, mods | 0x4000, key);
    }

    private void SearchToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openTool == "search")
        {
            ToggleTool("search");
            EndTextEntry();
            return;
        }
        OpenSearch();
    }

    private void OpenSearch()
    {
        if (NotchHidden) return;
        // Pressed the shortcut while it's already open: close it
        if (_expanded && _openTool == "search" && SearchBox.IsKeyboardFocusWithin)
        {
            SetExpanded(false);
            return;
        }

        SetExpanded(true);
        _openTool = "search";
        RefreshSections();
        SearchBox.Text = "";
        ShowSearchResults();
        Dispatcher.InvokeAsync(() => BeginTextEntry(SearchBox), System.Windows.Threading.DispatcherPriority.Input);
        _ = EnsureSearchIndexAsync();
    }

    // ---------- Typing and choosing ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowSearchResults();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSearchSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSearchSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                if (_searchSelected >= 0 && _searchSelected < _searchResults.Count)
                    RunSearchItem(_searchResults[_searchSelected]);
                break;
            default:
                TextEntry_PreviewKeyDown(sender, e); // Esc closes
                break;
        }
    }

    private void MoveSearchSelection(int by)
    {
        if (_searchResults.Count == 0) return;
        _searchSelected = (_searchSelected + by + _searchResults.Count) % _searchResults.Count;
        HighlightSearchSelection();
    }

    private void ShowSearchResults()
    {
        string q = SearchBox.Text.Trim();
        var results = new List<SearchItem>();

        if (q.Length > 0)
        {
            // A sum or conversion goes first
            if (q.Any(char.IsDigit) && Calculator.Evaluate(q, _settings.CalcDecimals) is { Result.Length: > 0 } calc)
                results.Add(new SearchItem($"= {calc.Result}", "", "Copy", "", () =>
                {
                    if (TrySetClipboardText(calc.Copy)) ShowTopMessage($"Copied {calc.Copy}", ChargingGreen, 2);
                }));

            results.AddRange(_searchIndex
                .Select(item => (item, score: Score(item, q)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.item.Name.Length)
                .Take(7)
                .Select(x => x.item));

            results.Add(new SearchItem($"Ask Claude: {q}", "", "Claude", "", () =>
                OpenLink("claude://claude.ai/new?q=" + Uri.EscapeDataString(q), "https://claude.ai/new?q=" + Uri.EscapeDataString(q))));
            results.Add(new SearchItem($"Search the web for “{q}”", "", "Web", "", () =>
                OpenLink("https://www.google.com/search?q=" + Uri.EscapeDataString(q), null)));
        }

        _searchResults = results;
        _searchSelected = 0;
        SearchResults.Children.Clear();
        for (int i = 0; i < results.Count; i++) SearchResults.Children.Add(BuildSearchRow(results[i], i));
        SearchHint.Text = _indexing && _searchIndex.Count == 0
            ? "Getting your apps and files ready…"
            : "Apps, your folders and scripts · ↑↓ to choose · Enter to open · Esc to close";
        HighlightSearchSelection();
        AnimateExpandedHeight();
    }

    private FrameworkElement BuildSearchRow(SearchItem item, int index)
    {
        var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand, Tag = index };
        var dock = new DockPanel();
        var kind = new TextBlock { Text = item.Kind, Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(kind, Dock.Right);
        dock.Children.Add(kind);

        ImageSource? icon = SearchIcon(item.Path);
        FrameworkElement iconElement = icon != null
            ? new Image { Source = icon, Width = 18, Height = 18 }
            : new TextBlock { Text = item.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = Brushes.White, Width = 18, TextAlignment = TextAlignment.Center };
        iconElement.Margin = new Thickness(0, 0, 10, 0);
        iconElement.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(iconElement, Dock.Left);
        dock.Children.Add(iconElement);

        dock.Children.Add(new TextBlock
        {
            Text = item.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.Path.Length > 0 ? item.Path : null,
        });
        row.Child = dock;
        row.MouseEnter += (_, _) => { _searchSelected = index; HighlightSearchSelection(); };
        row.MouseLeftButtonUp += (_, _) => RunSearchItem(item);
        return row;
    }

    private readonly Dictionary<string, ImageSource?> _searchIcons = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? SearchIcon(string path)
    {
        if (path.Length == 0 || !(File.Exists(path) || Directory.Exists(path))) return null;
        if (!_searchIcons.TryGetValue(path, out var icon))
        {
            try { icon = GetIcon(path); } catch { icon = null; }
            if (_searchIcons.Count > 500) _searchIcons.Clear();
            _searchIcons[path] = icon;
        }
        return icon;
    }

    private void HighlightSearchSelection()
    {
        foreach (var row in SearchResults.Children.OfType<Border>())
            row.Background = Equals(row.Tag, _searchSelected) ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) : Brushes.Transparent;
    }

    private void RunSearchItem(SearchItem item)
    {
        _textEntry = false; // let the app we open take the keyboard
        SetExpanded(false);
        if (item.Run != null)
        {
            item.Run();
            return;
        }

        try
        {
            string ext = Path.GetExtension(item.Path).ToLowerInvariant();
            var start = ext == ".ps1"
                ? new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{item.Path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(item.Path) { UseShellExecute = true };
            if (item.Kind == "Script") start.WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "";
            Process.Start(start);
        }
        catch
        {
            Notify("", $"Couldn't open {item.Name}", "", LowRed, 3);
        }
    }

    private static void OpenLink(string link, string? fallback)
    {
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch
        {
            if (fallback != null)
                try { Process.Start(new ProcessStartInfo(fallback) { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>How well an item matches: starts with &gt; a word starts with &gt; contains &gt; letters in order.</summary>
    private static int Score(SearchItem item, string q)
    {
        string name = item.Name;
        int bonus = item.Kind switch { "App" => 12, "Script" => 10, "Folder" => 4, _ => 0 };
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100 + bonus;
        int at = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at > 0 && !char.IsLetterOrDigit(name[at - 1])) return 80 + bonus;
        if (at > 0) return 55 + bonus;
        if (q.Length < 3) return 0;
        // Letters in order, e.g. "vsc" → "Visual Studio Code"
        int i = 0;
        foreach (char c in name)
            if (i < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length ? 20 + bonus : 0;
    }

    // ---------- What it searches ----------

    private async Task EnsureSearchIndexAsync()
    {
        if (_indexing || DateTime.Now - _searchIndexedAt < TimeSpan.FromMinutes(5)) return;
        _indexing = true;
        var folders = _settings.SearchFolders.ToList();
        try
        {
            var index = await Task.Run(() => BuildSearchIndex(folders));
            _searchIndex = index;
            _searchIndexedAt = DateTime.Now;
        }
        catch { }
        finally
        {
            _indexing = false;
        }
        if (_openTool == "search") ShowSearchResults();
    }

    private static List<SearchItem> BuildSearchIndex(List<string> folders)
    {
        var items = new List<SearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Apps from the Start menu
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            foreach (var file in SafeFiles(Path.Combine(root, "Programs"), 4, 3000))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add("app:" + name)) items.Add(new SearchItem(name, file, "App", ""));
            }
        }

        // A few Windows apps that aren't in the Start menu folders
        foreach (var (name, target) in new[]
                 {
                     ("Settings", "ms-settings:"), ("Calculator", "calc.exe"), ("Task Manager", "taskmgr.exe"),
                     ("File Explorer", "explorer.exe"), ("Notepad", "notepad.exe"), ("Control Panel", "control.exe"),
                     ("Microsoft Store", "ms-windows-store:"), ("Snipping Tool", "ms-screenclip:"),
                 })
            if (seen.Add("app:" + name)) items.Add(new SearchItem(name, target, "App", ""));

        // Your scripts
        Directory.CreateDirectory(ScriptsFolder);
        foreach (var file in SafeFiles(ScriptsFolder, 2, 500))
            items.Add(new SearchItem(Path.GetFileNameWithoutExtension(file), file, "Script", ""));

        // Files and folders in the folders you chose
        foreach (var folder in folders)
        {
            string path = Environment.ExpandEnvironmentVariables(folder);
            if (!Directory.Exists(path)) continue;
            int count = 0;
            foreach (var entry in SafeEntries(path, 3))
            {
                bool isDir = Directory.Exists(entry);
                items.Add(new SearchItem(Path.GetFileName(entry), entry, isDir ? "Folder" : "File", isDir ? "" : ""));
                if (++count > 5000) break;
            }
        }
        return items;
    }

    private static IEnumerable<string> SafeFiles(string root, int depth, int max) =>
        SafeEntries(root, depth).Where(File.Exists).Take(max);

    // Walks a folder a few levels deep, skipping hidden and system folders and places you can't read
    private static IEnumerable<string> SafeEntries(string root, int depth)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (dir, d) = pending.Dequeue();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }
            foreach (var e in entries)
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(e); }
                catch { continue; }
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                string name = Path.GetFileName(e);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
                yield return e;
                if ((attrs & FileAttributes.Directory) != 0 && d + 1 < depth) pending.Enqueue((e, d + 1));
            }
        }
    }
}
