using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinNotch;

// System stats in the open notch: CPU use, memory use and network speed (down / up).
public partial class MainWindow
{
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _statsTimerReady;
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastStatRx = -1, _lastStatTx = -1;
    private DateTime _lastStatAt;
    private double _statRx, _statTx, _statCpu;

    private void ApplyStats()
    {
        if (!_statsTimerReady)
        {
            _statsTimerReady = true;
            _statsTimer.Tick += (_, _) => UpdateStats();
        }

        CpuStat.Visibility = Vis(_settings.StatsCpu);
        RamStat.Visibility = Vis(_settings.StatsRam);
        NetStat.Visibility = Vis(_settings.StatsNet);

        // Keeps sampling in the background so numbers are ready the moment the notch opens
        if (_settings.ShowStats) { _statsTimer.Start(); UpdateStats(); }
        else _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        if (!_settings.ShowStats) return;

        if (_settings.StatsCpu) SampleCpu();
        if (_settings.StatsNet) SampleStatsNetwork();
        if (!_expanded) return; // nothing to draw

        if (_settings.StatsCpu) CpuText.Text = $"{_statCpu:0}%";

        if (_settings.StatsRam)
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1_073_741_824.0;
                double totalGb = mem.ullTotalPhys / 1_073_741_824.0;
                RamText.Text = $"{mem.dwMemoryLoad}% · {usedGb:0.0}/{totalGb:0} GB";
            }
        }

        if (_settings.StatsNet) NetText.Text = $"↓ {FormatRate(_statRx)}  ↑ {FormatRate(_statTx)}";
    }

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return;
        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);

        if (_lastKernel != 0)
        {
            ulong idleDelta = idle - _lastIdle;
            ulong total = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
            if (total > 0)
            {
                double busy = 100.0 * (total - idleDelta) / total;
                _statCpu = Math.Clamp(_statCpu * 0.4 + busy * 0.6, 0, 100);
            }
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
    }

    private void SampleStatsNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = ni.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch
        {
            return;
        }

        var now = DateTime.Now;
        double seconds = (now - _lastStatAt).TotalSeconds;
        if (_lastStatRx >= 0 && seconds > 0.2)
        {
            _statRx = _statRx * 0.4 + Math.Max(0, (rx - _lastStatRx) / seconds) * 0.6;
            _statTx = _statTx * 0.4 + Math.Max(0, (tx - _lastStatTx) / seconds) * 0.6;
        }
        _lastStatRx = rx;
        _lastStatTx = tx;
        _lastStatAt = now;
    }

    private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
                                              out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
                                              out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
