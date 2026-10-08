using System.Windows;
using Forms = System.Windows.Forms;

namespace WinNotch;

// System tray icon: right-click for Settings / Hide notch / Close, left-click to hide or show the notch
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayHideItem;

    private bool _userHidden;    // hidden from the tray or right-click menu
    private bool _hiddenForSnip; // briefly hidden while taking a screenshot

    private bool NotchHidden => (_hiddenForFullscreen && !_overlayActive) || _userHidden || _hiddenForSnip;

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
            Visible = true
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleNotchHidden();
        };
    }

    private void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false; // otherwise a "ghost" icon lingers until you hover over it
        _tray.Dispose();
        _tray = null;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            // notch.ico is built into the exe; pick the small (16px-ish) size the tray uses
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("WinNotch.notch.ico");
            if (stream != null) return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }

        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    private void HideNotch_Click(object sender, RoutedEventArgs e) => ToggleNotchHidden();

    private void ToggleNotchHidden()
    {
        _userHidden = !_userHidden;
        ApplyNotchVisibility();
    }

    // The notch is hidden if you've hidden it, something is fullscreen, or a screenshot is being taken
    private void ApplyNotchVisibility()
    {
        if (NotchHidden)
        {
            SetExpanded(false);
            EndHud(immediate: true);
            Pill.Visibility = Visibility.Hidden;
        }
        else if (Pill.Visibility != Visibility.Visible)
        {
            Pill.Visibility = Visibility.Visible;
            PositionWindow(); // also puts the notch back on top
        }

        if (_trayHideItem != null) _trayHideItem.Text = _userHidden ? "Show notch" : "Hide notch";
        if (_tray != null) _tray.Text = _userHidden ? "WinNotch (hidden) - click to show" : "WinNotch";
    }
}
