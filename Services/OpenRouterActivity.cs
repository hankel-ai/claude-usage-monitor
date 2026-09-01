using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// Stops in the OpenRouter bar's click cycle. Four spend windows plus a credit-balance view.
/// OpenRouter only exposes 30 days of history, so there is deliberately no YTD stop
/// (unlike <see cref="SpendWindow"/>).
/// </summary>
public enum OpenRouterWindow
{
    Today,
    Last7Days,
    Last30Days,
    MonthToDate,
    Balance
}

public static class OpenRouterWindowExtensions
{
    /// <summary>Short tag shown in the bar.</summary>
    public static string Tag(this OpenRouterWindow window) => window switch
    {
        OpenRouterWindow.Today       => "Today",
        OpenRouterWindow.Last7Days   => "7d",
        OpenRouterWindow.Last30Days  => "30d",
        OpenRouterWindow.MonthToDate => "MTD",
        OpenRouterWindow.Balance     => "Bal",
        _                            => "MTD",
    };

    /// <summary>
    /// Human label for tooltips. Every window is UTC-bounded because that is what OpenRouter
    /// reports; the day therefore rolls over at 20:00 Eastern, not midnight. Saying so in the
    /// tooltip is the whole reason these strings name the zone.
    /// </summary>
    public static string Description(this OpenRouterWindow window) => window switch
    {
        OpenRouterWindow.Today       => "Today (UTC day — resets 8pm ET)",
        OpenRouterWindow.Last7Days   => "Last 7 days (UTC)",
        OpenRouterWindow.Last30Days  => "Last 30 days (UTC)",
        OpenRouterWindow.MonthToDate => "Month to date (UTC)",
        OpenRouterWindow.Balance     => "Credit balance",
        _                            => "Month to date (UTC)",
    };

    /// <summary>Next stop in the click cycle (wraps).</summary>
    public static OpenRouterWindow Next(this OpenRouterWindow window)
    {
        var values = (OpenRouterWindow[])Enum.GetValues(typeof(OpenRouterWindow));
        var idx = Array.IndexOf(values, window);
        return values[(idx + 1) % values.Length];
    }

    public static OpenRouterWindow Parse(string? name)
    {
        return name?.Trim().ToUpperInvariant() switch
        {
            "TODAY"                => OpenRouterWindow.Today,
            "7D" or "LAST7DAYS"    => OpenRouterWindow.Last7Days,
            "30D" or "LAST30DAYS"  => OpenRouterWindow.Last30Days,
            "MTD" or "MONTHTODATE" => OpenRouterWindow.MonthToDate,
            "BAL" or "BALANCE"     => OpenRouterWindow.Balance,
            _ => Enum.TryParse<OpenRouterWindow>(name, true, out var w) ? w : OpenRouterWindow.MonthToDate,
        };
    }
}

/// <summary>Live per-key counters from /keys, summed across every key on the account.</summary>
public sealed record OpenRouterKeyUsage(double Lifetime, double Daily, double Weekly, double Monthly);

/// <summary>
/// One poll's worth of OpenRouter data. Every window is computed up-front so cycling the bar is a
/// pure re-render with no network call.
///
/// Every figure is server-computed and stateless: nothing here depends on the widget having been
/// running earlier. Shut the app down for a week and the numbers are still correct on restart.
/// </summary>
public sealed record OpenRouterSnapshot(
    double Today,
    double Last7Days,
    double Last30Days,
    double MonthToDate,
    double TotalCredits,
    double TotalUsage)
{
    /// <summary>Spend for a window stop. <see cref="OpenRouterWindow.Balance"/> is not a
    /// window sum and is rendered separately.</summary>
    public double For(OpenRouterWindow window) => window switch
    {
        OpenRouterWindow.Today       => Today,
        OpenRouterWindow.Last7Days   => Last7Days,
        OpenRouterWindow.Last30Days  => Last30Days,
        OpenRouterWindow.MonthToDate => MonthToDate,
        _                            => MonthToDate,
    };

    /// <summary>Credits left to spend. Can go negative on an overdrawn account.</summary>
    public double RemainingCredits => TotalCredits - TotalUsage;
}

/// <summary>
/// Pure parsing / roll-up for the OpenRouter responses. Deliberately free of HTTP, settings
/// and UI types so the test project can link this file directly (same arrangement as
/// <see cref="BarColorMap"/>).
///
/// DESIGN RULE: every figure must be derivable from a single poll's responses. No persisted
/// baselines, no deltas against remembered values — the numbers must be correct even if the
/// widget has been shut down for days. That rules out the otherwise-tempting
/// <c>total_usage - sum(buckets)</c>, which is wrong anyway: it also counts usage older than the
/// 30-day activity window (verified live at $0.18 on this account).
/// </summary>
public static class OpenRouterActivityParser
{
    /// <summary>
    /// /keys shape: { "data": [ { "usage": .., "usage_daily": .., "usage_weekly": ..,
    /// "usage_monthly": .., ... }, ... ] }. Summed across keys for an account-wide figure.
    ///
    /// These counters are maintained server-side on UTC boundaries, which is precisely why they
    /// are used: they need no local state and survive any amount of downtime.
    /// </summary>
    public static bool TryParseKeyUsage(string json, out OpenRouterKeyUsage usage)
    {
        usage = new OpenRouterKeyUsage(0, 0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            double life = 0, day = 0, week = 0, month = 0;
            foreach (var row in data.EnumerateArray())
            {
                life += Num(row, "usage");
                day += Num(row, "usage_daily");
                week += Num(row, "usage_weekly");
                month += Num(row, "usage_monthly");
            }
            usage = new OpenRouterKeyUsage(life, day, week, month);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double Num(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0;

    /// <summary>
    /// Activity shape: { "data": [ { "date": "...", "usage": 0.42, ... }, ... ] }.
    /// Rows are per date+model+endpoint, so several rows share a date.
    ///
    /// Only "usage" is summed: "byok_usage_inference" is billed by the upstream provider, not
    /// out of OpenRouter credits, so counting it would inflate spend against an OpenRouter budget.
    /// </summary>
    public static bool TryParseActivity(string json, out Dictionary<DateOnly, double> byDate)
    {
        byDate = new Dictionary<DateOnly, double>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in data.EnumerateArray())
            {
                if (!row.TryGetProperty("date", out var d) || d.ValueKind != JsonValueKind.String)
                    continue;
                if (!TryParseActivityDate(d.GetString(), out var date))
                    continue;
                if (!row.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Number)
                    continue;

                byDate.TryGetValue(date, out var running);
                byDate[date] = running + u.GetDouble();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The activity endpoint returns a midnight *timestamp* ("2026-08-31 00:00:00"), not a bare
    /// date — DateOnly.TryParse rejects the time component outright, which silently zeroed every
    /// window. Accept both shapes: bare date first, then full timestamp truncated to its date.
    /// </summary>
    public static bool TryParseActivityDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        // AdjustToUniversal keeps an offset-bearing timestamp on the UTC day the API bucketed it
        // into, rather than shifting it into the machine's local day.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        return false;
    }

    /// <summary>Credits shape: { "data": { "total_credits": 50.0, "total_usage": 12.4 } }.</summary>
    public static (double totalCredits, double totalUsage) ParseCredits(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return (0, 0);

            double credits = 0, usage = 0;
            if (data.TryGetProperty("total_credits", out var c) && c.ValueKind == JsonValueKind.Number)
                credits = c.GetDouble();
            if (data.TryGetProperty("total_usage", out var u) && u.ValueKind == JsonValueKind.Number)
                usage = u.GetDouble();
            return (credits, usage);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Assembles the snapshot from one poll.
    ///
    /// Today and MTD come straight from the server-side counters. The rolling 7d/30d windows are
    /// the completed-day buckets plus today's counter — today's own bucket is excluded because
    /// /activity only publishes *completed* UTC days, and anything dated at or after
    /// <paramref name="utcToday"/> would double-count against <paramref name="keys"/>.Daily.
    ///
    /// <paramref name="utcToday"/> is the UTC date, matching the buckets and the counters. Using
    /// the Eastern date here would misalign the window edges against UTC-bucketed data.
    /// </summary>
    public static OpenRouterSnapshot BuildSnapshot(
        Dictionary<DateOnly, double> byDate, double totalCredits, double totalUsage,
        DateOnly utcToday, OpenRouterKeyUsage keys)
    {
        double last7 = 0, last30 = 0;
        foreach (var (date, amount) in byDate)
        {
            if (date >= utcToday) continue;
            if (date >= utcToday.AddDays(-6)) last7 += amount;
            if (date >= utcToday.AddDays(-29)) last30 += amount;
        }

        return new OpenRouterSnapshot(
            keys.Daily,
            last7 + keys.Daily,
            last30 + keys.Daily,
            keys.Monthly,
            totalCredits,
            totalUsage);
    }
}
