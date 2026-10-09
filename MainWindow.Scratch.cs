using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WinNotch;

// Scratch pad on the shelf page: quick notes, saved to %AppData%\WinNotch\scratch.txt as you type.
public partial class MainWindow
{
    public static string ScratchFile => Path.Combine(AppSettings.Folder, "scratch.txt");

    private readonly DispatcherTimer _scratchSave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _scratchLoaded, _scratchSaveReady;

    private void ApplyScratch()
    {
        ScratchArea.Visibility = Vis(_settings.ShelfScratch);
        if (_scratchLoaded) return;
        try { if (File.Exists(ScratchFile)) ScratchBox.Text = File.ReadAllText(ScratchFile); } catch { }
        _scratchLoaded = true; // after loading, so loading isn't treated as an edit
        ScratchPlaceholder.Visibility = Vis(ScratchBox.Text.Length == 0);
        ScratchSaved.Text = "";
    }

    private void ScratchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ScratchBox.IsKeyboardFocusWithin || GetForegroundWindow() != _hwnd) BeginTyping(ScratchBox);
    }

    private void ScratchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ScratchPlaceholder.Visibility = Vis(ScratchBox.Text.Length == 0);
        if (!_scratchLoaded) return;
        if (!_scratchSaveReady)
        {
            _scratchSaveReady = true;
            _scratchSave.Tick += (_, _) => SaveScratch();
        }
        ScratchSaved.Text = "…";
        _scratchSave.Stop();
        _scratchSave.Start();
        AnimateHeight();
    }

    private void SaveScratch()
    {
        _scratchSave.Stop();
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(ScratchFile, ScratchBox.Text);
            ScratchSaved.Text = "Saved";
        }
        catch
        {
            ScratchSaved.Text = "Couldn't save";
        }
    }

    /// <summary>True if the mouse is over the scratch pad and it has more text to scroll that way.</summary>
    private bool ScratchCanScroll(int delta)
    {
        if (!ScratchBox.IsMouseOver || ScratchBox.ExtentHeight <= ScratchBox.ViewportHeight + 1) return false;
        return delta > 0
            ? ScratchBox.VerticalOffset > 0
            : ScratchBox.VerticalOffset + ScratchBox.ViewportHeight < ScratchBox.ExtentHeight - 1;
    }
}
