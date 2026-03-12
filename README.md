# Claude Usage Monitor

A lightweight Windows desktop widget that displays your Claude AI usage as visual meters. Shows both your 5-hour rolling window and 7-day utilization at a glance, so you always know how close you are to rate limits.

![WPF](https://img.shields.io/badge/WPF-.NET%208-blue) ![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

## Features

- **Two usage meters** — 5-hour (green) and 7-day (blue) utilization bars
- **Delta highlights** — usage increase since last poll shown as a brighter strip at the end of each bar
- **Reset timer bars** — optional thin red bars showing time elapsed in each rate limit window
- **Color thresholds** — bars turn orange at 70% and red at 90%
- **Always-on-top** — stays visible over other windows (toggleable)
- **Hover tooltips** — shows exact percentage, time until reset, and change since last poll
- **Draggable** — click and drag anywhere; position is saved across restarts
- **Configurable polling** — 30 seconds to 30 minutes (default: 2 minutes)
- **Launch on startup** — optional Windows startup registration (no admin required)
- **Animated transitions** — smooth bar fill animations on each update
- **Tiny footprint** — ~25-50 MB RAM, single process, no browser engine

## Requirements

- **Windows 10 or 11** (x64)
- **.NET 8 SDK** — for building from source ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Claude Code** — must be installed and authenticated (the app reads its OAuth token)

## Authentication / Token Dependency

This app does **not** manage its own login. It reads the OAuth access token that Claude Code stores after you authenticate.

### How it works

1. When you run `claude` in a terminal and log in, Claude Code saves your OAuth token to a credentials file
2. This app reads that token and uses it to call the Anthropic usage API
3. If the token expires, re-authenticate in Claude Code — the app picks up the new token automatically

### Credentials file location

The app reads the token from:

```
%USERPROFILE%\.claude\.credentials.json
```

Which resolves to (example):

```
C:\Users\YourUsername\.claude\.credentials.json
```

This path is **not hardcoded** — it uses the `USERPROFILE` environment variable, so it works for any Windows user account.

The file is created by Claude Code and has this structure:

```json
{
  "claudeAiOauth": {
    "accessToken": "sk-ant-oat01-..."
  }
}
```

**If you don't have Claude Code installed**, install it and run `claude` once in a terminal to authenticate. The credentials file will be created automatically.

### API endpoint

The app polls:

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <token>
anthropic-beta: oauth-2025-04-20
```

Response format:

```json
{
  "five_hour": { "utilization": 42.0, "resets_at": "2026-03-12T15:00:00Z" },
  "seven_day": { "utilization": 68.0, "resets_at": "2026-03-18T03:00:00Z" }
}
```

### App settings location

The app stores its own settings (polling interval, window position, always-on-top preference) at:

```
%APPDATA%\ClaudeUsageMonitor\settings.json
```

## Building from source

```cmd
git clone https://github.com/hankel-ai/claude-usage-monitor.git
cd ClaudeUsageMonitor
dotnet build
```

## Running

### Development (from source)

```cmd
dotnet run
```

### Publishing a single-file executable

```cmd
dotnet publish -c Release
```

The output executable is:

```
bin\Release\net8.0-windows\win-x64\publish\ClaudeUsageMonitor.exe
```

This is a **self-contained single `.exe`** (~90 MB) — no .NET runtime installation needed on the target machine. Copy it anywhere and run it directly.

### Launch on Windows startup

In the app, right-click the widget and go to **Settings...** then check **Launch on Windows startup**. This adds a registry entry at `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (no admin required). This only works when running the published `ClaudeUsageMonitor.exe`, not via `dotnet run`.

## Usage

The widget appears as a small floating panel near the bottom-right of your screen.

### Right-click context menu

| Option | Description |
|--------|-------------|
| **Always on Top** | Toggle whether the widget stays above other windows |
| **Use Mock Data** | Display sample data to test the UI without API calls |
| **Show Reset Timers** | Toggle the thin red elapsed-time bars under each meter |
| **Settings...** | Open settings (polling interval, credentials status, startup toggle) |
| **Refresh Now** | Immediately re-fetch usage data |
| **Exit** | Close the app |

### Meter colors

| Utilization | Color |
|------------|-------|
| 0-69% | Green (5-hour) / Blue (7-day) |
| 70-89% | Orange |
| 90-100% | Red |

The brighter strip at the end of each bar indicates the usage increase since the last poll.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Bars stay empty | Hover to check tooltip — likely "No Claude Code credentials found". Run `claude` in a terminal and log in. |
| "Token expired" tooltip | Re-authenticate in Claude Code (`claude` in terminal). The app re-reads the token on each poll. |
| "Usage API rate limited" tooltip | The Anthropic usage endpoint itself has rate limits. Increase your polling interval in Settings. |
| Widget not visible | It defaults to bottom-right of the screen. Check near the taskbar. If lost, delete `%APPDATA%\ClaudeUsageMonitor\settings.json` to reset position. |
| "Launch on startup" not working | This only works with the published `.exe`. Run `dotnet publish -c Release` first, then use `ClaudeUsageMonitor.exe` from the publish folder. |

## License

MIT
