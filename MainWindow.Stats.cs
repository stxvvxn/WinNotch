using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinNotch;

// System info tiles in the open notch: CPU, memory, GPU, disk, network, battery, uptime.
public partial class MainWindow
{
    private sealed class Tile
    {
        public string Key = "";
        public TextBlock Label = null!;
        public TextBlock Value = null!;
        public TextBlock Detail = null!;
        public Border Fill = null!;
        public ColumnDefinition FillCol = null!, RestCol = null!;
        public FrameworkElement Root = null!;
        public bool Compact;
    }

    private readonly Dictionary<string, Tile> _tiles = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastRx = -1, _lastTx = -1;
    private DateTime _lastNetAt;
    private double _cpu, _rx, _tx, _gpu = -1;
    private bool _gpuBusy;
    private DateTime _gpuAt = DateTime.MinValue;

    private static readonly Brush BarGreen = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush BarOrange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush BarRed = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush BarBlue = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>The bar colour when nothing's wrong: green, or the accent colour if chosen in Settings → Theme.</summary>
    private Brush NormalBar => _settings.AccentBars ? (Brush)FindResource("Accent") : BarGreen;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void InitStats()
    {
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start(); // keeps sampling so the numbers are ready the moment it opens
        SampleCpu();
        SampleNetwork();
    }

    private void BuildStatTiles()
    {
        StatsGrid.Children.Clear();
        _tiles.Clear();
        StatsGrid.Visibility = Vis(_settings.ShowSystemInfo);
        if (!_settings.ShowSystemInfo) return;

        bool compact = _settings.CompactStats;
        void Add(string key, string glyph, string label, bool on)
        {
            if (on) StatsGrid.Children.Add(compact ? BuildCompactTile(key, glyph) : BuildTile(key, glyph, label));
        }
        Add("cpu", "", "CPU", _settings.StatCpu);
        Add("memory", "", "Memory", _settings.StatMemory);
        Add("gpu", "", "GPU", _settings.StatGpu);
        Add("disk", "", "Disk", _settings.StatDisk);
        Add("network", "", "Network", _settings.StatNetwork);
        Add("battery", "", "Battery", _settings.StatBattery && HasBattery());
        Add("uptime", "", "Up time", _settings.StatUptime);

        int n = StatsGrid.Children.Count;
        StatsGrid.Columns = compact ? Math.Max(1, n) : n switch { <= 4 => Math.Max(1, n), 5 or 6 => 3, _ => 4 };
        StatsGrid.Margin = compact ? new Thickness(-3, 10, -3, 0) : new Thickness(-4, 10, -4, 0);
        StatsGrid.Visibility = Vis(n > 0);
        UpdateStats();
    }

    private FrameworkElement BuildTile(string key, string glyph, string label)
    {
        var tile = new Tile { Key = key };
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = (Brush)FindResource("Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        tile.Label = new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)FindResource("Secondary") };
        head.Children.Add(tile.Label);
        stack.Children.Add(head);

        tile.Value = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0),
            Text = "–",
        };
        Typography.SetNumeralAlignment(tile.Value, FontNumeralAlignment.Tabular);
        stack.Children.Add(tile.Value);

        tile.Detail = new TextBlock
        {
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        stack.Children.Add(tile.Detail);

        // Thin usage bar
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 7, 0, 0) };
        tile.FillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        tile.RestCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        bar.ColumnDefinitions.Add(tile.FillCol);
        bar.ColumnDefinitions.Add(tile.RestCol);
        var track = new Border { Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)), CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        tile.Fill = new Border { CornerRadius = new CornerRadius(2), Background = BarGreen };
        bar.Children.Add(tile.Fill);
        stack.Children.Add(bar);

        var root = new Border { Style = (Style)FindResource("Tile"), Child = stack };
        tile.Root = root;
        if (key == "disk")
        {
            // Click to flick between the drives chosen in Settings
            root.MouseLeftButtonUp += (_, _) =>
            {
                var drives = DiskDrives();
                if (drives.Count < 2) return;
                _diskIndex = (_diskIndex + 1) % drives.Count;
                UpdateStats();
            };
        }
        _tiles[key] = tile;
        return root;
    }

    /// <summary>The one-line version: icon, short name and the number, in a small pill.</summary>
    private FrameworkElement BuildCompactTile(string key, string glyph)
    {
        var tile = new Tile { Key = key };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = (Brush)FindResource("Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        });
        tile.Label = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        tile.Label.Text = key switch { "memory" => "RAM", "network" => "", "battery" => "", "uptime" => "Up", "disk" => "C:", _ => key.ToUpperInvariant() };
        row.Children.Add(tile.Label);
        tile.Value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Text = "–", TextTrimming = TextTrimming.CharacterEllipsis };
        Typography.SetNumeralAlignment(tile.Value, FontNumeralAlignment.Tabular);
        row.Children.Add(tile.Value);

        // Not shown in the one-line version, but kept so the updates work the same way
        tile.Detail = new TextBlock();
        tile.Fill = new Border();
        tile.FillCol = new ColumnDefinition();
        tile.RestCol = new ColumnDefinition();
        tile.Compact = true;

        var root = new Border
        {
            Background = (Brush)FindResource("Surface"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(3),
            Child = row,
        };
        tile.Root = root;
        if (key == "disk") root.MouseLeftButtonUp += (_, _) => NextDisk();
        _tiles[key] = tile;
        return root;
    }

    private void NextDisk()
    {
        var drives = DiskDrives();
        if (drives.Count < 2) return;
        _diskIndex = (_diskIndex + 1) % drives.Count;
        UpdateStats();
    }

    private void SetTile(string key, string value, string detail, double? fraction, Brush? colour = null)
    {
        if (!_tiles.TryGetValue(key, out var t)) return;
        t.Value.Text = value;
        if (t.Compact)
        {
            // One line: no bar, so warn by colouring the number instead
            double frac = fraction ?? 0;
            var warn = colour ?? (frac > 0.9 ? BarRed : frac > 0.7 ? BarOrange : null);
            t.Value.Foreground = fraction != null && (warn == BarRed || warn == BarOrange) ? warn : Brushes.White;
            t.Root.ToolTip = detail.Length > 0 ? detail : null;
            return;
        }
        t.Detail.Text = detail;
        double f = Math.Clamp(fraction ?? 0, 0, 1);
        t.FillCol.Width = new GridLength(f, GridUnitType.Star);
        t.RestCol.Width = new GridLength(1 - f, GridUnitType.Star);
        t.Fill.Visibility = Vis(fraction != null);
        t.Fill.Background = colour ?? (f > 0.9 ? BarRed : f > 0.7 ? BarOrange : NormalBar);
    }

    private void UpdateStats()
    {
        SampleCpu();
        SampleNetwork();
        if (!_expanded || !_settings.ShowSystemInfo) return;

        SetTile("cpu", $"{_cpu:0}%", $"{Environment.ProcessorCount} threads", _cpu / 100);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1_073_741_824.0, total = mem.ullTotalPhys / 1_073_741_824.0;
            SetTile("memory", $"{mem.dwMemoryLoad}%", $"{used:0.0} of {total:0} GB", mem.dwMemoryLoad / 100.0);
        }

        if (_tiles.ContainsKey("gpu"))
        {
            if (!_gpuBusy && DateTime.Now - _gpuAt > TimeSpan.FromSeconds(2)) _ = SampleGpuAsync();
            if (_gpu >= 0) SetTile("gpu", $"{_gpu:0}%", "3D engine", _gpu / 100);
            else SetTile("gpu", "–", "Reading…", null);
        }

        if (_tiles.TryGetValue("disk", out var diskTile))
        {
            var drives = DiskDrives();
            if (_diskIndex >= drives.Count) _diskIndex = 0;
            string letter = drives[_diskIndex];
            diskTile.Label.Text = diskTile.Compact ? $"{letter}:" : $"Disk {letter}:";
            diskTile.Root.Cursor = drives.Count > 1 ? System.Windows.Input.Cursors.Hand : null;
            if (!diskTile.Compact)
                diskTile.Root.ToolTip = drives.Count > 1 ? $"Click to switch drive ({string.Join(", ", drives.Select(d => d + ":"))})" : null;
            string position = drives.Count > 1 ? $" · {_diskIndex + 1}/{drives.Count}" : "";
            try
            {
                var d = new DriveInfo(letter);
                if (!d.IsReady) throw new IOException();
                double usedFrac = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
                // Disk: green up to 69%, orange 70-79%, red from 80%
                double pct = Math.Round(usedFrac * 100);
                SetTile("disk", $"{pct:0}%", $"{FormatSize(d.AvailableFreeSpace)} free{position}", usedFrac,
                        pct >= 80 ? BarRed : pct >= 70 ? BarOrange : NormalBar);
            }
            catch
            {
                SetTile("disk", "–", $"Not connected{position}", null);
            }
        }

        SetTile("network", $"↓ {Rate(_rx)}", $"↑ {Rate(_tx)}", null);

        if (_tiles.ContainsKey("battery") && GetSystemPowerStatus(out var p) && p.BatteryLifePercent <= 100)
        {
            bool charging = p.ACLineStatus == 1;
            string detail = charging ? "Charging"
                : p.BatteryLifeTime > 0 ? $"{TimeSpan.FromSeconds(p.BatteryLifeTime):h\\:mm} left" : "On battery";
            SetTile("battery", $"{p.BatteryLifePercent}%", detail, p.BatteryLifePercent / 100.0,
                    charging ? BarGreen : p.BatteryLifePercent <= 20 ? BarRed : _settings.AccentBars ? NormalBar : BarBlue);
        }

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        SetTile("uptime", up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m",
                $"Since {DateTime.Now - up:ddd HH:mm}", null);
    }

    private int _diskIndex;

    /// <summary>The drive letters from Settings (just C if none are chosen).</summary>
    private List<string> DiskDrives()
    {
        var list = _settings.DiskDrives
            .Select(d => d.Trim().TrimEnd(':', '\\').ToUpperInvariant())
            .Where(d => d.Length == 1 && char.IsLetter(d[0]))
            .Distinct()
            .ToList();
        return list.Count > 0 ? list : new List<string> { "C" };
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_099_511_627_776 ? $"{bytes / 1_099_511_627_776.0:0.0} TB" : $"{bytes / 1_073_741_824.0:0} GB";

    private static string Rate(double bytesPerSecond) =>
        bytesPerSecond >= 1_000_000 ? $"{bytesPerSecond / 1_000_000:0.0} MB/s"
        : bytesPerSecond >= 1000 ? $"{bytesPerSecond / 1000:0} KB/s"
        : $"{bytesPerSecond:0} B/s";

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return;
        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
        if (_lastKernel != 0)
        {
            ulong total = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
            if (total > 0)
            {
                double busy = 100.0 * (total - (idle - _lastIdle)) / total;
                _cpu = Math.Clamp(_cpu * 0.4 + busy * 0.6, 0, 100);
            }
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
    }

    private void SampleNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { return; }

        var now = DateTime.Now;
        double seconds = (now - _lastNetAt).TotalSeconds;
        if (_lastRx >= 0 && seconds > 0.2)
        {
            _rx = _rx * 0.4 + Math.Max(0, (rx - _lastRx) / seconds) * 0.6;
            _tx = _tx * 0.4 + Math.Max(0, (tx - _lastTx) / seconds) * 0.6;
        }
        _lastRx = rx;
        _lastTx = tx;
        _lastNetAt = now;
    }

    // GPU use: the busiest "3D" engine across all apps (what Task Manager shows), read in the background
    private async Task SampleGpuAsync()
    {
        _gpuBusy = true;
        _gpuAt = DateTime.Now;
        try
        {
            _gpu = await Task.Run(() =>
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var counters = category.GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                    .ToList();
                try
                {
                    foreach (var c in counters) c.NextValue(); // first reading is always 0
                    Thread.Sleep(500);
                    // Add up every app's use of the 3D engine
                    double total = counters.Sum(c => { try { return (double)c.NextValue(); } catch { return 0; } });
                    return Math.Clamp(total, 0, 100);
                }
                finally
                {
                    foreach (var c in counters) c.Dispose();
                }
            });
        }
        catch
        {
            _gpu = -1;
            if (_tiles.ContainsKey("gpu")) SetTile("gpu", "–", "Not available", null);
        }
        finally
        {
            _gpuBusy = false;
        }
    }

    private static bool HasBattery() =>
        GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryLifePercent <= 100;

    private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
                                              out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
                                              out System.Runtime.InteropServices.ComTypes.FILETIME user);
}
