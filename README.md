# Windows Toolbelt

A collection of Windows desktop utilities: a Pomodoro timer and saved window layouts.

## Features

- **Pomodoro:** configurable sessions, breaks, frequent durations, sitting/standing reminders, taskbar progress, sound and notifications.
- **Window layout:** click **Save layout** to replace the saved snapshot with the positions, sizes, monitors and minimized/maximized states of your currently open desktop windows. Click **Restore layout** to apply it. Both actions also appear beside Start/Pause and Reset in the taskbar hover preview, and work independently of the timer.

Layouts persist at `%APPDATA%\WindowsToolbelt\window-layout.json`. Restore matches existing windows, skips closed or ambiguous windows, and reports any windows Windows would not allow it to move (for example, an elevated app). It does not launch applications or switch virtual desktops. Hidden windows, tool windows and windows on other virtual desktops are excluded. If a monitor is disconnected or its work area changes, restored windows are fitted onto an available display. Each save replaces the previous layout.

Existing Pomodoro settings are read from `%APPDATA%\WindowsPomodoro\settings.json` until settings are saved in the new `%APPDATA%\WindowsToolbelt` folder. The previous Start with Windows entry is migrated to the new executable when the app starts.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), or install via Visual Studio:

When installing or modifying Visual Studio:

1. Open the **Visual Studio Installer**
2. Click **Modify** on your installation
3. Under the **Workloads** tab, check **.NET desktop development**
4. In the **Installation details** panel on the right, ensure **.NET 10.0 Runtime** is checked
5. Click **Modify** to apply

## Build

```
dotnet build
```

## Run

```
dotnet run
```

## Publish

To produce a single standalone `.exe` with no dependencies:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output will be in `bin/Release/net10.0-windows10.0.17763.0/win-x64/publish/WindowsToolbelt.exe`.

To stop the published app, rebuild and launch it:

```powershell
& .\.codex\skills\run-release\scripts\run-release.ps1
```

## Validate window layouts

```powershell
dotnet run --project tests/WindowLayoutChecks/WindowLayoutChecks.csproj -c Release
```

The checks exercise persistence, matching, normal/minimized/maximized placement and changed monitor work areas using disposable test windows. They never restore your other desktop windows.
