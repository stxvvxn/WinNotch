using Microsoft.Win32;
using System.Windows.Threading;

namespace WinNotch;

// Green dot = camera in use, orange dot = microphone in use (like the privacy dots on a Mac)
public partial class MainWindow
{
    private readonly DispatcherTimer _privacyTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private void InitPrivacy()
    {
        _privacyTimer.Tick += (_, _) => UpdatePrivacyDots();
        _privacyTimer.Start();
        UpdatePrivacyDots();
    }

    private void UpdatePrivacyDots()
    {
        bool camera = _settings.PrivacyDots && CapabilityInUse("webcam");
        bool mic = _settings.PrivacyDots && CapabilityInUse("microphone");
        CameraDot.Visibility = Vis(camera);
        MicDot.Visibility = Vis(mic);
    }

    // Windows keeps a record of which apps are using the camera/mic right now.
    // An app is using it if it has a start time but no stop time yet.
    private static bool CapabilityInUse(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
            return root != null && AnyAppInUse(root);
        }
        catch
        {
            return false;
        }
    }

    private static bool AnyAppInUse(RegistryKey key)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var app = key.OpenSubKey(name);
            if (app == null) continue;

            // Regular desktop apps are grouped under "NonPackaged"
            if (name == "NonPackaged")
            {
                if (AnyAppInUse(app)) return true;
                continue;
            }

            if (app.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                return true;
        }
        return false;
    }
}
