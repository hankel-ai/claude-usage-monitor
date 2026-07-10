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
public class LiteLLMSpendService : IDisposable
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageMonitor", "log.txt");

    private HttpClient? _httpClient;
    private string? _httpClientBaseUrl;
    private Timer? _timer;
    private volatile bool _stopped = true;
    private CancellationTokenSource? _cts;
    private int _pollIntervalSeconds = 120;
    private int _pollInProgress;

    /// <summary>The window that is currently polled. Set before polling / on click-cycle.</summary>
    public SpendWindow CurrentWindow { get; set; } = SpendWindow.MonthToDate;

    /// <summary>Fired with the total spend (USD) for <see cref="CurrentWindow"/>.</summary>
    public event Action<double>? SpendUpdated;
    public event Action<string>? SpendError;

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    private HttpClient EnsureHttpClient()
    {
        var baseUrl = AppSettingsService.Current.LiteLLMBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://litellm.example.com";

        // Rebuild the client if the configured base URL changed.
        if (_httpClient != null && _httpClientBaseUrl != baseUrl)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        if (_httpClient == null)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClientBaseUrl = baseUrl;
        }

        return _httpClient;
    }

    public void StartPolling(int intervalSeconds)
    {
        StopPolling();
        _pollIntervalSeconds = Math.Max(60, intervalSeconds);
        _stopped = false;
        _cts = new CancellationTokenSource();
        _timer = new Timer(async _ => await PollAndRescheduleAsync(), null,
            TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void StopPolling()
    {
        _stopped = true;
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Fetch immediately for the current window (e.g. after a click-cycle).</summary>
    public void RefreshNow()
    {
        if (_stopped) return;
        _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private async Task PollAndRescheduleAsync()
    {
        if (_stopped) return;
        if (Interlocked.Exchange(ref _pollInProgress, 1) == 1)
            return;
        try
        {
            var spend = await FetchSpendAsync(CurrentWindow, _cts?.Token ?? CancellationToken.None);
            if (spend.HasValue)
                SpendUpdated?.Invoke(spend.Value);
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
            if (!_stopped)
                _timer?.Change(TimeSpan.FromSeconds(_pollIntervalSeconds), Timeout.InfiniteTimeSpan);
        }
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
                SpendError?.Invoke("No LiteLLM API key configured");
                return null;
            }

            var (start, end) = window.ComputeRange();
            var startStr = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var endStr = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"/user/daily/activity/aggregated?start_date={startStr}&end_date={endStr}";

            var client = EnsureHttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {key}");

            var response = await client.SendAsync(request, ct);
            Log($"INFO  LiteLLM spend {window.Tag()} -> {(int)response.StatusCode} {response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                SpendError?.Invoke("LiteLLM auth failed (check key)");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var spend = ParseTotalSpend(json);
            if (spend.HasValue)
                Log($"OK    LiteLLM spend {window.Tag()} = ${spend.Value:0.00}");
            else
                SpendError?.Invoke("LiteLLM: no spend in response");
            return spend;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log($"ERROR LiteLLM network: {ex.Message}");
            SpendError?.Invoke($"LiteLLM network error");
            return null;
        }
        catch (Exception ex)
        {
            Log($"ERROR LiteLLM: {ex.Message}");
            SpendError?.Invoke($"LiteLLM error");
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

    public void Dispose()
    {
        StopPolling();
        _httpClient?.Dispose();
        _cts?.Dispose();
        _cts = null;
    }
}
