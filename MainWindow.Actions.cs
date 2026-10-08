using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// ---------- Settings file model (actions.json) ----------

public sealed class ActionsConfig
{
    public List<AppActions> Apps { get; set; } = new();
}

public sealed class AppActions
{
    public string Name { get; set; } = "";
    public List<string> Processes { get; set; } = new();
    public List<string> WindowClasses { get; set; } = new(); // optional: only these kinds of window
    public List<QuickAction> Actions { get; set; } = new();
}

public sealed class QuickAction
{
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string? Keys { get; set; }
    public string? Run { get; set; }

    [JsonIgnore]
    public string Glyph
    {
        get
        {
            if (Icon.Length is > 0 and <= 2) return Icon; // a literal character/emoji
            return int.TryParse(Icon, System.Globalization.NumberStyles.HexNumber, null, out int code)
                ? char.ConvertFromUtf32(code)
                : ""; // lightning bolt as a fallback
        }
    }

    [JsonIgnore]
    public string Tooltip => !string.IsNullOrWhiteSpace(Keys) ? $"{Label} ({Keys})" : Label;
}

// ---------- Quick actions: buttons that change with the app you're using ----------

public partial class MainWindow
{
    private static readonly string ActionsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "actions.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private ActionsConfig _actionsConfig = new();
    private string? _actionsError;
    private FileSystemWatcher? _actionsWatcher;
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _reloadDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private IntPtr _targetWindow;          // the app window the buttons will act on
    private uint _lastPid;
    private string _lastProcessName = "";
    private string _lastWindowClass = "";
    private AppActions? _currentApp;

    private void InitActions()
    {
        LoadActions();
        WatchActionsFile();

        _foregroundTimer.Tick += (_, _) => CheckForegroundApp();
        _foregroundTimer.Start();
    }

    // ---------- Loading actions.json ----------

    private void LoadActions()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ActionsFile)!);
            if (!File.Exists(ActionsFile)) File.WriteAllText(ActionsFile, DefaultActions.Json);

            string text = File.ReadAllText(ActionsFile);
            text = AddClaudeActionsOnce(text);
            text = AddAllyActionsOnce(text);
            text = AddExplorerActionsOnce(text);
            text = AddWorkAppActionsOnce(text);
            _actionsConfig = JsonSerializer.Deserialize<ActionsConfig>(text, JsonOptions) ?? new ActionsConfig();
            _actionsError = null;
        }
        catch (JsonException ex)
        {
            // Keep the last good settings and show what's wrong
            _actionsError = $"actions.json has a mistake on line {(ex.LineNumber ?? 0) + 1}";
        }
        catch (IOException)
        {
            // File is mid-save; the watcher will fire again
        }

        _lastProcessName = ""; // force the current app to be matched again
        CheckForegroundApp();
        UpdateActionsUi();
    }

    // Adds the Claude buttons to an existing actions.json (just once, so deleting them sticks)
    private string AddClaudeActionsOnce(string text)
    {
        if (_settings.ClaudeActionsAdded) return text;
        _settings.ClaudeActionsAdded = true;
        _settings.Save();
        return text.Contains("\"Claude\"", StringComparison.OrdinalIgnoreCase)
            ? text
            : InsertAppBlock(text, DefaultActions.ClaudeBlock);
    }

    // Adds File Explorer buttons to an existing actions.json (just once, so deleting them sticks)
    private string AddExplorerActionsOnce(string text)
    {
        if (_settings.ExplorerActionsAdded) return text;
        _settings.ExplorerActionsAdded = true;
        _settings.Save();
        return text.Contains("\"File Explorer\"", StringComparison.OrdinalIgnoreCase)
            ? text
            : InsertAppBlock(text, DefaultActions.ExplorerBlock);
    }

    // Adds Word, Outlook and Discord buttons to an existing actions.json (just once)
    private string AddWorkAppActionsOnce(string text)
    {
        if (_settings.WorkAppActionsAdded) return text;
        _settings.WorkAppActionsAdded = true;
        _settings.Save();
        return text.Contains("\"Discord\"", StringComparison.OrdinalIgnoreCase)
            ? text
            : InsertAppBlock(text, DefaultActions.WorkAppsBlock);
    }

    // Adds Steam and Xbox app buttons the first time ROG Ally mode is switched on
    private string AddAllyActionsOnce(string text)
    {
        if (!_settings.RogAlly || _settings.AllyActionsAdded) return text;
        _settings.AllyActionsAdded = true;
        _settings.Save();
        return text.Contains("\"Steam\"", StringComparison.OrdinalIgnoreCase)
            ? text
            : InsertAppBlock(text, DefaultActions.AllyBlock);
    }

    // Inserts app entries just before the "]" that closes the apps list, and saves if still valid
    private string InsertAppBlock(string text, string block)
    {
        int end = text.LastIndexOf(']');
        if (end < 0) return text;
        int prevBrace = text.LastIndexOf('}', end);
        if (prevBrace < 0) return text;

        string updated = text[..(prevBrace + 1)] + ",\n" + block.TrimEnd() + "\n  " + text[end..];
        try
        {
            JsonSerializer.Deserialize<ActionsConfig>(updated, JsonOptions);
            File.WriteAllText(ActionsFile, updated);
            return updated;
        }
        catch
        {
            return text;
        }
    }

    private void WatchActionsFile()
    {
        try
        {
            _actionsWatcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(ActionsFile)!, "actions.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            // Editors often save in several steps, so wait until they've finished
            _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); LoadActions(); };
            FileSystemEventHandler changed = (_, _) => Dispatcher.InvokeAsync(() =>
            {
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            });
            _actionsWatcher.Changed += changed;
            _actionsWatcher.Created += changed;
            _actionsWatcher.Renamed += (_, _) => changed(this, null!);
            _actionsWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private void EditActions_Click(object sender, RoutedEventArgs e) => OpenActionsFile();

    public void OpenActionsFile()
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ActionsFile}\"") { UseShellExecute = true }); }
        catch { }
    }

    // ---------- Which app is in front? ----------

    private void CheckForegroundApp()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return; // the notch itself or its menus: keep the previous app

        _targetWindow = hwnd;

        string name;
        if (pid == _lastPid && _lastProcessName != "")
        {
            name = _lastProcessName;
        }
        else
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = ""; }
        }

        // The kind of window, e.g. File Explorer folders are "CabinetWClass" while the desktop
        // and taskbar belong to the same explorer.exe but are different kinds
        var classBuffer = new System.Text.StringBuilder(64);
        GetClassName(hwnd, classBuffer, classBuffer.Capacity);
        string windowClass = classBuffer.ToString();

        if (pid == _lastPid && name == _lastProcessName && windowClass == _lastWindowClass && _currentApp != null) return;
        _lastPid = pid;
        _lastProcessName = name;
        _lastWindowClass = windowClass;

        var app = _actionsConfig.Apps.FirstOrDefault(a =>
            a.Processes.Any(p => string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
            && (a.WindowClasses.Count == 0
                || a.WindowClasses.Any(c => string.Equals(c, windowClass, StringComparison.OrdinalIgnoreCase))));

        if (!ReferenceEquals(app, _currentApp))
        {
            _currentApp = app;
            UpdateActionsUi();
        }
    }

    private void ClaudeToolButton_Click(object sender, RoutedEventArgs e) => ToggleTool("claude");

    // The Claude panel always uses the "Claude" entry from actions.json, whichever app is in front
    private void UpdateClaudePanel()
    {
        var claude = _actionsConfig.Apps.FirstOrDefault(a =>
            a.Name.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            || a.Processes.Any(p => p.Equals("Claude", StringComparison.OrdinalIgnoreCase)));

        var actions = claude?.Actions.Take(6).ToList() ?? new List<QuickAction>();
        ClaudeItems.ItemsSource = actions;
        ClaudeEmpty.Visibility = Vis(actions.Count == 0);
    }

    private void UpdateActionsUi()
    {
        UpdateClaudePanel();
        if (_actionsError != null)
        {
            ActionsAppName.Text = "⚠ " + _actionsError;
            ActionsAppName.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            ActionItems.ItemsSource = null;
            _actionsWanted = true;
        }
        else if (_currentApp is { Actions.Count: > 0 } app)
        {
            ActionsAppName.Text = app.Name.ToUpperInvariant();
            ActionsAppName.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ActionItems.ItemsSource = app.Actions.Take(6).ToList();
            _actionsWanted = true;
        }
        else
        {
            ActionItems.ItemsSource = null;
            _actionsWanted = false;
        }

        RefreshSections();
    }

    // ---------- Running an action ----------

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickAction action) return;

        if (!string.IsNullOrWhiteSpace(action.Run))
        {
            string target = Environment.ExpandEnvironmentVariables(action.Run);
            if (target.Contains("{clipboard}"))
            {
                string copied = "";
                try { if (Clipboard.ContainsText()) copied = Clipboard.GetText(); } catch { }
                if (copied.Length > 10000) copied = copied[..10000]; // links have a length limit
                target = target.Replace("{clipboard}", Uri.EscapeDataString(copied));
            }

            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { }
            return;
        }

        if (string.IsNullOrWhiteSpace(action.Keys)) return;

        // Make sure the keys go to the app, not the notch (e.g. right after using a right-click menu)
        if (_targetWindow != IntPtr.Zero && GetForegroundWindow() != _targetWindow)
        {
            SetForegroundWindow(_targetWindow);
            await Task.Delay(80);
        }

        KeySender.Send(action.Keys);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

// ---------- Pressing keyboard shortcuts ----------

internal static class KeySender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["prtscn"] = 0x2C,
        ["plus"] = 0xBB, ["="] = 0xBB, ["minus"] = 0xBD, ["-"] = 0xBD,
        [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE,
        ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["`"] = 0xC0,
    };

    // Keys that need the "extended" flag to be read correctly by some apps
    private static readonly HashSet<ushort> Extended = new()
    {
        0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x5B, 0x2C
    };

    private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B;

    /// <summary>Presses a shortcut like "Ctrl+Shift+S" or "Alt+Left".</summary>
    public static void Send(string shortcut)
    {
        var keys = new List<ushort>();
        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryGetVk(raw, out ushort vk)) keys.Add(vk);
            else return; // unknown key name: do nothing rather than press the wrong thing
        }
        if (keys.Count == 0) return;

        // Modifiers first, then the main key(s); release in reverse order
        var ordered = keys.Where(IsModifier).Concat(keys.Where(k => !IsModifier(k))).ToList();
        var inputs = new List<INPUT>();
        foreach (var vk in ordered) inputs.Add(Key(vk, up: false));
        for (int i = ordered.Count - 1; i >= 0; i--) inputs.Add(Key(ordered[i], up: true));

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Turns "Ctrl+Alt+N" into RegisterHotKey modifiers (Alt=1, Ctrl=2, Shift=4, Win=8) and a key.</summary>
    public static bool TryParseHotkey(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var raw in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetVk(raw, out ushort vk)) return false;
            switch (vk)
            {
                case 0x12: modifiers |= 1; break;
                case 0x11: modifiers |= 2; break;
                case 0x10: modifiers |= 4; break;
                case 0x5B: modifiers |= 8; break;
                default: key = vk; break;
            }
        }
        return key != 0;
    }

    private static bool TryGetVk(string name, out ushort vk)
    {
        if (Named.TryGetValue(name, out vk)) return true;

        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            vk = char.ToUpperInvariant(name[0]); // A-Z and 0-9 match their virtual key codes
            return true;
        }

        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + f - 1);
            return true;
        }

        vk = 0;
        return false;
    }

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0),
                    dwFlags = flags
                }
            }
        };
    }
}
