using ClaudeUsageMonitor.Services;

// Minimal dependency-free test runner for BarColorMap. Returns a non-zero exit
// code on any failure so it can gate CI. Run: dotnet run --project tests/BarColorMapTests
int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) { Console.WriteLine($"ok  {msg}"); }
    else { Console.WriteLine($"FAIL {msg}"); failures++; }
}

var red = ((byte)244, (byte)67, (byte)54);
var orange = ((byte)255, (byte)152, (byte)0);
var green = ((byte)76, (byte)175, (byte)80);

// --- Usage scheme (reference: green -> orange -> red as it increases) ---
Check(BarColorMap.ThresholdColor(0) == green, "usage 0% -> green");
Check(BarColorMap.ThresholdColor(69.9) == green, "usage 69.9% -> green");
Check(BarColorMap.ThresholdColor(70) == orange, "usage 70% -> orange");
Check(BarColorMap.ThresholdColor(89.9) == orange, "usage 89.9% -> orange");
Check(BarColorMap.ThresholdColor(90) == red, "usage 90% -> red");
Check(BarColorMap.ThresholdColor(100) == red, "usage 100% -> red");

// --- Reset-timer smooth gradient (red -> orange -> green as elapsed increases) ---
Check(BarColorMap.ResetTimerColor(0) == red, "timer 0% elapsed (just reset) -> red");
Check(BarColorMap.ResetTimerColor(50) == orange, "timer 50% elapsed -> orange");
Check(BarColorMap.ResetTimerColor(100) == green, "timer 100% elapsed (almost reset) -> green");

// Intermediate values should be between the endpoint colors
var (r25, g25, _) = BarColorMap.ResetTimerColor(25);
Check(r25 >= red.Item1 && r25 <= orange.Item1, "timer 25% R between red and orange");
Check(g25 > red.Item2 && g25 < orange.Item2, "timer 25% G between red and orange");

var (r75, g75, _) = BarColorMap.ResetTimerColor(75);
Check(r75 < orange.Item1, "timer 75% R less than orange (heading toward green)");
Check(g75 > orange.Item2, "timer 75% G greater than orange (heading toward green)");

// Monotonic: green channel increases across the range
var (_, g20, _) = BarColorMap.ResetTimerColor(20);
var (_, g80, _) = BarColorMap.ResetTimerColor(80);
Check(g20 < g80, "green channel increases over time");

// --- Clamping out-of-range inputs ---
Check(BarColorMap.ResetTimerColor(-10) == red, "timer clamps < 0 -> red");
Check(BarColorMap.ResetTimerColor(150) == green, "timer clamps > 100 -> green");

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) FAILED");
return failures == 0 ? 0 : 1;
