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

// --- Reset-timer reverse scheme (red just after reset -> green as reset nears) ---
Check(BarColorMap.ResetTimerColor(0) == red, "timer 0% elapsed (just reset) -> red");
Check(BarColorMap.ResetTimerColor(5) == red, "timer 5% elapsed -> red");
Check(BarColorMap.ResetTimerColor(10) == red, "timer 10% elapsed -> red");
Check(BarColorMap.ResetTimerColor(10.1) == orange, "timer 10.1% elapsed -> orange");
Check(BarColorMap.ResetTimerColor(30) == orange, "timer 30% elapsed -> orange");
Check(BarColorMap.ResetTimerColor(30.1) == green, "timer 30.1% elapsed -> green");
Check(BarColorMap.ResetTimerColor(70) == green, "timer 70% elapsed -> green");
Check(BarColorMap.ResetTimerColor(100) == green, "timer 100% elapsed (almost reset) -> green");

// --- It is exactly the mirror of the usage scheme ---
Check(BarColorMap.ResetTimerColor(40) == BarColorMap.ThresholdColor(60), "reverse symmetry at 40/60");
Check(BarColorMap.ResetTimerColor(85) == BarColorMap.ThresholdColor(15), "reverse symmetry at 85/15");

// --- Clamping out-of-range inputs ---
Check(BarColorMap.ResetTimerColor(-10) == red, "timer clamps < 0 -> red");
Check(BarColorMap.ResetTimerColor(150) == green, "timer clamps > 100 -> green");

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) FAILED");
return failures == 0 ? 0 : 1;
