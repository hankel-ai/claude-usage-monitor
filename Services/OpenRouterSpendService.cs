using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// Polls OpenRouter for account-wide spend and credit balance.
///
/// Requires a **management key** (openrouter.ai → Settings → Management API Keys); a normal
/// inference key gets 403 from every endpoint. Three calls per poll:
///   GET /keys      -> per-key usage / usage_daily / usage_weekly / usage_monthly (live, UTC)
///   GET /activity  -> per day/model rows, whole-UTC-day buckets, last 30 *completed* days
///   GET /credits   -> total_credits (purchased), total_usage (lifetime spent)
///
/// STATELESS BY REQUIREMENT: every figure is computed server-side by OpenRouter and read fresh
/// each poll. Nothing depends on the widget having been running earlier — shut it down for a
/// week and the numbers are still correct on restart. That rules out any locally-tracked
/// baseline or delta.
///
/// The cost of that guarantee is UTC boundaries: /keys maintains its counters on UTC days and
/// months, so "Today" rolls over at 20:00 Eastern rather than midnight. An Eastern-accurate day
/// is only obtainable either by tracking deltas locally (which breaks when the app is off) or
/// from openrouter.ai's private /api/frontend analytics endpoint, which is cookie-authenticated
/// and rejects API keys outright.
/// </summary>
public class OpenRouterSpendService : SpendServiceBase
{
    private const string ApiRoot = "https://openrouter.ai/api/v1";

    protected override string BaseUrl => ApiRoot;
    protected override string DefaultBaseUrl => ApiRoot;

    /// <summary>Fired with a fully-populated snapshot covering every window stop.</summary>
    public event Action<OpenRouterSnapshot>? SnapshotUpdated;

    protected override async Task PollOnceAsync(CancellationToken ct)
    {
        var snapshot = await FetchSnapshotAsync(ct);
        if (snapshot != null)
            SnapshotUpdated?.Invoke(snapshot);
    }

    public async Task<OpenRouterSnapshot?> FetchSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var key = AppSettingsService.Current.OpenRouterApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                RaiseError("No OpenRouter API key configured");
                return null;
            }

            var keysJson = await GetAsync("keys", key, ct);
            if (keysJson == null) return null;

            var activityJson = await GetAsync("activity", key, ct);
            if (activityJson == null) return null;

            var creditsJson = await GetAsync("credits", key, ct);
            if (creditsJson == null) return null;

            if (!OpenRouterActivityParser.TryParseKeyUsage(keysJson, out var keyUsage))
            {
                RaiseError("OpenRouter: bad keys response");
                return null;
            }

            if (!OpenRouterActivityParser.TryParseActivity(activityJson, out var byDate))
            {
                RaiseError("OpenRouter: bad activity response");
                return null;
            }

            var (totalCredits, totalUsage) = OpenRouterActivityParser.ParseCredits(creditsJson);

            var snapshot = OpenRouterActivityParser.BuildSnapshot(
                byDate, totalCredits, totalUsage,
                DateOnly.FromDateTime(DateTime.UtcNow), keyUsage);

            Log($"OK    OpenRouter today=${snapshot.Today:0.00} 7d=${snapshot.Last7Days:0.00} " +
                $"30d=${snapshot.Last30Days:0.00} mtd=${snapshot.MonthToDate:0.00} " +
                $"credits=${snapshot.RemainingCredits:0.00} of ${totalCredits:0.00} " +
                $"({byDate.Count} active days)");
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log($"ERROR OpenRouter network: {ex.Message}");
            RaiseError("OpenRouter network error");
            if (ex.StatusCode == null)
                RaiseNetworkError();
            return null;
        }
        catch (Exception ex)
        {
            Log($"ERROR OpenRouter: {ex.Message}");
            RaiseError("OpenRouter error");
            return null;
        }
    }

    /// <summary>
    /// GETs one endpoint and returns the body, or null after surfacing the failure.
    /// Absolute URLs on purpose: the shared <see cref="SpendServiceBase.EnsureHttpClient"/>
    /// trims the trailing slash off the base address, which would make a relative "credits"
    /// resolve against /api/ and drop the /v1 segment.
    /// </summary>
    private async Task<string?> GetAsync(string path, string key, CancellationToken ct)
    {
        var client = EnsureHttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/{path}");
        request.Headers.Add("Authorization", $"Bearer {key}");

        var response = await client.SendAsync(request, ct);
        Log($"INFO  OpenRouter /{path} -> {(int)response.StatusCode} {response.StatusCode}");

        // A normal sk-or-v1 inference key gets 403 here — by far the likeliest setup mistake,
        // so it gets its own message rather than a generic auth error.
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            RaiseError("OpenRouter: management key required");
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            RaiseError("OpenRouter auth failed (check key)");
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
