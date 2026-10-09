using System.IO;

namespace WinNotch;

/// <summary>
/// A small diagnostic log (%AppData%\WinNotch\debug.log) to track down open/close and focus problems.
/// Kept under ~200 KB.
/// </summary>
internal static class DebugLog
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "debug.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 200_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
