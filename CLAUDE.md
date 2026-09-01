# Claude Usage Monitor

## Purpose
A lightweight always-on-top Windows desktop widget that shows Claude usage as visual meters:
5-hour and 7-day rate-limit utilization (polled from `api.anthropic.com/api/oauth/usage`), plus
an optional **LiteLLM Vertex spend** bar (Total Spend from a self-hosted LiteLLM proxy) and an
optional **OpenRouter spend** bar (account-wide spend + credit balance from openrouter.ai).

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
- `Services/SpendServiceBase.cs` — abstract polling scaffolding shared by both spend meters: the
  self-rescheduling timer, `HttpClient` lifecycle, shared log file, and the `SpendError` /
  `NetworkError` events. Subclasses supply `BaseUrl`, `DefaultBaseUrl` and `PollOnceAsync`, and own
  their "updated" event (the payload shapes differ, so it does not live on the base).
- `Services/LiteLLMSpendService.cs` — polls the LiteLLM proxy for `total_spend`; `SpendWindow` enum +
  date-range/tag helpers. Lightweight (no 429 backoff); errors logged + surfaced.
- `Services/OpenRouterSpendService.cs` — polls openrouter.ai `/activity` + `/credits`;
  `OpenRouterWindow` enum and the `OpenRouterSnapshot` record.
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
- **Window height** is sized additively in `UpdateWindowHeight()` (base 68, +12 timers, +21 LiteLLM
  spend, +18 OpenRouter) — don't reintroduce the old hardcoded 80/68. **`BaseContentHeight` (119) and
  the XAML `Window Height` are coupled by the 1.5x hover zoom**: the Window must be taller than
  `BaseContentHeight * 1.5` (119 * 1.5 = 178.5, hence `Height="240"`) or the bottom bar clips while
  zoomed. Adding a fifth bar means bumping both.
- **OpenRouter meter** (`Services/OpenRouterSpendService.cs`):
  - Requires a **management key** (openrouter.ai → Settings → Management API Keys). A normal
    `sk-or-v1-...` inference key gets **403** from both endpoints — that case has its own message
    (`"OpenRouter: management key required"`) because it is the likeliest setup mistake.
  - **`/activity` omits the in-progress UTC day entirely** — "last 30 *completed* UTC days" is
    literal, and `?date=<today>` returns **HTTP 400**. Between 20:00 Eastern and midnight there is
    therefore no bucket anywhere containing tonight's spend.
  - **HARD REQUIREMENT — STATELESS.** Every figure must come from a single poll's responses.
    No persisted baselines, no deltas against remembered values, nothing that assumes the widget
    was running earlier. The user's rule: *"you better not have any dependencies on accurate data
    being based on the app running."* Shut the app down for a week; the numbers must still be
    right on restart. An earlier design tracked `total_usage` deltas against baselines in
    settings.json — it produced an exact *Eastern* day but silently degraded whenever the app was
    off. It was removed. Do not reintroduce it.
  - **`Today` = `/keys` `usage_daily`, `MTD` = `usage_monthly`**, summed across keys. These are
    server-side counters, so they need no local state and survive any downtime.
  - **The cost of statelessness is UTC boundaries.** `usage_daily` rolls at UTC midnight, i.e.
    **20:00 Eastern** — unlike the LiteLLM bar, which is Eastern. `Description()` names the zone
    in the tooltip. An Eastern-accurate day is only obtainable by local delta tracking (breaks
    the rule) or from the private `/api/frontend/v1/private/analytics-query`, which is
    **cookie-authenticated and returns 401 to any API key**.
  - **NEVER use `total_usage - sum(buckets)`.** It looks like the in-progress UTC day and is not:
    it also counts usage older than the 30-day activity window. Verified live — that subtraction
    gave $0.8576 while the true `usage_daily` was $0.6751, a $0.18 gap of pre-window spend. A
    regression test pins this.
  - **7d / 30d are completed-day buckets PLUS `usage_daily`**, so the windows nest. Buckets dated
    today or later are excluded — `/activity` publishes only *completed* UTC days, so such a row
    would double-count against `usage_daily`.
  - **30 days of history max**, so there is no YTD stop. The cycle is Today → 7d → 30d → MTD → Bal.
  - **One poll computes every window** into an `OpenRouterSnapshot`, so clicking the bar is a pure
    re-render with no network call — do not copy the LiteLLM bar's clear-and-refetch click handler.
  - Only `usage` is summed from activity rows; `byok_usage_inference` is excluded on purpose (billed
    by the upstream provider, not out of OpenRouter credits).
  - The `Bal` stop fills with credits **consumed** so "more fill = more spent" stays consistent with
    every other bar. `total_credits <= 0` (pay-as-you-go) falls back to showing lifetime usage flat.
  - Requests use **absolute** URLs: `SpendServiceBase.EnsureHttpClient` trims the trailing slash off
    the base address, which would make a relative `credits` resolve against `/api/` and lose `/v1`.
- All three API services log to the shared `%APPDATA%\ClaudeUsageMonitor\log.txt` (auto-truncated at 1 MB).
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
