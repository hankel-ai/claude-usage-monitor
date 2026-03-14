using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClaudeUsageMonitor.Models;

namespace ClaudeUsageMonitor.Services;

public class ClaudeApiService : IDisposable
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageMonitor", "log.txt");

    private HttpClient? _httpClient;
    private Timer? _timer;
    private volatile bool _stopped = true;  // guards against stale _timer reads across threads
    private UsageData? _lastSuccessfulUsage;
    private readonly Random _random = new();
    private int _pollIntervalSeconds = 120;
    private int _consecutiveThrottleCount;
    private DateTime _backoffUntilUtc = DateTime.MinValue;
    private int _pollInProgress;

    /// <summary>The token value that most recently resulted in a 401/403 response.</summary>
    public string? LastFailedToken { get; private set; }

    public event Action<UsageData>? UsageUpdated;
    public event Action<string>? ErrorOccurred;
    public event Action? AuthExpired;
    /// <summary>Fired on 429 with the UTC time when the backoff window ends.</summary>
    public event Action<DateTime>? RateLimited;

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Keep log file under 1 MB — truncate when exceeded
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.WriteAllText(LogPath, "");

            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void EnsureHttpClient()
    {
        if (_httpClient != null) return;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void StartPolling(int intervalSeconds)
    {
        StopPolling();

        _pollIntervalSeconds = Math.Max(30, intervalSeconds);
        _consecutiveThrottleCount = 0;
        _backoffUntilUtc = DateTime.MinValue;
        _stopped = false;   // must be cleared BEFORE creating the timer

        _timer = new Timer(
            async _ => await PollAndRescheduleAsync(),
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
    }

    public void StopPolling()
    {
        _stopped = true;    // volatile write — visible to all threads immediately
        _timer?.Dispose();
        _timer = null;
    }

    private async Task PollAndRescheduleAsync()
    {
        if (_stopped) return;

        // Ensure one in-flight poll at a time even if previous request is slow.
        if (Interlocked.Exchange(ref _pollInProgress, 1) == 1)
            return;

        try
        {
            await PollUsageAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
            RescheduleNextPoll();
        }
    }

    private void RescheduleNextPoll()
    {
        if (_stopped) return;

        var timer = _timer;
        if (timer == null) return;

        var nextDelay = TimeSpan.FromSeconds(_pollIntervalSeconds);
        var now = DateTime.UtcNow;

        if (_backoffUntilUtc > now)
        {
            var backoffDelay = _backoffUntilUtc - now;
            if (backoffDelay > nextDelay)
                nextDelay = backoffDelay;
        }

        timer.Change(nextDelay, Timeout.InfiniteTimeSpan);
    }

    public async Task PollUsageAsync()
    {
        try
        {
            var token = AppSettingsService.GetOAuthToken();
            if (string.IsNullOrEmpty(token))
            {
                Log("WARN  No credentials found");
                ErrorOccurred?.Invoke("No Claude Code credentials found");
                return;
            }

            EnsureHttpClient();

            Log("INFO  Polling usage API...");
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/oauth/usage");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            var response = await _httpClient!.SendAsync(request);
            Log($"INFO  Response: {(int)response.StatusCode} {response.StatusCode}");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                LastFailedToken = token;
                StopPolling();
                Log("WARN  Auth expired or forbidden — stopped");
                AuthExpired?.Invoke();
                ErrorOccurred?.Invoke("Token expired - waiting for refresh...");
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ApplyThrottleBackoff(response);
                StopPolling();
                Log("WARN  Rate limited (429) — stopped");
                RateLimited?.Invoke(_backoffUntilUtc);
                return;
            }

            response.EnsureSuccessStatusCode();

            _consecutiveThrottleCount = 0;
            _backoffUntilUtc = DateTime.MinValue;

            var json = await response.Content.ReadAsStringAsync();
            var usage = ParseUsageResponse(json);
            _lastSuccessfulUsage = usage;
            Log($"OK    5h={usage.FiveHourUtilization:F1}%  7d={usage.SevenDayUtilization:F1}%");
            UsageUpdated?.Invoke(usage);
        }
        catch (TaskCanceledException)
        {
            ApplyTransientErrorBackoff();
            Log("ERROR Request timed out");
            ErrorOccurred?.Invoke("Request timed out");
        }
        catch (HttpRequestException ex)
        {
            ApplyTransientErrorBackoff();
            Log($"ERROR Network: {ex.Message}");
            ErrorOccurred?.Invoke($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"ERROR {ex.Message}");
            ErrorOccurred?.Invoke($"Error: {ex.Message}");
        }
    }

    private void ApplyTransientErrorBackoff()
    {
        // Short backoff on transient failures to avoid synchronized retries.
        var jitterSeconds = _random.Next(5, 16);
        var transientDelay = TimeSpan.FromSeconds(Math.Min(90, _pollIntervalSeconds + jitterSeconds));
        var candidate = DateTime.UtcNow.Add(transientDelay);

        if (candidate > _backoffUntilUtc)
            _backoffUntilUtc = candidate;
    }

    private void ApplyThrottleBackoff(HttpResponseMessage response)
    {
        _consecutiveThrottleCount++;

        var retryAfter = response.Headers.RetryAfter;
        TimeSpan delay;

        if (retryAfter?.Delta != null)
        {
            delay = retryAfter.Delta.Value;
        }
        else if (retryAfter?.Date != null)
        {
            delay = retryAfter.Date.Value.UtcDateTime - DateTime.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
        }
        else
        {
            // Exponential fallback capped at 15 minutes plus a little jitter.
            var exponent = Math.Min(_consecutiveThrottleCount, 5);
            var baseSeconds = _pollIntervalSeconds * Math.Pow(2, exponent);
            var cappedSeconds = Math.Min(baseSeconds, 900);
            var jitterSeconds = _random.Next(3, 16);
            delay = TimeSpan.FromSeconds(cappedSeconds + jitterSeconds);
        }

        _backoffUntilUtc = DateTime.UtcNow.Add(delay);
        Log($"WARN  Backing off for {Math.Max(1, (int)delay.TotalSeconds)}s after 429");
    }

    private static UsageData ParseUsageResponse(string json)
    {
        var usage = new UsageData();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Expected format:
        // { "five_hour": { "utilization": 6.0, "resets_at": "..." },
        //   "seven_day": { "utilization": 35.0, "resets_at": "..." } }

        if (root.TryGetProperty("five_hour", out var fiveHour))
        {
            if (fiveHour.TryGetProperty("utilization", out var util))
                usage.FiveHourUtilization = util.GetDouble();
            if (fiveHour.TryGetProperty("resets_at", out var resets))
            {
                if (DateTime.TryParse(resets.GetString(), null,
                    DateTimeStyles.RoundtripKind, out var dt))
                    usage.FiveHourResetsAt = dt.ToUniversalTime();
            }
        }

        if (root.TryGetProperty("seven_day", out var sevenDay))
        {
            if (sevenDay.TryGetProperty("utilization", out var util))
                usage.SevenDayUtilization = util.GetDouble();
            if (sevenDay.TryGetProperty("resets_at", out var resets))
            {
                if (DateTime.TryParse(resets.GetString(), null,
                    DateTimeStyles.RoundtripKind, out var dt))
                    usage.SevenDayResetsAt = dt.ToUniversalTime();
            }
        }

        return usage;
    }

    public void Dispose()
    {
        StopPolling();
        _httpClient?.Dispose();
    }
}
