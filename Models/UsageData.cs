using System;

namespace ClaudeUsageMonitor.Models;

public class UsageData
{
    /// <summary>5-hour rolling window utilization (0-100)</summary>
    public double FiveHourUtilization { get; set; }
    public DateTime? FiveHourResetsAt { get; set; }

    /// <summary>7-day rolling window utilization (0-100)</summary>
    public double SevenDayUtilization { get; set; }
    public DateTime? SevenDayResetsAt { get; set; }

    public string FiveHourTooltip
    {
        get
        {
            var pct = $"5-Hour: {FiveHourUtilization:F1}%";
            if (FiveHourResetsAt.HasValue)
            {
                var remaining = FiveHourResetsAt.Value - DateTime.UtcNow;
                if (remaining.TotalMinutes > 0)
                    pct += $" (resets in {FormatTimeSpan(remaining)})";
            }
            return pct;
        }
    }

    public string SevenDayTooltip
    {
        get
        {
            var pct = $"7-Day: {SevenDayUtilization:F1}%";
            if (SevenDayResetsAt.HasValue)
            {
                var remaining = SevenDayResetsAt.Value - DateTime.UtcNow;
                if (remaining.TotalMinutes > 0)
                    pct += $" (resets in {FormatTimeSpan(remaining)})";
            }
            return pct;
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
