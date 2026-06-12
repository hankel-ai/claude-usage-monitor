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
    /// Reverse coloring for a reset-timer bar. <paramref name="elapsedPercentage"/>
    /// is how much of the window has elapsed (0 = just reset, 100 = resetting
    /// now). Returns red when little has elapsed and green as the reset nears —
    /// the mirror image of <see cref="ThresholdColor"/>.
    /// </summary>
    public static (byte R, byte G, byte B) ResetTimerColor(double elapsedPercentage)
    {
        var clamped = elapsedPercentage < 0 ? 0
            : elapsedPercentage > 100 ? 100
            : elapsedPercentage;
        return ThresholdColor(100 - clamped);
    }
}
