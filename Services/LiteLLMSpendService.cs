using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// Relative-time windows offered by the LiteLLM Usage page. The click cycle on the
/// spend bar advances through these in declaration order.
/// </summary>
public enum SpendWindow
{
    Today,
    Last7Days,
    Last30Days,
    MonthToDate,
    YearToDate
}

public static class SpendWindowExtensions
{
    /// <summary>Inclusive local-date range [start, end] for the given window.</summary>
    public static (DateOnly start, DateOnly end) ComputeRange(this SpendWindow window)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return window switch
        {
            SpendWindow.Today       => (today, today),
            SpendWindow.Last7Days   => (today.AddDays(-6), today),
            SpendWindow.Last30Days  => (today.AddDays(-29), today),
            SpendWindow.MonthToDate => (new DateOnly(today.Year, today.Month, 1), today),
            SpendWindow.YearToDate  => (new DateOnly(today.Year, 1, 1), today),
            _                       => (new DateOnly(today.Year, today.Month, 1), today),
        };
    }

    /// <summary>Short tag shown in the bar label (matches the LiteLLM UI chips).</summary>
    public static string Tag(this SpendWindow window) => window switch
    {
        SpendWindow.Today       => "Today",
        SpendWindow.Last7Days   => "7d",
        SpendWindow.Last30Days  => "30d",
        SpendWindow.MonthToDate => "MTD",
        SpendWindow.YearToDate  => "YTD",
        _                       => "MTD",
    };

    /// <summary>Human label for tooltips.</summary>
    public static string Description(this SpendWindow window) => window switch
    {
        SpendWindow.Today       => "Today",
        SpendWindow.Last7Days   => "Last 7 days",
        SpendWindow.Last30Days  => "Last 30 days",
        SpendWindow.MonthToDate => "Month to date",
        SpendWindow.YearToDate  => "Year to date",
        _                       => "Month to date",
    };

    /// <summary>Next window in the click cycle (wraps).</summary>
    public static SpendWindow Next(this SpendWindow window)
    {
        var values = (SpendWindow[])Enum.GetValues(typeof(SpendWindow));
        var idx = Array.IndexOf(values, window);
        return values[(idx + 1) % values.Length];
    }

    public static SpendWindow Parse(string? name)
    {
        // Accept both enum names and short tags for backward/forward friendliness.
        return name?.Trim().ToUpperInvariant() switch
        {
            "TODAY"                       => SpendWindow.Today,
            "7D" or "LAST7DAYS"           => SpendWindow.Last7Days,
            "30D" or "LAST30DAYS"         => SpendWindow.Last30Days,
            "MTD" or "MONTHTODATE"        => SpendWindow.MonthToDate,
            "YTD" or "YEARTODATE"         => SpendWindow.YearToDate,
            _ => Enum.TryParse<SpendWindow>(name, true, out var w) ? w : SpendWindow.MonthToDate,
        };
    }
}

/// <summary>
/// Polls a LiteLLM proxy's spend for the selected relative-time window and reports the
/// total dollar figure. Lightweight sibling of <see cref="ClaudeApiService"/> — no 429
/// backoff machinery; transient failures are simply logged and retried next tick.
/// </summary>
public class LiteLLMSpendService : SpendServiceBase
{
    // Explicit Eastern zone (the Windows id auto-handles EST/EDT) — the spend report's day
    // boundaries are pinned to Eastern regardless of the machine's local timezone.
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    protected override string BaseUrl => AppSettingsService.Current.LiteLLMBaseUrl;
    protected override string DefaultBaseUrl => "https://litellm.example.com";

    /// <summary>The window that is currently polled. Set before polling / on click-cycle.</summary>
    public SpendWindow CurrentWindow { get; set; } = SpendWindow.MonthToDate;

    /// <summary>Fired with the total spend (USD) for <see cref="CurrentWindow"/>.</summary>
    public event Action<double>? SpendUpdated;

    protected override async Task PollOnceAsync(CancellationToken ct)
    {
        var spend = await FetchSpendAsync(CurrentWindow, ct);
        if (spend.HasValue)
            SpendUpdated?.Invoke(spend.Value);
    }

    /// <summary>
    /// GET /user/daily/activity/aggregated for the window's date range and return
    /// metadata.total_spend (USD). Returns null on any failure (logged / surfaced).
    /// </summary>
    public async Task<double?> FetchSpendAsync(SpendWindow window, CancellationToken ct)
    {
        try
        {
            var key = AppSettingsService.Current.LiteLLMApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                RaiseError("No LiteLLM API key configured");
                return null;
            }

            // Today / 7d: honor Eastern day boundaries exactly by summing granular per-request
            // rows. The daily-activity aggregate below is whole-UTC-day bucketed (server ignores
            // the timezone param), which is fine for the wide windows but wrong for "Today".
            if (window is SpendWindow.Today or SpendWindow.Last7Days)
                return await FetchGranularEasternAsync(window, key, ct);

            var (start, end) = window.ComputeRange();
            var startStr = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var endStr = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var tzOffset = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
            var url = $"/user/daily/activity/aggregated?start_date={startStr}&end_date={endStr}&timezone={tzOffset}";

            var client = EnsureHttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {key}");

            var response = await client.SendAsync(request, ct);
            Log($"INFO  LiteLLM spend {window.Tag()} -> {(int)response.StatusCode} {response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                RaiseError("LiteLLM auth failed (check key)");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var spend = ParseTotalSpend(json);
            if (spend.HasValue)
                Log($"OK    LiteLLM spend {window.Tag()} = ${spend.Value:0.00}");
            else
                RaiseError("LiteLLM: no spend in response");
            return spend;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log($"ERROR LiteLLM network: {ex.Message}");
            RaiseError($"LiteLLM network error");
            if (ex.StatusCode == null)
                RaiseNetworkError();
            return null;
        }
        catch (Exception ex)
        {
            Log($"ERROR LiteLLM: {ex.Message}");
            RaiseError($"LiteLLM error");
            return null;
        }
    }

    /// <summary>
    /// Response shape: { "results": [...], "metadata": { "total_spend": 12.34, ... } }
    /// </summary>
    private static double? ParseTotalSpend(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("total_spend", out var spend) &&
                spend.ValueKind == JsonValueKind.Number)
            {
                return spend.GetDouble();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Today's date in the Eastern zone (EST/EDT handled automatically).</summary>
    private static DateOnly EasternToday()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern).DateTime);

    /// <summary>UTC instant corresponding to Eastern wall-clock midnight of the given date.</summary>
    private static DateTime EasternMidnightUtc(DateOnly d)
        => TimeZoneInfo.ConvertTimeToUtc(
               DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified),
               Eastern);

    /// <summary>
    /// Exact Eastern-binned spend for Today / Last7Days by summing granular per-request rows from
    /// <c>/spend/logs/ui</c>. LiteLLM's daily-activity tables are whole-UTC-day buckets (the server
    /// ignores the timezone param by design), so the only way to honor Eastern day boundaries is to
    /// sum raw rows whose UTC instants fall inside the Eastern window. Empty result is a real $0.00.
    /// </summary>
    private async Task<double?> FetchGranularEasternAsync(SpendWindow window, string key, CancellationToken ct)
    {
        var todayEt = EasternToday();
        var startEt = window == SpendWindow.Today ? todayEt : todayEt.AddDays(-6);

        // /spend/logs/ui filters start_date/end_date on the stored UTC instant, so pass the UTC
        // instants that bound the Eastern window. Upper bound is exclusive Eastern-midnight-tomorrow,
        // capped at now (no future rows, and avoids sending a future end_date).
        var startUtc = EasternMidnightUtc(startEt);
        var endUtc = EasternMidnightUtc(todayEt.AddDays(1));
        if (endUtc > DateTime.UtcNow) endUtc = DateTime.UtcNow;

        var s = Uri.EscapeDataString(startUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var e = Uri.EscapeDataString(endUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        var client = EnsureHttpClient();
        double total = 0;
        int page = 1, totalPages = 1;
        const int pageSize = 100, maxPages = 100;   // /spend/logs/ui caps page_size at 100

        do
        {
            var url = $"/spend/logs/ui?start_date={s}&end_date={e}&page={page}&page_size={pageSize}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {key}");

            var response = await client.SendAsync(request, ct);
            Log($"INFO  LiteLLM spend/logs/ui {window.Tag()} p{page} -> {(int)response.StatusCode} {response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                RaiseError("LiteLLM auth failed (check key)");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            if (!TryParseGranularPage(json, out var pageSum, out totalPages))
            {
                RaiseError("LiteLLM: bad spend/logs response");
                return null;
            }

            total += pageSum;
            page++;
        }
        while (page <= totalPages && page <= maxPages);

        Log($"OK    LiteLLM spend {window.Tag()} (ET granular) = ${total:0.00}");
        return total;
    }

    /// <summary>
    /// /spend/logs/ui page shape: { "data": [ { "spend": 0.01, ... }, ... ], "total_pages": N, ... }.
    /// Sums the numeric spend of each row on the page. Note the envelope's "total" is a row count,
    /// not a spend sum, so it is deliberately not used here.
    /// </summary>
    private static bool TryParseGranularPage(string json, out double sum, out int totalPages)
    {
        sum = 0;
        totalPages = 1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("total_pages", out var tp) && tp.ValueKind == JsonValueKind.Number)
                totalPages = tp.GetInt32();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    if (row.TryGetProperty("spend", out var sp) && sp.ValueKind == JsonValueKind.Number)
                        sum += sp.GetDouble();
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
