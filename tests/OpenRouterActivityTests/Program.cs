using ClaudeUsageMonitor.Services;

// Minimal dependency-free test runner for the OpenRouter activity parser. Returns a non-zero
// exit code on any failure. Run: dotnet run --project tests/OpenRouterActivityTests
int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) { Console.WriteLine($"ok  {msg}"); }
    else { Console.WriteLine($"FAIL {msg}"); failures++; }
}

static bool Near(double a, double b) => Math.Abs(a - b) < 0.000001;

// Missing key reads as 0 rather than throwing, so a regression reports every failure
// instead of dying on the first absent date.
static double At(Dictionary<DateOnly, double> d, int y, int m, int day)
    => d.TryGetValue(new DateOnly(y, m, day), out var v) ? v : 0;

// --- Date parsing -------------------------------------------------------------------
// REGRESSION: the live /activity endpoint returns a midnight *timestamp*, not a bare date.
// DateOnly.TryParse rejects the time component, which silently skipped every row and made
// all four spend windows read $0.00 while the credit balance looked fine.
Check(OpenRouterActivityParser.TryParseActivityDate("2026-08-31 00:00:00", out var ts) &&
      ts == new DateOnly(2026, 8, 31), "timestamp form '2026-08-31 00:00:00' parses");
Check(OpenRouterActivityParser.TryParseActivityDate("2026-08-31", out var bare) &&
      bare == new DateOnly(2026, 8, 31), "bare date form '2026-08-31' parses");
Check(OpenRouterActivityParser.TryParseActivityDate("2026-08-31T00:00:00Z", out var iso) &&
      iso == new DateOnly(2026, 8, 31), "ISO-8601 UTC form parses");
Check(!OpenRouterActivityParser.TryParseActivityDate("not-a-date", out _), "garbage rejected");
Check(!OpenRouterActivityParser.TryParseActivityDate(null, out _), "null rejected");
Check(!OpenRouterActivityParser.TryParseActivityDate("", out _), "empty rejected");

// --- Activity parsing ---------------------------------------------------------------
// Shape copied from a real /activity response.
const string activityJson = """
{"data":[
  {"date":"2026-08-31 00:00:00","model":"deepseek/deepseek-v4-flash","usage":0.080165,
   "byok_usage_inference":0,"requests":38,"prompt_tokens":2618234,"completion_tokens":26632},
  {"date":"2026-08-31 00:00:00","model":"anthropic/claude-opus-5","usage":0.25,
   "byok_usage_inference":9.99,"requests":3,"prompt_tokens":100,"completion_tokens":50},
  {"date":"2026-08-30 00:00:00","model":"deepseek/deepseek-v4-flash","usage":1.00,
   "byok_usage_inference":0,"requests":10,"prompt_tokens":5,"completion_tokens":5},
  {"date":"2026-08-14 00:00:00","model":"deepseek/deepseek-v4-flash","usage":2.00,
   "byok_usage_inference":0,"requests":10,"prompt_tokens":5,"completion_tokens":5},
  {"date":"2026-07-20 00:00:00","model":"deepseek/deepseek-v4-flash","usage":4.00,
   "byok_usage_inference":0,"requests":10,"prompt_tokens":5,"completion_tokens":5}
]}
""";

Check(OpenRouterActivityParser.TryParseActivity(activityJson, out var byDate), "activity parses");
Check(byDate.Count == 4, $"4 distinct dates (got {byDate.Count})");
Check(Near(At(byDate, 2026, 8, 31), 0.330165), "same-date rows are summed across models");
// BYOK is billed by the upstream provider, not out of OpenRouter credits — the 9.99 must not appear.
Check(!Near(At(byDate, 2026, 8, 31), 10.320165), "byok_usage_inference excluded");

Check(!OpenRouterActivityParser.TryParseActivity("{\"nope\":1}", out _), "missing data array -> false");
Check(!OpenRouterActivityParser.TryParseActivity("not json", out _), "malformed json -> false");
Check(OpenRouterActivityParser.TryParseActivity("{\"data\":[]}", out var empty) && empty.Count == 0,
    "empty data array is a valid, empty result");

// Rows missing usage or date are skipped, not fatal.
Check(OpenRouterActivityParser.TryParseActivity(
        "{\"data\":[{\"date\":\"2026-08-31 00:00:00\"},{\"usage\":1.0},{\"date\":\"x\",\"usage\":1.0}]}",
        out var partial) && partial.Count == 0, "incomplete rows skipped without failing");

// --- Stateless window roll-up ---------------------------------------------------------
// Today and MTD come from OpenRouter's own server-side counters; 7d/30d are the completed-day
// buckets plus today's counter. Nothing here depends on the app having been running.
var utcToday = new DateOnly(2026, 8, 31);
var keys = new OpenRouterKeyUsage(Lifetime: 6.9725, Daily: 0.51, Weekly: 0.98, Monthly: 3.51);
var snap = OpenRouterActivityParser.BuildSnapshot(byDate, 15.0, 6.972516462, utcToday, keys);

Check(Near(snap.Today, 0.51), $"Today comes from usage_daily (got {snap.Today})");
Check(Near(snap.MonthToDate, 3.51), "MTD comes from usage_monthly");
Check(Near(snap.Last7Days, 1.0 + 0.51), $"7d = prior buckets ($1.00) + today (got {snap.Last7Days})");
Check(Near(snap.Last30Days, 3.0 + 0.51), $"30d = prior buckets ($3.00) + today (got {snap.Last30Days})");
Check(Near(snap.RemainingCredits, 8.027483538), "RemainingCredits = credits - usage");
// The windows must nest, or the bar contradicts itself as you click through it.
Check(snap.Today <= snap.Last7Days && snap.Last7Days <= snap.Last30Days, "Today <= 7d <= 30d");

// THE STATELESSNESS GUARANTEE: identical inputs must give identical outputs no matter what
// happened before. Two calls with no shared state, as if the app had just started cold.
var coldStart = OpenRouterActivityParser.BuildSnapshot(byDate, 15.0, 6.972516462, utcToday, keys);
Check(coldStart == snap, "a cold start produces the identical snapshot - no hidden local state");

// Today's own bucket must not be double-counted: /activity publishes only COMPLETED UTC days,
// so a row dated today (or later) would overlap usage_daily.
var dupe = new Dictionary<DateOnly, double> { [utcToday] = 9.99, [utcToday.AddDays(-1)] = 1.0 };
var dupeSnap = OpenRouterActivityParser.BuildSnapshot(dupe, 0, 0, utcToday, keys);
Check(Near(dupeSnap.Last7Days, 1.0 + 0.51), "today's bucket excluded from 7d (no double count)");

// Boundary: the 7-day window is inclusive of today-6 and excludes today-7.
var edge = new Dictionary<DateOnly, double>
{
    [utcToday.AddDays(-6)] = 1.0,
    [utcToday.AddDays(-7)] = 1.0,
    [utcToday.AddDays(-29)] = 1.0,
    [utcToday.AddDays(-30)] = 1.0,
};
var zero = new OpenRouterKeyUsage(0, 0, 0, 0);
var edgeSnap = OpenRouterActivityParser.BuildSnapshot(edge, 0, 0, utcToday, zero);
Check(Near(edgeSnap.Last7Days, 1.0), "7d includes today-6 and excludes today-7");
Check(Near(edgeSnap.Last30Days, 3.0), "30d includes today-29 and excludes today-30");

// --- /keys parsing --------------------------------------------------------------------
// Shape copied from a real /keys response. Counters are summed across every key.
const string keysJson = """
{"data":[
 {"name":"openclaw","usage":6.972516462,"usage_daily":0.212013332,
  "usage_weekly":0.520863505,"usage_monthly":0.212013332},
 {"name":"second","usage":1.0,"usage_daily":0.25,"usage_weekly":0.5,"usage_monthly":0.75}
]}
""";
Check(OpenRouterActivityParser.TryParseKeyUsage(keysJson, out var ku), "keys response parses");
Check(Near(ku.Lifetime, 7.972516462), $"lifetime summed across keys (got {ku.Lifetime})");
Check(Near(ku.Daily, 0.462013332), $"usage_daily summed across keys (got {ku.Daily})");
Check(Near(ku.Weekly, 1.020863505), "usage_weekly summed across keys");
Check(Near(ku.Monthly, 0.962013332), "usage_monthly summed across keys");

Check(OpenRouterActivityParser.TryParseKeyUsage("{\"data\":[]}", out var kuEmpty)
      && Near(kuEmpty.Daily, 0), "no keys -> zeros, not a failure");
Check(!OpenRouterActivityParser.TryParseKeyUsage("{\"nope\":1}", out _), "missing data array -> false");
Check(!OpenRouterActivityParser.TryParseKeyUsage("not json", out _), "malformed keys json -> false");
// A key with null/absent counters must not blow up the sum.
Check(OpenRouterActivityParser.TryParseKeyUsage(
        "{\"data\":[{\"usage\":1.0},{\"usage_daily\":null,\"usage_monthly\":2.0}]}", out var kuPartial)
      && Near(kuPartial.Lifetime, 1.0) && Near(kuPartial.Daily, 0) && Near(kuPartial.Monthly, 2.0),
    "absent / null counters count as 0");

// REGRESSION: total_usage - sum(buckets) was once used to infer the current day. It is wrong -
// it also counts usage older than the 30-day activity window. Verified live on this account:
// total_usage $7.4356, sum(buckets) $6.5780 -> $0.8576, but the real usage_daily was $0.6751.
// The $0.18 difference is pre-window spend. The snapshot must never use that subtraction.
var realBuckets = new Dictionary<DateOnly, double> { [new DateOnly(2026, 8, 31)] = 6.5780 };
var realKeys = new OpenRouterKeyUsage(7.4356, 0.6751, 0.9839, 0.6751);
var realSnap = OpenRouterActivityParser.BuildSnapshot(
    realBuckets, 15.0, 7.4356, new DateOnly(2026, 9, 1), realKeys);
Check(Near(realSnap.Today, 0.6751), "Today is usage_daily, NOT total_usage - sum(buckets)");
Check(!Near(realSnap.Today, 0.8576), "the discredited subtraction is not used");
Check(Near(realSnap.MonthToDate, 0.6751), "MTD on the 1st equals usage_daily, never $0.00");

// --- Credits parsing ----------------------------------------------------------------
var (credits, usage) = OpenRouterActivityParser.ParseCredits(
    "{\"data\":{\"total_credits\":15,\"total_usage\":6.972516462}}");
Check(Near(credits, 15.0) && Near(usage, 6.972516462), "credits parse");

var (noCredits, noUsage) = OpenRouterActivityParser.ParseCredits("{\"data\":{}}");
Check(Near(noCredits, 0) && Near(noUsage, 0), "missing credit fields default to 0");

var (badCredits, badUsage) = OpenRouterActivityParser.ParseCredits("garbage");
Check(Near(badCredits, 0) && Near(badUsage, 0), "malformed credits json defaults to 0");

// --- Window cycle -------------------------------------------------------------------
Check(OpenRouterWindow.Today.Next() == OpenRouterWindow.Last7Days, "cycle Today -> 7d");
Check(OpenRouterWindow.MonthToDate.Next() == OpenRouterWindow.Balance, "cycle MTD -> Bal");
Check(OpenRouterWindow.Balance.Next() == OpenRouterWindow.Today, "cycle wraps Bal -> Today");
Check(OpenRouterWindowExtensions.Parse("Bal") == OpenRouterWindow.Balance, "parse tag 'Bal'");
Check(OpenRouterWindowExtensions.Parse("30d") == OpenRouterWindow.Last30Days, "parse tag '30d'");
Check(OpenRouterWindowExtensions.Parse(null) == OpenRouterWindow.MonthToDate, "parse null -> MTD");
Check(OpenRouterWindowExtensions.Parse("nonsense") == OpenRouterWindow.MonthToDate,
    "unknown tag falls back to MTD");
// Round-trip: every stop's tag must parse back to itself, or the saved setting silently resets.
foreach (var w in Enum.GetValues<OpenRouterWindow>())
    Check(OpenRouterWindowExtensions.Parse(w.Tag()) == w, $"tag round-trips for {w}");

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) FAILED.");
return failures == 0 ? 0 : 1;
