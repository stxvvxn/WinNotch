using System.Windows;
using System.Windows.Input;

namespace WinNotch;

// Settings → Home page: finding the weather town, refreshing the calendar
public partial class SettingsWindow
{
    private async void WeatherFind_Click(object sender, RoutedEventArgs e)
    {
        string query = WeatherPlaceBox.Text.Trim();
        if (query.Length == 0)
        {
            WeatherStatus.Text = "Type a town first, e.g. Bristol or Paris, France";
            return;
        }
        WeatherFindButton.IsEnabled = false;
        WeatherStatus.Text = "Looking it up…";
        string? found = await _owner.SetWeatherPlaceAsync(query);
        WeatherFindButton.IsEnabled = _owner.Settings.WeatherEnabled;
        if (found == null)
        {
            WeatherStatus.Text = "Couldn't find that town (or there's no internet). Try adding the country, e.g. Newport, Wales";
            return;
        }
        WeatherPlaceBox.Text = found;
        WeatherStatus.Text = $"Showing the weather for {found}";
    }

    private void WeatherPlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WeatherFind_Click(sender, e);
    }

    private void CalendarRefresh_Click(object sender, RoutedEventArgs e)
    {
        Changed(sender, e); // picks up a link that was just pasted in
        _owner.ReloadCalendar();
    }
}
