using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// Updates: checks GitHub for a newer release shortly after starting and every 6 hours,
// then asks before installing it.
public partial class MainWindow
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private bool _updateTimerReady, _updateBusy;

    private void InitUpdates()
    {
        if (!_updateTimerReady)
        {
            _updateTimerReady = true;
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
            // First check a little after startup, so it doesn't slow down opening
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            first.Tick += async (_, _) =>
            {
                first.Stop();
                await CheckForUpdatesAsync(manual: false);
            };
            first.Start();
        }
        if (_settings.AutoUpdate) _updateTimer.Start(); else _updateTimer.Stop();
    }

    /// <summary>Manual = from Settings or the tray menu: always reports back, even when there's nothing new.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        if (!manual && !_settings.AutoUpdate) return;
        _updateBusy = true;
        try
        {
            UpdateInfo? update;
            try
            {
                update = await Updater.CheckAsync(_settings.UpdateRepo);
            }
            catch (Exception ex)
            {
                if (manual)
                    MessageBox.Show($"Couldn't check for updates.\n\n{ex.Message}\n\nThe GitHub repo needs to be public, with at least one release.",
                                    "WinNotch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (update == null)
            {
                if (manual)
                    MessageBox.Show($"You're up to date (version {Updater.CurrentVersion}).", "WinNotch",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Skipped this one before? Only the automatic check stays quiet about it
            if (!manual && update.Tag == _settings.SkippedVersion) return;

            switch (AskToUpdate(update))
            {
                case UpdateChoice.Install:
                    await InstallUpdateAsync(update);
                    break;
                case UpdateChoice.Skip:
                    _settings.SkippedVersion = update.Tag;
                    _settings.Save();
                    break;
            }
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (!Updater.CanSelfUpdate)
        {
            MessageBox.Show("This copy was started from the code (run.bat), so it can't replace itself.\n\n" +
                            "Pull the new code from GitHub instead, or use WinNotch.exe from the latest release.",
                            "WinNotch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Progress shows on the closed notch where the date normally is
            var progress = new Progress<double>(p =>
            {
                _headerFlash = $"Updating… {Math.Round(p * 100)}%";
                _headerFlashUntil = DateTime.Now.AddSeconds(30);
                _headerFlashGlyph = "\uE896"; // download arrow
                UpdateClock();
            });
            _headerFlash = "Updating…";
            _headerFlashUntil = DateTime.Now.AddSeconds(30);
            _headerFlashGlyph = "\uE896"; // download arrow
            UpdateClock();
            await Updater.InstallAsync(update, progress);
            Application.Current.Shutdown(); // the new version is already starting
        }
        catch (Exception ex)
        {
            _headerFlash = null;
            UpdateClock();
            MessageBox.Show($"The update didn't work, so nothing was changed.\n\n{ex.Message}", "WinNotch",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private enum UpdateChoice { Later, Install, Skip }

    // A small dark window: what's new, plus Update now / Later / Skip this version
    private UpdateChoice AskToUpdate(UpdateInfo update)
    {
        var choice = UpdateChoice.Later;
        var accent = (Brush)FindResource("Accent");
        var dialog = new Window
        {
            Title = "WinNotch update",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"WinNotch {update.Version} is available",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"You have {Updater.CurrentVersion}. Updating takes a few seconds and WinNotch restarts by itself.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        string notes = string.IsNullOrWhiteSpace(update.Notes) ? "No notes for this version." : update.Notes.Trim();
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            },
        });

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        Button MakeButton(string text, UpdateChoice result, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = primary ? Brushes.Black : Brushes.White,
                Background = primary ? accent : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(0),
                IsDefault = primary,
                IsCancel = result == UpdateChoice.Later,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => { choice = result; dialog.Close(); };
            return b;
        }
        var skip = MakeButton("Skip this version", UpdateChoice.Skip);
        skip.Margin = new Thickness(0);
        DockPanel.SetDock(skip, Dock.Left);
        buttons.Children.Add(skip);
        var install = MakeButton("Update now", UpdateChoice.Install, primary: true);
        var later = MakeButton("Later", UpdateChoice.Later);
        DockPanel.SetDock(install, Dock.Right);
        DockPanel.SetDock(later, Dock.Right);
        buttons.Children.Add(install);
        buttons.Children.Add(later);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.ShowDialog();
        return choice;
    }
}
