using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNotch;

// Quick notes and the calculator: the two tools you type into.
// The notch normally never takes focus, so while you type it borrows it and hands it back afterwards.
public partial class MainWindow
{
    private static readonly string NotesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "notes.txt");

    private readonly DispatcherTimer _notesSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _notesLoaded, _textEntry, _deactivateHooked;
    private int _calcVersion;
    private string _calcLastResult = "";

    private bool TextEntryActive => NotesBox.IsKeyboardFocusWithin || CalcBox.IsKeyboardFocusWithin;

    // ---------- Shared bits ----------

    private void ApplyExtras()
    {
        if (!_notesLoaded)
        {
            try { if (File.Exists(NotesFile)) NotesBox.Text = File.ReadAllText(NotesFile); } catch { }
            _notesLoaded = true; // after loading, so loading doesn't count as an edit
            NotesSaved.Text = "";
            _notesSaveTimer.Tick += (_, _) => SaveNotes();
        }
        if (!_deactivateHooked)
        {
            _deactivateHooked = true;
            // Clicked into another app while typing: close up (unless the mouse is still over the notch)
            Deactivated += (_, _) =>
            {
                _textEntry = false;
                if (_expanded && !_hovering && !_dragOver && !KeepOpen) SetExpanded(false);
            };
        }

        NotesBox.FontSize = _settings.NotesTextSize switch { "small" => 11, "large" => 16, _ => 13 };

        ApplyMic();
        ApplyStats();
    }

    private void BeginTextEntry(TextBox box)
    {
        if (!_textEntry)
        {
            _textEntry = true;
            SetForegroundWindow(_hwnd);
            Activate();
        }
        box.Focus();
        Keyboard.Focus(box);
    }

    /// <summary>Gives the keyboard back to the app you were using before.</summary>
    private void EndTextEntry()
    {
        if (!_textEntry) return;
        _textEntry = false;
        Keyboard.ClearFocus();
        if (_notesSaveTimer.IsEnabled) SaveNotes();
        if (GetForegroundWindow() == _hwnd && _targetWindow != IntPtr.Zero)
            SetForegroundWindow(_targetWindow);
    }

    private void TextEntry_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box && !box.IsKeyboardFocusWithin) BeginTextEntry(box);
    }

    private void TextEntry_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _hovering = false;
            SetExpanded(false);
            return;
        }

        if (sender == CalcBox && e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_settings.CalcCopyOnEnter && _calcLastResult.Length > 0 && TrySetClipboardText(_calcLastResult))
                ShowTopMessage($"Copied {_calcLastResult}", ChargingGreen, 2);
        }
    }

    // ---------- Quick notes ----------

    private void NotesToolButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTool("notes");
        if (_openTool == "notes")
            Dispatcher.InvokeAsync(() => { BeginTextEntry(NotesBox); NotesBox.CaretIndex = NotesBox.Text.Length; }, DispatcherPriority.Input);
        else
            EndTextEntry();
    }

    private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_notesLoaded) return;
        NotesSaved.Text = "…";
        _notesSaveTimer.Stop();
        _notesSaveTimer.Start();
    }

    private void SaveNotes()
    {
        _notesSaveTimer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotesFile)!);
            File.WriteAllText(NotesFile, NotesBox.Text);
            NotesSaved.Text = "Saved";
        }
        catch
        {
            NotesSaved.Text = "Couldn't save";
        }
    }

    // ---------- Calculator / converter ----------

    private void CalcToolButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTool("calc");
        if (_openTool == "calc")
            Dispatcher.InvokeAsync(() => { BeginTextEntry(CalcBox); CalcBox.SelectAll(); }, DispatcherPriority.Input);
        else
            EndTextEntry();
    }

    private async void CalcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int version = ++_calcVersion;
        string input = CalcBox.Text;

        if (string.IsNullOrWhiteSpace(input))
        {
            ShowCalc("", "");
            return;
        }

        var answer = Calculator.Evaluate(input, _settings.CalcDecimals);
        if (answer == null && _settings.CalcCurrency && Calculator.LooksLikeCurrency(input))
        {
            ShowCalc("", "Getting exchange rates…");
            answer = await Calculator.ConvertCurrencyAsync(input, _settings.CalcDecimals);
            if (version != _calcVersion) return; // you've typed more since
        }

        if (answer is { } a) ShowCalc(a.Result, a.Detail, a.Copy);
        else ShowCalc("", "Type a sum (12*4+3), a conversion (5 ft in cm) or a currency (20 usd to gbp)");
    }

    private void ShowCalc(string result, string detail, string copy = "")
    {
        _calcLastResult = copy;
        CalcResult.Text = result.Length > 0 ? "= " + result : "";
        CalcDetail.Text = detail;
    }
}
