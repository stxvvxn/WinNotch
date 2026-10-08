# WinNotch

A MacBook-style notch / Dynamic Island for Windows: now playing, shelf, quick actions for your apps,
pop-ups, timer, clipboard history, notes, calculator, system stats, bubbles, themes and an ROG Ally mode.

## Download

Grab **WinNotch.exe** from the [latest release](../../releases/latest). It's a single portable exe —
no install needed. WinNotch checks here for new versions and offers to update itself
(Settings → General → Updates, or right-click the tray icon → Check for updates).

## Building it yourself

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

- `run.bat` — build and run a debug copy
- `build-exe.bat` — build the portable exe into `publish\`

## Releasing a new version

Push to `main`. GitHub Actions builds `WinNotch.exe` and publishes it as release `v1.0.<build number>`,
using the commit message as the release notes. Everyone running WinNotch gets offered the update.
