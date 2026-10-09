using System.Windows;
using System.Windows.Controls;

namespace WinNotch;

// Settings → About → Release notes: what changed in each version, the one you're running first
public partial class SettingsWindow
{
    private List<ReleaseInfo> _releases = new();

    private async Task LoadReleaseNotesAsync()
    {
        try
        {
            var (releases, fromCache) = await Updater.GetReleasesAsync(_owner.Settings.UpdateRepo);
            _releases = releases;
            ReleaseBox.Items.Clear();
            var current = Updater.CurrentVersion;
            int select = -1;
            for (int i = 0; i < releases.Count; i++)
            {
                var r = releases[i];
                bool running = r.Version == current;
                if (running && select < 0) select = i;
                string label = $"{r.Version}{(running ? "  (this version)" : "")}";
                ReleaseBox.Items.Add(new ComboBoxItem { Content = label, Tag = i });
            }

            if (releases.Count == 0)
            {
                ReleaseInfoText.Text = "No releases on GitHub yet.";
                return;
            }
            // Not one of the published versions (e.g. built from the code): show the newest
            ReleaseBox.SelectedIndex = select >= 0 ? select : 0;
            if (fromCache) ReleaseInfoText.Text += " · couldn't reach GitHub, showing the saved notes";
        }
        catch
        {
            ReleaseInfoText.Text = "Couldn't load the release notes - check your internet connection.";
        }
    }

    private void ReleaseBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if ((ReleaseBox.SelectedItem as ComboBoxItem)?.Tag is not int i || i < 0 || i >= _releases.Count) return;
        var r = _releases[i];
        string date = r.Published > DateTime.MinValue ? r.Published.ToString("d MMMM yyyy") : "";
        bool running = r.Version == Updater.CurrentVersion;
        ReleaseInfoText.Text = running ? $"Released {date} · you're running this one"
            : r.Version > Updater.CurrentVersion ? $"Released {date} · newer than yours"
            : $"Released {date}";
        ReleaseNotesText.Text = r.Notes.Length > 0 ? r.Notes : "No notes for this version.";
    }
}
