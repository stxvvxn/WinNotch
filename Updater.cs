using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WinNotch;

/// <summary>One published version, for the release notes in Settings → About.</summary>
public sealed record ReleaseInfo(string Tag, string Notes, DateTime Published, string PageUrl)
{
    public Version Version => Updater.TryParseVersion(Tag, out var v) ? v : new Version(0, 0, 0);
}

/// <summary>A newer WinNotch found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, string PageUrl);

/// <summary>
/// Checks the GitHub repo's latest release for a newer WinNotch.exe, downloads it and swaps it in.
/// A running exe can't be overwritten, but it can be renamed - so the old one becomes WinNotch.exe.old,
/// the new one takes its place, and the .old file is deleted next time WinNotch starts.
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinNotch-Updater"); // GitHub requires one
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>True for the single-file exe from GitHub or build-exe.bat; false when run from Visual Studio / run.bat.</summary>
    public static bool CanSelfUpdate =>
        ExePath is { } exe && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "WinNotch.dll"));

    /// <summary>Returns the latest release if it's newer than this copy, otherwise null. Throws if GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string repo)
    {
        string json = await Http.GetStringAsync($"https://api.github.com/repos/{repo.Trim().Trim('/')}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest) || latest <= CurrentVersion) return null;

        string? url = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals("WinNotch.exe", StringComparison.OrdinalIgnoreCase))
                url = asset.GetProperty("browser_download_url").GetString();
        }
        if (url == null) return null; // release without an exe (still building?)

        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateInfo(latest, tag, notes, url, page);
    }

    /// <summary>Every release on GitHub, newest first (saved on the PC so they still show offline).</summary>
    public static async Task<(List<ReleaseInfo> Releases, bool FromCache)> GetReleasesAsync(string repo)
    {
        string cache = Path.Combine(AppSettings.Folder, "releases.json");
        try
        {
            var all = new List<ReleaseInfo>();
            for (int page = 1; page <= 5; page++)
            {
                string json = await Http.GetStringAsync($"https://api.github.com/repos/{repo.Trim().Trim('/')}/releases?per_page=100&page={page}");
                using var doc = JsonDocument.Parse(json);
                int count = 0;
                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    count++;
                    if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                    string tag = r.GetProperty("tag_name").GetString() ?? "";
                    string notes = r.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                    DateTime published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetDateTime().ToLocalTime() : DateTime.MinValue;
                    string pageUrl = r.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
                    all.Add(new ReleaseInfo(tag, CleanNotes(notes), published, pageUrl));
                }
                if (count < 100) break;
            }
            all = all.OrderByDescending(r => r.Version).ThenByDescending(r => r.Published).ToList();
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(cache, JsonSerializer.Serialize(all));
            }
            catch { }
            return (all, false);
        }
        catch
        {
            try
            {
                if (File.Exists(cache))
                    return (JsonSerializer.Deserialize<List<ReleaseInfo>>(File.ReadAllText(cache)) ?? new(), true);
            }
            catch { }
            throw;
        }
    }

    /// <summary>Drops the behind-the-scenes lines (co-author / session links) from a release's notes.</summary>
    private static string CleanNotes(string notes)
    {
        var lines = notes.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.StartsWith("Co-Authored-By:", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("Claude-Session:", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("Signed-off-by:", StringComparison.OrdinalIgnoreCase)
                     && !l.Contains("Generated with [Claude Code]"));
        return string.Join("\n", lines).Trim();
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Downloads the new exe, swaps it in and starts it. The caller should then shut down.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        string exe = ExePath ?? throw new InvalidOperationException("Can't find WinNotch.exe");
        string newFile = exe + ".new";
        string oldFile = exe + ".old";

        // 1. Download next to the current exe
        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(newFile);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        if (new FileInfo(newFile).Length < 1_000_000) throw new InvalidDataException("The download looks incomplete");

        // 2. Swap: running exe -> .old, new -> exe
        if (File.Exists(oldFile)) File.Delete(oldFile);
        File.Move(exe, oldFile);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(oldFile, exe); // put things back
            throw;
        }

        // 3. Start the new copy; it waits for this one to close
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
    }

    /// <summary>Deletes the previous version left behind by an update.</summary>
    public static void CleanUpOldVersion()
    {
        if (ExePath is not { } exe) return;
        Task.Run(async () =>
        {
            foreach (var leftover in new[] { exe + ".old", exe + ".new" })
            {
                for (int attempt = 0; attempt < 10 && File.Exists(leftover); attempt++)
                {
                    try { File.Delete(leftover); }
                    catch { await Task.Delay(1000); } // old copy may still be closing
                }
            }
        });
    }
}
