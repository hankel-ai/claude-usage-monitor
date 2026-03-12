using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeUsageMonitor.Models;

namespace ClaudeUsageMonitor.Services;

public class ClaudeApiService : IDisposable
{
    private HttpClient? _httpClient;
    private Timer? _timer;

    public event Action<UsageData>? UsageUpdated;
    public event Action<string>? ErrorOccurred;
    public event Action? AuthExpired;

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
        _timer = new Timer(
            async _ => await PollUsageAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(intervalSeconds));
    }

    public void StopPolling()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public async Task PollUsageAsync()
    {
        try
        {
            var token = AppSettingsService.GetOAuthToken();
            if (string.IsNullOrEmpty(token))
            {
                ErrorOccurred?.Invoke("No Claude Code credentials found");
                return;
            }

            EnsureHttpClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/oauth/usage");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            var response = await _httpClient!.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                AuthExpired?.Invoke();
                ErrorOccurred?.Invoke("Token expired - re-login via Claude Code");
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ErrorOccurred?.Invoke("Usage API rate limited - will retry");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var usage = ParseUsageResponse(json);
            UsageUpdated?.Invoke(usage);
        }
        catch (TaskCanceledException)
        {
            ErrorOccurred?.Invoke("Request timed out");
        }
        catch (HttpRequestException ex)
        {
            ErrorOccurred?.Invoke($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error: {ex.Message}");
        }
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
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
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
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    usage.SevenDayResetsAt = dt.ToUniversalTime();
            }
        }

        return usage;
    }

    public static UsageData GetMockData()
    {
        return new UsageData
        {
            FiveHourUtilization = 42,
            FiveHourResetsAt = DateTime.UtcNow.AddHours(3).AddMinutes(22),
            SevenDayUtilization = 68,
            SevenDayResetsAt = DateTime.UtcNow.AddDays(4).AddHours(7)
        };
    }

    public void Dispose()
    {
        StopPolling();
        _httpClient?.Dispose();
    }
}
