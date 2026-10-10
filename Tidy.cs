using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinNotch;

/// <summary>A folder whose contents the Tidy up page clears out (the folder itself is kept).</summary>
public sealed class CleanPath
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A .bat / .cmd / .ps1 (or any program) the Tidy up page can run with one click.</summary>
public sealed class TidyScript
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool ShowWindow { get; set; }   // off = runs quietly in the background
    public bool RunAsAdmin { get; set; }
}

public sealed record CleanResult(long BytesFreed, int FilesDeleted, int FilesSkipped);
public sealed record ScriptResult(bool Ok, int ExitCode, string Output);

/// <summary>Measuring and clearing temp folders, emptying the Recycle Bin, and running your own scripts.</summary>
public static class Tidy
{
    public static List<CleanPath> DefaultPaths() => new()
    {
        new() { Path = "%TEMP%" },
        new() { Path = @"C:\Windows\Temp" },
    };

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>
    /// Folders that must never be emptied: drive roots, Windows itself, Program Files, the Users folder,
    /// your profile and its main folders. Their temp/cache sub-folders are fine.
    /// </summary>
    public static string? WhyNotAllowed(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(Expand(path)).TrimEnd('\\'); }
        catch { return "That isn't a valid folder path."; }

        if (full.Length <= 3) return "A whole drive can't be cleaned - pick a folder inside it.";
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');
        var blocked = new[]
        {
            Special(Environment.SpecialFolder.Windows),
            Special(Environment.SpecialFolder.System),
            Special(Environment.SpecialFolder.ProgramFiles),
            Special(Environment.SpecialFolder.ProgramFilesX86),
            Special(Environment.SpecialFolder.CommonApplicationData),
            Special(Environment.SpecialFolder.UserProfile),
            Special(Environment.SpecialFolder.ApplicationData),
            Special(Environment.SpecialFolder.LocalApplicationData),
            Special(Environment.SpecialFolder.Desktop),
            Special(Environment.SpecialFolder.MyDocuments),
            Special(Environment.SpecialFolder.MyPictures),
            Special(Environment.SpecialFolder.MyMusic),
            Special(Environment.SpecialFolder.MyVideos),
            System.IO.Path.Combine(Special(Environment.SpecialFolder.UserProfile), "Downloads"),
            System.IO.Path.Combine(Special(Environment.SpecialFolder.UserProfile), "OneDrive"),
            System.IO.Path.GetDirectoryName(Special(Environment.SpecialFolder.UserProfile)) ?? "", // C:\Users
        };
        if (blocked.Any(b => b.Length > 0 && string.Equals(b, full, StringComparison.OrdinalIgnoreCase)))
            return "That folder holds important files, so it can't be cleaned out. Pick a temp or cache folder inside it instead.";
        return null;
    }

    // ---------- Measuring ----------

    /// <summary>How much would be cleared (files older than the cut-off).</summary>
    public static long Measure(string path, int olderThanHours, CancellationToken cancel = default)
    {
        string root = Expand(path);
        if (!Directory.Exists(root)) return 0;
        var cutoff = DateTime.Now.AddHours(-Math.Max(0, olderThanHours));
        long total = 0;
        foreach (var file in Files(root))
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                var info = new FileInfo(file);
                if (olderThanHours <= 0 || info.LastWriteTime < cutoff) total += info.Length;
            }
            catch { }
        }
        return total;
    }

    public static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    // ---------- Cleaning ----------

    /// <summary>Deletes the files inside (skipping ones in use or newer than the cut-off), then empty sub-folders.</summary>
    public static CleanResult Clean(string path, int olderThanHours, CancellationToken cancel = default)
    {
        string root = Expand(path);
        if (WhyNotAllowed(root) != null || !Directory.Exists(root)) return new CleanResult(0, 0, 0);
        var cutoff = DateTime.Now.AddHours(-Math.Max(0, olderThanHours));
        long freed = 0;
        int deleted = 0, skipped = 0;

        foreach (var file in Files(root).ToList())
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                var info = new FileInfo(file);
                if (olderThanHours > 0 && info.LastWriteTime >= cutoff) { skipped++; continue; }
                long size = info.Length;
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                freed += size;
                deleted++;
            }
            catch
            {
                skipped++; // in use, or needs admin
            }
        }

        // Then any folders left empty, deepest first (the top folder itself stays)
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                     .OrderByDescending(d => d.Length).ToList())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }

        return new CleanResult(freed, deleted, skipped);
    }

    public static long EmptyRecycleBin()
    {
        long size = RecycleBinSize();
        // No confirmation box, no progress window, no sound
        return SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) == 0 ? size : 0;
    }

    private static IEnumerable<string> Files(string root)
    {
        try
        {
            // Don't follow links/junctions out of the folder
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    // ---------- Scripts ----------

    /// <summary>Runs a script and waits for it. Quiet scripts have their output collected for the notch to show.</summary>
    public static async Task<ScriptResult> RunAsync(TidyScript script, CancellationToken cancel)
    {
        string path = Expand(script.Path);
        if (!File.Exists(path)) return new ScriptResult(false, -1, "The file isn't there any more.");
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        var psi = ext switch
        {
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe", $"/c \"\"{path}\"\""),
            ".ps1" => new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\""),
            _ => new ProcessStartInfo(path),
        };
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(path)!;

        // Run as admin or in its own window: Windows handles the window, so there's no output to collect
        bool capture = !script.ShowWindow && !script.RunAsAdmin;
        if (capture)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        else
        {
            psi.UseShellExecute = true;
            if (script.RunAsAdmin) psi.Verb = "runas";
            if (!script.ShowWindow) psi.WindowStyle = ProcessWindowStyle.Hidden;
        }

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { return new ScriptResult(false, -1, ex.Message); } // e.g. "No" on the admin prompt
        if (process == null) return new ScriptResult(false, -1, "It didn't start.");

        using (process)
        {
            var output = new StringBuilder();
            if (capture)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            try
            {
                await process.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ScriptResult(false, -1, "Stopped.");
            }
            string text;
            lock (output) text = output.ToString().Trim();
            return new ScriptResult(process.ExitCode == 0, process.ExitCode, text);
        }
    }

    [StructLayout(LayoutKind.Sequential)] // natural packing on 64-bit Windows
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
