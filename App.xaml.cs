using System.Windows;

namespace WinNotch;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only allow one copy of the notch to run at a time
        _mutex = new Mutex(true, "WinNotch_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance && e.Args.Contains("--updated"))
        {
            // Just updated: the old copy is closing down, so wait for it to let go
            try { isFirstInstance = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirstInstance = true; }
        }
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }
        Updater.CleanUpOldVersion();

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
