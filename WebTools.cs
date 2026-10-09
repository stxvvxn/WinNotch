using System.Diagnostics;

namespace WinNotch;

/// <summary>The web search engines and AI tools the search box can hand your text to.</summary>
public static class WebTools
{
    public sealed record Tool(string Key, string Name, string Url, bool TakesQuery = true);

    public static readonly Tool[] Engines =
    {
        new("google", "Google", "https://www.google.com/search?q={query}"),
        new("bing", "Bing", "https://www.bing.com/search?q={query}"),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}"),
        new("brave", "Brave Search", "https://search.brave.com/search?q={query}"),
        new("ecosia", "Ecosia", "https://www.ecosia.org/search?q={query}"),
        new("custom", "Custom", ""),
    };

    public static readonly Tool[] AiTools =
    {
        new("claude", "Claude", "https://claude.ai/new?q={query}"),
        new("chatgpt", "ChatGPT", "https://chatgpt.com/?q={query}"),
        new("copilot", "Microsoft Copilot", "https://copilot.microsoft.com/?q={query}"),
        new("perplexity", "Perplexity", "https://www.perplexity.ai/search?q={query}"),
        new("gemini", "Gemini", "https://gemini.google.com/app", TakesQuery: false), // can't take text in the link
        new("custom", "Custom", ""),
    };

    public static Tool Engine(AppSettings s) => Pick(Engines, s.WebEngine, s.CustomWebUrl);
    public static Tool Ai(AppSettings s) => Pick(AiTools, s.AiTool, s.CustomAiUrl);

    private static Tool Pick(Tool[] list, string key, string customUrl)
    {
        var tool = list.FirstOrDefault(t => t.Key == key) ?? list[0];
        if (tool.Key == "custom")
            tool = tool with { Url = customUrl, TakesQuery = customUrl.Contains("{query}") };
        return tool;
    }

    /// <summary>Opens the search engine with the text.</summary>
    public static void SearchWeb(AppSettings s, string text) => Open(Fill(Engine(s).Url, text));

    /// <summary>Opens the AI tool with the text. Returns false when the text had to go on the clipboard instead.</summary>
    public static bool AskAi(AppSettings s, string text)
    {
        var tool = Ai(s);
        if (tool.Key == "claude" && s.ClaudeDesktopApp && HasProtocol("claude")
            && Open("claude://claude.ai/new?q=" + Uri.EscapeDataString(text)))
            return true;
        if (!tool.TakesQuery)
        {
            Open(tool.Url);
            return false; // the caller copies the text so you can paste it
        }
        Open(Fill(tool.Url, text));
        return true;
    }

    private static string Fill(string template, string text) =>
        template.Contains("{query}") ? template.Replace("{query}", Uri.EscapeDataString(text)) : template;

    /// <summary>True if an app is registered for links like claude://</summary>
    private static bool HasProtocol(string scheme)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
