# Claude Usage Monitor

A lightweight Windows desktop widget that displays your Claude AI usage as visual meters. Shows both your 5-hour rolling window and 7-day utilization at a glance, so you always know how close you are to rate limits.

![WPF](https://img.shields.io/badge/WPF-.NET%208-blue) ![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)

## Features

- **Two usage meters** — 5-hour (green) and 7-day (blue) utilization bars
- **LiteLLM Vertex spend meter** — optional third bar showing Total Spend from a LiteLLM proxy against a monthly budget; click to cycle the time window (Today / 7d / 30d / MTD / YTD)
- **OpenRouter spend meter** — optional fourth bar showing account-wide OpenRouter spend against a monthly budget; click to cycle Today / 7d / 30d / MTD / **Bal** (remaining credit balance)
- **Delta highlights** — usage increase since last poll shown as a brighter strip at the end of each bar
- **Reset timer bars** — optional thin red bars showing time elapsed in each rate limit window
- **Color thresholds** — bars turn orange at 70% and red at 90%
- **Always-on-top** — stays visible over other windows (toggleable)
- **Hover tooltips** — shows exact percentage, time until reset, and change since last poll
- **Draggable** — click and drag anywhere; position is saved across restarts
- **Configurable polling** — 30 seconds to 30 minutes (default: 2 minutes)
- **Launch on startup** — optional Windows startup registration (no admin required)
- **Pause polling** — temporarily stop API calls; widget dims to indicate paused state
- **In-bar text** — shows percentage inside each bar; switches to countdown at 100%
- **Animated transitions** — smooth bar fill animations on each update
- **Crash logging** — unhandled errors are written to `crash.log` with a dialog
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

## LiteLLM Vertex Spend Meter

An optional third bar shows **Total Spend** from a self-hosted [LiteLLM](https://litellm.ai) proxy
(e.g. `https://litellm.example.com`) so you can watch proxy costs alongside your Claude usage.

- **On by default** once a key is configured; toggle it via right-click → **Show LiteLLM Spend**.
- The bar fills toward a **monthly budget** (default `$500`) and turns orange at 70% / red at 90%,
  matching the usage meters. The bar text shows the window tag and the real dollar figure, e.g. `MTD  $123.45`.
- **Click the bar** to cycle the relative window: **Today → 7d → 30d → MTD → YTD**. The selection persists.

### Configuration

Right-click → **Settings** → **LiteLLM Vertex Spend**:

- **API key** — a LiteLLM admin/master key (`sk-...`) with permission to read spend. Stored in `settings.json`.
- **Base URL** — the proxy URL (e.g. `https://litellm.example.com`).
- **Monthly budget (USD)** — the ceiling the bar fills toward (default `500`).

### API endpoint

The spend meter polls (Bearer-authenticated with the configured key):

```
GET <base-url>/user/daily/activity/aggregated?start_date=YYYY-MM-DD&end_date=YYYY-MM-DD
Authorization: Bearer <litellm-key>
```

and reads `metadata.total_spend` from the response — the same figure shown on the LiteLLM Usage page.
Date ranges are computed in local time from the selected window.

## OpenRouter Spend Meter

An optional fourth bar shows **account-wide OpenRouter spend** and your remaining credit balance.

- **On by default** once a key is configured; toggle via right-click → **Show OpenRouter Spend**.
- **Click the bar** to cycle **Today → 7d → 30d → MTD → Bal**. The four window stops fill toward a
  monthly budget (default `$50`, orange at 70% / red at 90%). The **Bal** stop switches the bar to a
  credit meter: it fills with credits *consumed*, and the text reads e.g. `Bal  $37.60 left`.
- Cycling is instant — one poll caches every window, so clicking never triggers a request.
- The hover tooltip always shows the other half: spend stops include the credit line, and **Bal**
  includes month-to-date spend.

### Configuration

Right-click → **Settings** → **OpenRouter Spend**:

- **Management API key** — **must** be a management key from
  openrouter.ai → Settings → Management API Keys. A normal `sk-or-v1-...` inference key returns
  **403** and the bar tooltip will say `OpenRouter: management key required`. Stored in `settings.json`.
- **Monthly budget (USD)** — the ceiling the four window stops fill toward (default `50`).
- **Polling interval** — default 5 minutes, independent of both the Claude and LiteLLM pollers.

There is no base-URL setting; openrouter.ai is a hosted service and the API root is a constant.

### API endpoints

Two Bearer-authenticated calls per poll:

```
GET https://openrouter.ai/api/v1/activity     -> data[] rows: { date, model, usage, requests, ... }
GET https://openrouter.ai/api/v1/credits      -> data: { total_credits, total_usage }
```

Only `usage` is summed from the activity rows. `byok_usage_inference` is deliberately excluded —
BYOK traffic is billed by the upstream provider, not out of OpenRouter credits, so counting it
would inflate spend against an OpenRouter budget.

### Timezone and history limits

Two constraints come straight from the API and cannot be worked around client-side:

- **Every figure is stateless.** All of them are counters OpenRouter maintains server-side, read
  fresh on each poll. The widget stores no spend data of its own, so you can shut it down for a
  week and the numbers are still correct when it comes back — nothing degrades with downtime.
- **`Today` and `MTD` use UTC boundaries.** They come from `usage_daily` / `usage_monthly`, which
  reset at UTC midnight — **8pm Eastern**, not local midnight. This differs from the LiteLLM bar,
  which uses Eastern days. The tooltip names the zone.
  This is a deliberate trade: an Eastern-accurate day would require the widget to track spend
  deltas continuously, which makes the number wrong whenever the app isn't running. Statelessness
  was judged more important. (The per-request granularity behind openrouter.ai's own charts comes
  from a private, cookie-authenticated endpoint that rejects API keys, so it isn't an option.)
- **`7d` / `30d` are the completed daily buckets plus today's counter**, so the windows nest
  properly (`Today` ≤ `7d` ≤ `30d`).
- **History is capped at 30 days**, which is why there is no YTD stop.

### App settings location

The app stores its own settings (polling interval, window position, always-on-top preference,
LiteLLM key/URL/budget/window, OpenRouter key/budget/window) at:

```
%APPDATA%\ClaudeUsageMonitor\settings.json
```

### Crash log

If the app encounters an unhandled error, it shows a dialog and writes details to:

```
%APPDATA%\ClaudeUsageMonitor\crash.log
```

Check this file if the app fails to start or closes unexpectedly.

### Log file

The app writes a timestamped log of every API poll to:

```
%APPDATA%\ClaudeUsageMonitor\log.txt
```

Which resolves to (example):

```
C:\Users\YourUsername\AppData\Roaming\ClaudeUsageMonitor\log.txt
```

Each line includes the timestamp, log level, and event:

```
2026-03-12 14:30:02  INFO  Polling usage API...
2026-03-12 14:30:03  OK    5h=6.0%  7d=35.0%
2026-03-12 14:32:02  INFO  Polling usage API...
2026-03-12 14:32:02  WARN  Rate limited (429) — keeping last known data
```

The log file is automatically truncated when it exceeds 1 MB. You can open it with any text editor or tail it in PowerShell:

```cmd
Get-Content "%APPDATA%\ClaudeUsageMonitor\log.txt" -Tail 20 -Wait
```

### API rate limiting

The Anthropic usage endpoint (`/api/oauth/usage`) enforces its own rate limits, separate from the model API. If you poll too frequently, you'll receive HTTP 429 responses. When this happens:

- The app **keeps displaying the last successfully fetched data** — bars won't go blank
- The log file will show `WARN  Rate limited (429)` entries
- Polling continues at the configured interval; the next successful response updates the bars normally

To avoid rate limiting, increase the polling interval in **Settings** (default is 2 minutes, which is well within limits under normal use). If you're seeing persistent 429s, try 5 or 10 minutes.

## Building from source

```cmd
git clone https://github.com/hankel-ai/claude-usage-monitor.git
cd ClaudeUsageMonitor
dotnet build
```

> **Note:** If `dotnet` is not recognized or says "No .NET SDKs were found", your system may have a runtime-only `dotnet.exe` on the PATH shadowing the SDK. Use the included `build.cmd` wrapper instead, which points directly to the SDK:
>
> ```cmd
> .\build.cmd build
> ```

## Running

### Development (from source)

```cmd
dotnet run
```

Or via the wrapper:

```cmd
.\build.cmd run
```

### Publishing a single-file executable

```cmd
dotnet publish -c Release -o bin\Publish
```

Or via the wrapper:

```cmd
.\build.cmd publish -c Release -o bin\Publish
```

The output executable is:

```
bin\Publish\ClaudeUsageMonitor.exe
```

This is a **self-contained single `.exe`** (~155 MB) — no .NET runtime installation needed on the target machine. Copy this one file to any Windows 10/11 x64 machine and run it directly.

#### Slim (framework-dependent) build

If you already have the .NET 8 Desktop Runtime installed, you can produce a much smaller exe:

```cmd
dotnet publish -c Release --self-contained false -o bin\Publish
```

This produces a **~192 KB** exe that requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the target machine.

### Launch on Windows startup

In the app, right-click the widget and go to **Settings...** then check **Launch on Windows startup**. This adds a registry entry at `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (no admin required). This only works when running the published `ClaudeUsageMonitor.exe`, not via `dotnet run`.

## Usage

The widget appears as a small floating panel near the bottom-right of your screen.

### Right-click context menu

| Option | Description |
|--------|-------------|
| **Always on Top** | Toggle whether the widget stays above other windows |
| **Show Reset Timers** | Toggle the thin red elapsed-time bars under each meter |
| **Show LiteLLM Spend** | Toggle the LiteLLM spend bar (only appears when a key is configured) |
| **Show OpenRouter Spend** | Toggle the OpenRouter bar (only appears when a management key is configured) |
| **Pause Polling** | Stop API polling. The widget dims with a semi-transparent overlay to indicate it is paused. Unchecking resumes polling. |
| **Pause LiteLLM Spend** | Stop LiteLLM spend polling only; the Claude meters keep updating |
| **Pause OpenRouter Spend** | Stop OpenRouter polling only; the other meters keep updating |
| **Settings...** | Open settings (polling interval, credentials status, startup toggle) |
| **Refresh Now** | Immediately re-fetch usage data (disabled while paused) |
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
| App doesn't start / hourglass then nothing | Check `%APPDATA%\ClaudeUsageMonitor\crash.log` for error details. If no crash log exists, the app may have started but the widget is hard to see — look near the bottom-right of your screen. |
| Widget shows "No credentials found" | Run `claude` in a terminal and log in. The app reads the token from `%USERPROFILE%\.claude\.credentials.json`. |
| Bars stay empty | Hover to check tooltip. If it says "No Claude Code credentials found", see above. |
| "Token expired" tooltip | Re-authenticate in Claude Code (`claude` in terminal). The app re-reads the token on each poll. |
| Bars not updating / 429 in log | The Anthropic usage endpoint is rate limiting you. The app keeps showing the last known data. Increase your polling interval in Settings (try 5-10 min). Check `%APPDATA%\ClaudeUsageMonitor\log.txt` for details. |
| Widget not visible | It defaults to bottom-right of the screen. Check near the taskbar. If lost, delete `%APPDATA%\ClaudeUsageMonitor\settings.json` to reset position. |
| Copied exe doesn't work on another PC | Make sure you copied the **155 MB** exe from `bin\Publish\`, not a smaller framework-dependent build. The standalone exe includes the .NET runtime. |
| "Launch on startup" not working | This only works with the published `.exe`. Run `dotnet publish -c Release -o bin\Publish` first, then use `ClaudeUsageMonitor.exe` from the `bin\Publish` folder. |

## License

MIT
