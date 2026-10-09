# WinNotch

An Apple-style notch for Windows. The closed notch shows the date and time; hover to open it for a search box
(files, apps, web, AI, maths, reminders), weather, your next calendar event, quick toggles, system info, and
pages for music, app volumes and a file shelf.

See [FEATURES.txt](FEATURES.txt) for everything it does.

## Download

Grab **WinNotch.exe** from the [latest release](../../releases/latest). It's a single portable exe —
no install needed. WinNotch checks here for new versions and offers to update itself
(Settings → General → Updates, or right-click the tray icon → Check for updates).

Version 1 users are offered version 2 automatically. Version 1's settings are kept in
`%AppData%\WinNotch\v1-backup`.

## Building it yourself

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

- `run.bat` — build and run a debug copy
- `build-exe.bat` — build the portable exe into `publish\`
