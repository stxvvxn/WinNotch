namespace WinNotch;

// The actions.json written on first run. Edit the copy in %AppData%\WinNotch, not this one.
internal static class DefaultActions
{
    public const string Json = """
// WinNotch quick actions
// ----------------------
// When one of these apps is in front, hovering the notch shows its buttons (up to 6).
//
//   "processes": the app's process name as shown in Task Manager > Details, without ".exe"
//   "windowClasses": (optional) only match these kinds of window, e.g. "CabinetWClass" for File Explorer
//   "label":     text under the button
//   "icon":      a Segoe Fluent Icons code, e.g. "E74E" (save). Full list:
//                https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font
//   "keys":      the shortcut to press, e.g. "Ctrl+Shift+S", "Alt+Left", "F5", "Space", "Delete"
//   "run":       (instead of "keys") a file, script, program or link to open, e.g. "C:\\Scripts\\resize.jsx"
//                Put {clipboard} in it to insert the text you last copied (web-safe), e.g. in a link
//
// Save this file and the notch updates straight away.
{
  "apps": [
    {
      "name": "Photoshop",
      "processes": [ "Photoshop" ],
      "actions": [
        // Quick Export has no shortcut by default. In Photoshop go to
        // Edit > Keyboard Shortcuts > File > Export > Quick Export as PNG and set it to Ctrl+Alt+Shift+P
        { "label": "Quick Export", "icon": "E898", "keys": "Ctrl+Alt+Shift+P" },
        { "label": "Export As",    "icon": "E78C", "keys": "Ctrl+Alt+Shift+W" },
        { "label": "Save As",      "icon": "E792", "keys": "Ctrl+Shift+S" },
        { "label": "Transform",    "icon": "E7AD", "keys": "Ctrl+T" },
        { "label": "Fit Screen",   "icon": "E740", "keys": "Ctrl+0" },
        { "label": "Undo",         "icon": "E7A7", "keys": "Ctrl+Z" }
      ]
    },
    {
      "name": "CapCut",
      "processes": [ "CapCut" ],
      "actions": [
        { "label": "Split",      "icon": "E8C6", "keys": "Ctrl+B" },
        { "label": "Delete",     "icon": "E74D", "keys": "Delete" },
        { "label": "Play/Pause", "icon": "E768", "keys": "Space" },
        { "label": "Undo",       "icon": "E7A7", "keys": "Ctrl+Z" },
        { "label": "Import",     "icon": "E8E5", "keys": "Ctrl+I" },
        { "label": "Export",     "icon": "E898", "keys": "Ctrl+E" }
      ]
    },
    {
      "name": "Browser",
      "processes": [ "chrome", "msedge" ],
      "actions": [
        { "label": "New Tab",    "icon": "E710", "keys": "Ctrl+T" },
        { "label": "Reopen Tab", "icon": "E81C", "keys": "Ctrl+Shift+T" },
        { "label": "Back",       "icon": "E72B", "keys": "Alt+Left" },
        { "label": "Reload",     "icon": "E72C", "keys": "F5" },
        { "label": "Downloads",  "icon": "E896", "keys": "Ctrl+J" },
        { "label": "Private",    "icon": "E72E", "keys": "Ctrl+Shift+N" }
      ]
    },
    {
      "name": "Firefox",
      "processes": [ "firefox" ],
      "actions": [
        { "label": "New Tab",    "icon": "E710", "keys": "Ctrl+T" },
        { "label": "Reopen Tab", "icon": "E81C", "keys": "Ctrl+Shift+T" },
        { "label": "Back",       "icon": "E72B", "keys": "Alt+Left" },
        { "label": "Reload",     "icon": "E72C", "keys": "F5" },
        { "label": "Downloads",  "icon": "E896", "keys": "Ctrl+Shift+Y" },
        { "label": "Private",    "icon": "E72E", "keys": "Ctrl+Shift+P" }
      ]
    },
    {
      "name": "File Explorer",
      "processes": [ "explorer" ],
      "windowClasses": [ "CabinetWClass" ], // folder windows only, not the desktop or taskbar
      "actions": [
        { "label": "New Folder", "icon": "E8F4", "keys": "Ctrl+Shift+N" },
        { "label": "Rename",     "icon": "E8AC", "keys": "F2" },
        { "label": "Copy Path",  "icon": "E8C8", "keys": "Ctrl+Shift+C" },
        { "label": "Up",         "icon": "E74A", "keys": "Alt+Up" },
        { "label": "New Tab",    "icon": "E710", "keys": "Ctrl+T" },
        { "label": "Properties", "icon": "E946", "keys": "Alt+Enter" }
      ]
    },
    {
      "name": "Word",
      "processes": [ "WINWORD" ],
      "actions": [
        { "label": "Save As",       "icon": "E792", "keys": "F12" },
        { "label": "Bold",          "icon": "E8DD", "keys": "Ctrl+B" },
        { "label": "Comment",       "icon": "E90A", "keys": "Ctrl+Alt+M" },
        { "label": "Word Count",    "icon": "E8FD", "keys": "Ctrl+Shift+G" },
        { "label": "Track Changes", "icon": "E70F", "keys": "Ctrl+Shift+E" },
        { "label": "Replace",       "icon": "E721", "keys": "Ctrl+H" }
      ]
    },
    {
      "name": "Outlook",
      "processes": [ "OUTLOOK" ], // classic Outlook
      "actions": [
        { "label": "New Email", "icon": "E715", "keys": "Ctrl+Shift+M" },
        { "label": "Reply",     "icon": "E8CA", "keys": "Ctrl+R" },
        { "label": "Reply All", "icon": "E8C2", "keys": "Ctrl+Shift+R" },
        { "label": "Forward",   "icon": "E89C", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E8C3", "keys": "Ctrl+Q" },
        { "label": "Calendar",  "icon": "E787", "keys": "Ctrl+2" }
      ]
    },
    {
      "name": "Outlook",
      "processes": [ "olk" ], // the new Outlook app
      "actions": [
        { "label": "New Email", "icon": "E715", "keys": "Ctrl+N" },
        { "label": "Reply",     "icon": "E8CA", "keys": "Ctrl+R" },
        { "label": "Reply All", "icon": "E8C2", "keys": "Ctrl+Shift+R" },
        { "label": "Forward",   "icon": "E89C", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E8C3", "keys": "Ctrl+Q" },
        { "label": "Calendar",  "icon": "E787", "keys": "Ctrl+2" }
      ]
    },
    {
      "name": "Discord",
      "processes": [ "Discord" ],
      "actions": [
        { "label": "Mute",      "icon": "E720", "keys": "Ctrl+Shift+M" },
        { "label": "Deafen",    "icon": "E7F6", "keys": "Ctrl+Shift+D" },
        { "label": "Switcher",  "icon": "E8AB", "keys": "Ctrl+K" },
        { "label": "Search",    "icon": "E721", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E73E", "keys": "Shift+Escape" },
        { "label": "Upload",    "icon": "E898", "keys": "Ctrl+Shift+U" }
      ]
    },
    {
      "name": "Claude",
      "processes": [ "Claude" ],
      "actions": [
        // These open Claude links. {clipboard} is replaced with whatever text you last copied.
        { "label": "New Chat",  "icon": "E8BD", "run": "claude://claude.ai/new" },
        { "label": "Explain",   "icon": "E897", "run": "claude://claude.ai/new?q=Explain%20this%20in%20simple%20terms%3A%0A%0A{clipboard}" },
        { "label": "Summarise", "icon": "E8A5", "run": "claude://claude.ai/new?q=Summarise%20this%3A%0A%0A{clipboard}" },
        { "label": "Fix Writing", "icon": "E70F", "run": "claude://claude.ai/new?q=Fix%20the%20spelling%20and%20grammar%2C%20keeping%20my%20tone%3A%0A%0A{clipboard}" },
        { "label": "Code",      "icon": "E943", "run": "claude://code/new" },
        { "label": "New Task",  "icon": "E8FD", "run": "claude://cowork/new" }
      ]
    }
  ]
}
""";

    // Added to an existing actions.json once
    public const string WorkAppsBlock = """
    {
      "name": "Word",
      "processes": [ "WINWORD" ],
      "actions": [
        { "label": "Save As",       "icon": "E792", "keys": "F12" },
        { "label": "Bold",          "icon": "E8DD", "keys": "Ctrl+B" },
        { "label": "Comment",       "icon": "E90A", "keys": "Ctrl+Alt+M" },
        { "label": "Word Count",    "icon": "E8FD", "keys": "Ctrl+Shift+G" },
        { "label": "Track Changes", "icon": "E70F", "keys": "Ctrl+Shift+E" },
        { "label": "Replace",       "icon": "E721", "keys": "Ctrl+H" }
      ]
    },
    {
      "name": "Outlook",
      "processes": [ "OUTLOOK" ], // classic Outlook
      "actions": [
        { "label": "New Email", "icon": "E715", "keys": "Ctrl+Shift+M" },
        { "label": "Reply",     "icon": "E8CA", "keys": "Ctrl+R" },
        { "label": "Reply All", "icon": "E8C2", "keys": "Ctrl+Shift+R" },
        { "label": "Forward",   "icon": "E89C", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E8C3", "keys": "Ctrl+Q" },
        { "label": "Calendar",  "icon": "E787", "keys": "Ctrl+2" }
      ]
    },
    {
      "name": "Outlook",
      "processes": [ "olk" ], // the new Outlook app
      "actions": [
        { "label": "New Email", "icon": "E715", "keys": "Ctrl+N" },
        { "label": "Reply",     "icon": "E8CA", "keys": "Ctrl+R" },
        { "label": "Reply All", "icon": "E8C2", "keys": "Ctrl+Shift+R" },
        { "label": "Forward",   "icon": "E89C", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E8C3", "keys": "Ctrl+Q" },
        { "label": "Calendar",  "icon": "E787", "keys": "Ctrl+2" }
      ]
    },
    {
      "name": "Discord",
      "processes": [ "Discord" ],
      "actions": [
        { "label": "Mute",      "icon": "E720", "keys": "Ctrl+Shift+M" },
        { "label": "Deafen",    "icon": "E7F6", "keys": "Ctrl+Shift+D" },
        { "label": "Switcher",  "icon": "E8AB", "keys": "Ctrl+K" },
        { "label": "Search",    "icon": "E721", "keys": "Ctrl+F" },
        { "label": "Mark Read", "icon": "E73E", "keys": "Shift+Escape" },
        { "label": "Upload",    "icon": "E898", "keys": "Ctrl+Shift+U" }
      ]
    }
""";

    // Added to an existing actions.json once
    public const string ExplorerBlock = """
    {
      "name": "File Explorer",
      "processes": [ "explorer" ],
      "windowClasses": [ "CabinetWClass" ], // folder windows only, not the desktop or taskbar
      "actions": [
        { "label": "New Folder", "icon": "E8F4", "keys": "Ctrl+Shift+N" },
        { "label": "Rename",     "icon": "E8AC", "keys": "F2" },
        { "label": "Copy Path",  "icon": "E8C8", "keys": "Ctrl+Shift+C" },
        { "label": "Up",         "icon": "E74A", "keys": "Alt+Up" },
        { "label": "New Tab",    "icon": "E710", "keys": "Ctrl+T" },
        { "label": "Properties", "icon": "E946", "keys": "Alt+Enter" }
      ]
    }
""";

    // Added the first time ROG Ally mode is switched on
    public const string AllyBlock = """
    {
      "name": "Steam",
      "processes": [ "steam", "steamwebhelper" ],
      "actions": [
        { "label": "Big Picture", "icon": "E7FC", "run": "steam://open/bigpicture" },
        { "label": "Library",     "icon": "E8F1", "run": "steam://open/games" },
        { "label": "Store",       "icon": "E719", "run": "steam://store" },
        { "label": "Downloads",   "icon": "E896", "run": "steam://open/downloads" },
        { "label": "Friends",     "icon": "E716", "run": "steam://open/friends" }
      ]
    },
    {
      "name": "Xbox",
      "processes": [ "XboxPcApp" ],
      "actions": [
        { "label": "Game Bar",    "icon": "E7FC", "keys": "Win+G" },
        { "label": "Screenshot",  "icon": "E722", "keys": "Win+Alt+PrintScreen" },
        { "label": "Record That", "icon": "E7C8", "keys": "Win+Alt+G" },
        { "label": "Record",      "icon": "E714", "keys": "Win+Alt+R" },
        { "label": "Mic",         "icon": "E720", "keys": "Win+Alt+M" }
      ]
    }
""";

    // Added to an existing actions.json once, for people who set WinNotch up before these existed
    public const string ClaudeBlock = """
    {
      "name": "Claude",
      "processes": [ "Claude" ],
      "actions": [
        // These open Claude links. {clipboard} is replaced with whatever text you last copied.
        { "label": "New Chat",  "icon": "E8BD", "run": "claude://claude.ai/new" },
        { "label": "Explain",   "icon": "E897", "run": "claude://claude.ai/new?q=Explain%20this%20in%20simple%20terms%3A%0A%0A{clipboard}" },
        { "label": "Summarise", "icon": "E8A5", "run": "claude://claude.ai/new?q=Summarise%20this%3A%0A%0A{clipboard}" },
        { "label": "Fix Writing", "icon": "E70F", "run": "claude://claude.ai/new?q=Fix%20the%20spelling%20and%20grammar%2C%20keeping%20my%20tone%3A%0A%0A{clipboard}" },
        { "label": "Code",      "icon": "E943", "run": "claude://code/new" },
        { "label": "New Task",  "icon": "E8FD", "run": "claude://cowork/new" }
      ]
    }
""";
}
