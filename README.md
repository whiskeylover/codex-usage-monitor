# Codex Usage Monitor

A lightweight Windows tray app for tracking your Codex five-hour and weekly allowances.

**v0.3.1 · Windows x64 · .NET 8 Desktop · [MIT](LICENSE)**

Hover over or click the teal ring icon in the notification area to open usage. 

![Usage card opened from the notification-area icon](docs/screenshots/usage-panel.png)

Right-click the same icon and choose **About**.

![About card above the expanded notification area](docs/screenshots/about-panel.png)

## Usage

- **Hover** to preview allowances, reset countdowns, and plan details without taking focus.
- **Left-click** to focus the same card and use Refresh. Click away or press Escape to dismiss.
- **Right-click** for refresh, diagnostics, About, and Exit.

Usage refreshes every two minutes. The app does not add itself to Windows startup.

## Setup

Requires Windows 10/11 x64, the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and the official [Codex CLI](https://github.com/openai/codex) on your PATH. Sign in using `codex login`.

Build from source using the steps below, then run `publish\codex-usage-monitor.exe`.

To update, exit the running app before replacing its files. To uninstall, exit and delete the app folder.

## Build

Install the .NET 8 SDK, then run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\publish\codex-usage-monitor.exe
```

The app is unsigned, so Windows SmartScreen may show “Windows protected your PC.” If you trust the executable, choose **More info → Run anyway**. On managed PCs, this option may be unavailable. The build command above bypasses PowerShell’s script execution policy for that process only; it does not bypass SmartScreen.

## Privacy

- **No telemetry or analytics.** No usage data is sent to a developer-operated server.
- **Your credentials stay with Codex CLI.** The app does not read, copy, or store your authentication files or tokens.
- **Local integration.** Usage is requested through the official local `codex app-server`; Codex CLI may contact OpenAI to retrieve it.
- **Local diagnostics only.** Logs stay on your PC unless you choose to share them.

## Troubleshooting

If usage is unavailable, check `codex --version` and `codex login`, then try **Refresh now** and **Open diagnostics**. Logs are stored at `%LOCALAPPDATA%\codex-usage-monitor\diagnostics.log`; review them before sharing.

Unofficial community project; not affiliated with OpenAI. Issues and pull requests are welcome.
