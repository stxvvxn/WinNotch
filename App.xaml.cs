using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace WinNotch;

public partial class App : Application
{
    private static string OpenSettingsFlag => Path.Combine(AppSettings.Folder, "open-settings-on-start");

    /// <summary>
    /// For development: quits, runs run.bat from the project folder (which rebuilds and starts it again),
    /// and opens Settings once it's back. Only shown when running from the code, not the released exe.
    /// </summary>
    public static void HardRestart()
    {
        string? bat = FindRunBat();
        if (bat == null)
        {
            MessageBox.Show("Couldn't find run.bat - this only works when running from the project folder.", "WinNotch");
            return;
        }

        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(OpenSettingsFlag, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\"\"")
            {
                WorkingDirectory = Path.GetDirectoryName(bat)!,
                UseShellExecute = true,
            });
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't run run.bat: {ex.Message}", "WinNotch");
        }
    }

    /// <summary>run.bat in a folder above the exe, i.e. this copy was built from the code.</summary>
    public static string? FindRunBat()
    {
        // The exe runs from bin\Debug\net10.0-..., so look a few folders up
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "run.bat");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        return null;
    }

    /// <summary>True (once) if the last run asked for Settings to open on start.</summary>
    public static bool TakeOpenSettingsFlag()
    {
        try
        {
            if (!File.Exists(OpenSettingsFlag)) return false;
            File.Delete(OpenSettingsFlag);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts a fresh copy and closes this one.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
            Current.Shutdown();
        }
        catch { }
    }

    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one copy at a time. Same name as version 1, so an updated copy waits for the old one to close.
        _mutex = new Mutex(true, "WinNotch_SingleInstance", out bool first);
        if (!first && (e.Args.Contains("--restart") || e.Args.Contains("--updated")))
        {
            // Restarting or just updated: wait for the old copy to finish closing
            try { first = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            Shutdown();
            return;
        }

        MoveToVersion2Data();
        Updater.CleanUpOldVersion();

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// First start of version 2: version 1's files in %AppData%\WinNotch are put away in a "v1-backup" folder
    /// (they don't fit the new settings), anything from the version 2 test copy is brought across, and a few
    /// settings chosen in version 1 (accent colour, start with Windows...) are kept.
    /// </summary>
    private static void MoveToVersion2Data()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = AppSettings.Folder;
            string marker = Path.Combine(folder, "version2");
            if (File.Exists(marker)) return;
            Directory.CreateDirectory(folder);

            // 1. Version 1's files -> v1-backup
            string backup = Path.Combine(folder, "v1-backup");
            var v1Entries = Directory.GetFileSystemEntries(folder)
                .Where(p => !string.Equals(Path.GetFileName(p), "v1-backup", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (v1Entries.Count > 0)
            {
                Directory.CreateDirectory(backup);
                foreach (var entry in v1Entries)
                {
                    string target = Path.Combine(backup, Path.GetFileName(entry));
                    try
                    {
                        if (Directory.Exists(entry)) { if (!Directory.Exists(target)) Directory.Move(entry, target); }
                        else File.Move(entry, target, overwrite: true);
                    }
                    catch { }
                }
            }

            // 2. The version 2 test copy's settings, reminders, shelf and notes
            string testCopy = Path.Combine(appData, "WinNotch" + "V2");
            if (Directory.Exists(testCopy))
            {
                foreach (var file in Directory.GetFiles(testCopy, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (name is "debug.log" or "open-settings-on-start") continue;
                    string target = Path.Combine(folder, Path.GetRelativePath(testCopy, file));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                    }
                    catch { }
                }
            }
            else
            {
                // 3. Coming straight from version 1: keep the simple choices, and its notes become the scratch pad
                CarryOverV1Settings(Path.Combine(backup, "settings.json"), Path.Combine(folder, "settings.json"));
                string notes = Path.Combine(backup, "notes.txt");
                string scratch = Path.Combine(folder, "scratch.txt");
                if (File.Exists(notes) && !File.Exists(scratch)) File.Copy(notes, scratch);
            }

            File.WriteAllText(marker, $"Moved to version 2 on {DateTime.Now:yyyy-MM-dd HH:mm}");
        }
        catch { /* never stop the notch starting */ }
    }

    private static void CarryOverV1Settings(string v1File, string v2File)
    {
        try
        {
            if (!File.Exists(v1File) || File.Exists(v2File)) return;
            if (JsonNode.Parse(File.ReadAllText(v1File)) is not JsonObject v1) return;
            var keep = new JsonObject();
            foreach (var name in new[] { "StartWithWindows", "HideInFullscreen", "Monitor", "Size", "AccentColor", "NotchStyle",
                                         "AutoUpdate", "AiTool", "WebEngine", "SearchFolders" })
            {
                if (v1[name] is { } value) keep[name] = value.DeepClone();
            }
            // Only keep it if this version can read it
            string json = keep.ToJsonString();
            JsonSerializer.Deserialize<AppSettings>(json);
            File.WriteAllText(v2File, json);
        }
        catch { }
    }
}
