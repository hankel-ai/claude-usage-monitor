namespace ClaudeUsageMonitor.Services;

/// <summary>
/// Central color mapping for the usage and reset-timer bars.
///
/// The 5-hour <b>usage</b> bar goes green → orange → red as utilization rises
/// (the reference scheme). The two <b>reset-timer</b> bars (5-hour and 7-day)
/// use the reverse: red just after a reset (little time elapsed) transitioning
/// to green as the window is almost up and a reset is imminent.
///
/// Pure — returns plain RGB tuples with no UI types — so the thresholds are
/// unit-testable without spinning up WPF. Call sites convert the tuple to a
/// <c>System.Windows.Media.Color</c> (bars) or <c>System.Drawing.Color</c>
/// (tray icon) as needed.
/// </summary>
public static class BarColorMap
{
    // Shared palette — matches the existing usage-bar thresholds.
    private static readonly (byte R, byte G, byte B) Red = (244, 67, 54);
    private static readonly (byte R, byte G, byte B) Orange = (255, 152, 0);
    private static readonly (byte R, byte G, byte B) Green = (76, 175, 80);

    /// <summary>
    /// Usage scheme: green below 70, orange in [70, 90), red at 90+. Mirrors the
    /// 5-hour usage bar so the reset bars can be its exact reverse.
    /// </summary>
    public static (byte R, byte G, byte B) ThresholdColor(double percentage)
    {
        if (percentage >= 90) return Red;
        if (percentage >= 70) return Orange;
        return Green;
    }

    /// <summary>
    /// Smooth gradient for a reset-timer bar. <paramref name="elapsedPercentage"/>
    /// is how much of the window has elapsed (0 = just reset, 100 = resetting
    /// now). Interpolates red → orange → green across the full range so the
    /// color change is visible throughout, not just in the first 30%.
    /// </summary>
    public static (byte R, byte G, byte B) ResetTimerColor(double elapsedPercentage)
    {
        var t = elapsedPercentage < 0 ? 0
            : elapsedPercentage > 100 ? 1.0
            : elapsedPercentage / 100.0;

        // Two-segment lerp: red → orange (0–0.5), orange → green (0.5–1.0)
        if (t <= 0.5)
        {
            var s = t / 0.5;
            return Lerp(Red, Orange, s);
        }
        else
        {
            var s = (t - 0.5) / 0.5;
            return Lerp(Orange, Green, s);
        }
    }

    private static (byte R, byte G, byte B) Lerp(
        (byte R, byte G, byte B) a,
        (byte R, byte G, byte B) b,
        double t)
    {
        return (
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
