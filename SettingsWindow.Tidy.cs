using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinNotch;

// Settings → Tidy up: the folders to clean and your scripts
public partial class SettingsWindow
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    // ---------- Folders ----------

    private void BuildCleanPathList()
    {
        CleanPathList.Children.Clear();
        var s = _owner.Settings;
        if (s.CleanPaths.Count == 0)
            CleanPathList.Children.Add(new TextBlock { Text = "No folders yet - add one below.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 4) });

        foreach (var path in s.CleanPaths)
        {
            var p = path;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var remove = RemoveButton("Stop cleaning this folder");
            remove.Click += (_, _) =>
            {
                s.CleanPaths.Remove(p);
                _owner.SettingsChanged();
                BuildCleanPathList();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            string expanded = Tidy.Expand(p.Path);
            var box = new CheckBox
            {
                IsChecked = p.Enabled,
                Content = expanded.Equals(p.Path, StringComparison.OrdinalIgnoreCase) ? p.Path : $"{p.Path}   ({expanded})",
                ToolTip = Directory.Exists(expanded) ? expanded : $"{expanded} - this folder doesn't exist at the moment",
                Opacity = Directory.Exists(expanded) ? 1 : 0.6,
            };
            box.Click += (_, _) =>
            {
                p.Enabled = box.IsChecked == true;
                _owner.SettingsChanged();
            };
            row.Children.Add(box);
            CleanPathList.Children.Add(row);
        }
    }

    private void AddCleanPath_Click(object sender, RoutedEventArgs e)
    {
        string path = NewCleanPathBox.Text.Trim().Trim('"');
        if (path.Length == 0) return;
        var s = _owner.Settings;

        if (Tidy.WhyNotAllowed(path) is { } why)
        {
            CleanPathStatus.Text = why;
            CleanPathStatus.Foreground = ErrorBrush;
            return;
        }
        if (s.CleanPaths.Any(p => Tidy.Expand(p.Path).TrimEnd('\\').Equals(Tidy.Expand(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            CleanPathStatus.Text = "That folder is already on the list.";
            CleanPathStatus.Foreground = ErrorBrush;
            return;
        }

        s.CleanPaths.Add(new CleanPath { Path = path });
        _owner.SettingsChanged();
        NewCleanPathBox.Text = "";
        CleanPathStatus.Text = Directory.Exists(Tidy.Expand(path))
            ? $"Added {path}."
            : $"Added {path} - it doesn't exist right now, so it'll be skipped until it does.";
        CleanPathStatus.Foreground = (Brush)FindResource("Accent");
        BuildCleanPathList();
    }

    private void BrowseCleanPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder to clean out" };
        if (dialog.ShowDialog(this) == true)
        {
            NewCleanPathBox.Text = dialog.FolderName;
            AddCleanPath_Click(sender, e);
        }
    }

    private void NewCleanPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCleanPath_Click(sender, e);
    }

    // ---------- Scripts ----------

    private void BuildScriptList()
    {
        ScriptList.Children.Clear();
        var s = _owner.Settings;
        if (s.TidyScripts.Count == 0)
            ScriptList.Children.Add(new TextBlock { Text = "No scripts yet - add one below.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 4) });

        foreach (var script in s.TidyScripts)
        {
            var sc = script;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var remove = RemoveButton("Remove this script (the file itself isn't touched)");
            remove.Click += (_, _) =>
            {
                s.TidyScripts.Remove(sc);
                _owner.SettingsChanged();
                BuildScriptList();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var admin = new CheckBox { Content = "As admin", IsChecked = sc.RunAsAdmin, Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            admin.Click += (_, _) => { sc.RunAsAdmin = admin.IsChecked == true; _owner.SettingsChanged(); };
            DockPanel.SetDock(admin, Dock.Right);
            row.Children.Add(admin);

            var window = new CheckBox { Content = "Show window", IsChecked = sc.ShowWindow, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            window.Click += (_, _) => { sc.ShowWindow = window.IsChecked == true; _owner.SettingsChanged(); };
            DockPanel.SetDock(window, Dock.Right);
            row.Children.Add(window);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = sc.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = sc.Path,
                Style = (Style)FindResource("Hint"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = File.Exists(Tidy.Expand(sc.Path)) ? new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)) : ErrorBrush,
                ToolTip = File.Exists(Tidy.Expand(sc.Path)) ? sc.Path : $"{sc.Path} - this file can't be found",
            });
            row.Children.Add(text);
            ScriptList.Children.Add(row);
        }
    }

    private void AddScript_Click(object sender, RoutedEventArgs e)
    {
        string path = NewScriptPathBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            ScriptStatus.Text = "Pick the file to run first (Browse…).";
            ScriptStatus.Foreground = ErrorBrush;
            return;
        }
        if (!File.Exists(Tidy.Expand(path)))
        {
            ScriptStatus.Text = "Can't find that file - check the path, or use Browse….";
            ScriptStatus.Foreground = ErrorBrush;
            return;
        }
        string name = NewScriptNameBox.Text.Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);

        _owner.Settings.TidyScripts.Add(new TidyScript { Name = name, Path = path });
        _owner.SettingsChanged();
        NewScriptNameBox.Text = NewScriptPathBox.Text = "";
        ScriptStatus.Text = $"Added \"{name}\" - it's on the Tidy up page in the notch.";
        ScriptStatus.Foreground = (Brush)FindResource("Accent");
        BuildScriptList();
    }

    private void BrowseScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a script to run from the notch",
            Filter = "Scripts (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1|Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            NewScriptPathBox.Text = dialog.FileName;
            if (NewScriptNameBox.Text.Trim().Length == 0) NewScriptNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void NewScript_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddScript_Click(sender, e);
    }

    private static Button RemoveButton(string tip) => new()
    {
        Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
        Padding = new Thickness(10, 5, 10, 5),
        ToolTip = tip,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
}
