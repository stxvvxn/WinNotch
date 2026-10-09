using Forms = System.Windows.Forms;

namespace WinNotch;

// Tray icon: right-click for Settings / Hide notch / Close; left-click hides or shows the notch
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayHideItem;

    private void InitTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        _trayHideItem = new Forms.ToolStripMenuItem("Hide notch", null, (_, _) => ToggleNotchHidden());
        menu.Items.Add(_trayHideItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Close WinNotch", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "WinNotch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleNotchHidden();
        };
    }

    private void ToggleNotchHidden()
    {
        _userHidden = !_userHidden;
        ApplyNotchVisibility();
    }

    private void UpdateTrayText()
    {
        if (_trayHideItem != null) _trayHideItem.Text = _userHidden ? "Show notch" : "Hide notch";
        if (_tray != null) _tray.Text = _userHidden ? "WinNotch (hidden) - click to show" : "WinNotch";
    }

    private void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("WinNotch.notch.ico");
            if (stream != null) return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
