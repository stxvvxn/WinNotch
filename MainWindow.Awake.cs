using System.Diagnostics;
using System.Windows.Threading;

namespace WinNotch;

// Keep the PC awake while chosen apps are open (OBS, a game, a video player...),
// and work out the overall "don't sleep" state with stay-awake and screen recording.
public partial class MainWindow
{
    private readonly DispatcherTimer _awakeTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _awakeTimerReady;
    private string? _awakeBecause; // the app keeping it awake right now

    private void ApplyAwakeApps()
    {
        if (!_awakeTimerReady)
        {
            _awakeTimerReady = true;
            _awakeTimer.Tick += (_, _) => CheckAwakeApps();
        }
        if (AwakeAppNames().Count > 0) _awakeTimer.Start(); else _awakeTimer.Stop();
        CheckAwakeApps();
    }

    private List<string> AwakeAppNames() =>
        _settings.StayAwakeApps.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0)
            .ToList();

    private void CheckAwakeApps()
    {
        string? found = null;
        foreach (var name in AwakeAppNames())
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) { found = name; break; }
            }
            catch { }
        }

        if (found == _awakeBecause) return;
        bool started = found != null && _awakeBecause == null;
        _awakeBecause = found;
        ApplyExecutionState();
        if (started)
            Notify("", "Staying awake", $"while {found} is open", AccentOrange, 3);
    }

    /// <summary>Tells Windows whether it may sleep / turn the screen off, from everything that cares.</summary>
    private void ApplyExecutionState()
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;
        uint flags = ES_CONTINUOUS;
        if (_keepAwake) flags |= ES_SYSTEM_REQUIRED;
        if (_awakeBecause != null) flags |= ES_SYSTEM_REQUIRED | (_settings.StayAwakeScreenOn ? ES_DISPLAY_REQUIRED : 0);
        if (Recording) flags |= ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
        SetThreadExecutionState(flags);
    }
}
