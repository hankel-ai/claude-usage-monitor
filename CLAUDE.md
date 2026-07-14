# Claude Usage Monitor

## Purpose
A lightweight always-on-top Windows desktop widget that shows Claude usage as visual meters:
5-hour and 7-day rate-limit utilization (polled from `api.anthropic.com/api/oauth/usage`), plus
an optional **LiteLLM Vertex spend** bar (Total Spend from a self-hosted LiteLLM proxy).

## Tech stack
- **WPF on .NET 8** (`net8.0-windows`), C#, nullable enabled. Also uses WinForms (`NotifyIcon` tray).
- **No external NuGet packages** — everything is BCL (`System.Net.Http`, `System.Text.Json`).
- Published as a **single-file, self-contained** `win-x64` exe (`PublishSingleFile`, `SelfContained`).

## Build / run / deploy
- **Build (verify compile):** `build.cmd build ClaudeUsageMonitor.csproj -c Release`
  (`build.cmd` just shims to `%LOCALAPPDATA%\dotnet\dotnet.exe` since the SDK isn't on PATH).
- **Build + deploy + relaunch:** `buildandcopy.cmd` — publishes the single-file exe, kills the
  running `ClaudeUsageMonitor.exe`, copies it to `C:\Users\admin\OneDrive\Programs\ClaudeUsageMonitor.exe`,
  and restarts it. **Edit the copies in this repo, never the one in `OneDrive\Programs`.**
- **Tests:** standalone console project at `tests/BarColorMapTests` (links production sources directly;
  excluded from the app build via `<Compile Remove="tests\**\*.cs" />`).

## Structure
- `MainWindow.xaml(.cs)` — the widget UI, all bars, tray icon, drag/zoom, context menu, polling wiring.
- `SettingsWindow.xaml(.cs)` — settings dialog (polling interval, hover delay, startup, LiteLLM fields).
- `Services/ClaudeApiService.cs` — polls the Anthropic usage API; 401→AuthExpired, 429→backoff events.
- `Services/LiteLLMSpendService.cs` — polls the LiteLLM proxy for `total_spend`; `SpendWindow` enum +
  date-range/tag helpers. Lightweight (no 429 backoff); errors logged + surfaced.
- `Services/AppSettingsService.cs` — `settings.json` load/save in `%APPDATA%\ClaudeUsageMonitor`,
  reads the Claude OAuth token from `~/.claude/.credentials.json`, HKCU Run key for startup.
- `Services/BarColorMap.cs` — reset-timer color ramp. `Models/UsageData.cs` — usage DTO + tooltips.

## Conventions / gotchas
- **Meters are percentages (0–100)**; the spend meter is a dollar figure rendered as a bar against a
  configurable **monthly budget** (default $500). Color thresholds are shared: orange ≥70%, red ≥90%.
- **Spend section** is on by default when a LiteLLM key exists; toggled via `Show LiteLLM Spend` menu.
  Has its own polling interval (`LiteLLMPollingIntervalSeconds`, default 120) and pause toggle
  (`Pause LiteLLM Spend` in the context menu), both independent of the Claude API polling.
  Clicking the spend bar cycles the window `Today → 7d → 30d → MTD → YTD` (`e.Handled = true` so the
  click doesn't reach the window drag/navigate handler). Selection + visibility persist in settings.
- **Window height** is sized additively in `UpdateWindowHeight()` (base 68, +12 timers, +21 spend) —
  don't reintroduce the old hardcoded 80/68.
- Both API services log to the shared `%APPDATA%\ClaudeUsageMonitor\log.txt` (auto-truncated at 1 MB).
- The LiteLLM key is stored in plaintext in `settings.json` (home-lab, single-user). Read via
  `PasswordBox.Password` in the settings dialog (WPF `PasswordBox` can't be data-bound).
- **Spend / timezone (hybrid, Eastern day boundaries):** LiteLLM's daily-activity tables are
  whole-**UTC**-day buckets; the `timezone` param on `/user/daily/activity/aggregated` is **ignored
  server-side** (verified in v1.91.2 `common_daily_activity._adjust_dates_for_timezone`, a documented
  pass-through), and the container `TZ` can't move it either (`startTime` is forced to UTC before
  bucketing). So `LiteLLMSpendService`:
  - **Today / 7d** → sum granular per-request rows from `GET /spend/logs/ui?start_date&end_date&page&page_size`
    whose UTC instants fall in the **Eastern** window (`page_size` **caps at 100** → paginate on
    `total_pages`; envelope `total` is a row count, not spend — sum `data[].spend`). Exact Eastern.
  - **30d / MTD / YTD** → keep `GET /user/daily/activity/aggregated` → `metadata.total_spend` (UTC
    buckets; a few boundary hours are negligible on a monthly/yearly total, and it avoids paginating
    thousands of fat rows each poll).
  - Eastern is pinned via `TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")` (auto EST/EDT),
    not the machine's local zone.
