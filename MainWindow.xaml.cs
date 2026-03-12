using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClaudeUsageMonitor.Models;
using ClaudeUsageMonitor.Services;

namespace ClaudeUsageMonitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private readonly ClaudeApiService _apiService;
    private bool _useMockData;
    private bool _isPaused;
    private bool _showResetTimers = true;
    private UsageData _lastUsage = new();
    private double _prevFiveHour;
    private double _prevSevenDay;
    private bool _hasReceivedData;

    public MainWindow()
    {
        InitializeComponent();

        var settings = AppSettingsService.Current;
        Topmost = settings.AlwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = settings.AlwaysOnTop;

        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0)
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 10;
            Top = area.Bottom - Height - 10;
        }

        _apiService = new ClaudeApiService();
        _apiService.UsageUpdated += OnUsageUpdated;
        _apiService.ErrorOccurred += OnError;
        _apiService.AuthExpired += OnAuthExpired;

        FiveHourTrack.SizeChanged += (_, _) => UpdateAllBars();
        SevenDayTrack.SizeChanged += (_, _) => UpdateAllBars();
        FiveHourTimerTrack.SizeChanged += (_, _) => UpdateTimerBars();
        SevenDayTimerTrack.SizeChanged += (_, _) => UpdateTimerBars();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.HasCredentials)
        {
            _apiService.StartPolling(AppSettingsService.Current.PollingIntervalSeconds);
        }
        else
        {
            ShowStatus("No credentials found\nRight-click \u2192 Settings");
            FiveHourTrack.ToolTip = "No Claude Code credentials found";
            SevenDayTrack.ToolTip = "Run 'claude' in a terminal and log in";
        }
    }

    private void ShowStatus(string? message)
    {
        if (message == null)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnUsageUpdated(UsageData usage)
    {
        Dispatcher.Invoke(() =>
        {
            // Capture previous values before updating
            if (_hasReceivedData)
            {
                _prevFiveHour = _lastUsage.FiveHourUtilization;
                _prevSevenDay = _lastUsage.SevenDayUtilization;
            }

            _lastUsage = usage;

            if (!_hasReceivedData)
            {
                // First data — no delta to show
                _prevFiveHour = usage.FiveHourUtilization;
                _prevSevenDay = usage.SevenDayUtilization;
                _hasReceivedData = true;
            }

            ShowStatus(null);
            UpdateAllBars();
            UpdateTimerBars();
            FiveHourTrack.ToolTip = BuildTooltipWithDelta(usage.FiveHourTooltip, usage.FiveHourUtilization, _prevFiveHour);
            SevenDayTrack.ToolTip = BuildTooltipWithDelta(usage.SevenDayTooltip, usage.SevenDayUtilization, _prevSevenDay);
            UpdateTimerTooltips();
        });
    }

    private static string BuildTooltipWithDelta(string baseTooltip, double current, double previous)
    {
        var delta = current - previous;
        if (Math.Abs(delta) < 0.05) return baseTooltip;
        var sign = delta > 0 ? "+" : "";
        return $"{baseTooltip}  ({sign}{delta:F1}% since last poll)";
    }

    private void OnError(string error)
    {
        Dispatcher.Invoke(() =>
        {
            FiveHourTrack.ToolTip = $"5-Hour: {error}";
            SevenDayTrack.ToolTip = $"7-Day: {error}";
        });
    }

    private void OnAuthExpired()
    {
        Dispatcher.Invoke(() => _apiService.StopPolling());
    }

    private void UpdateAllBars()
    {
        var fiveHourTrackW = FiveHourTrack.ActualWidth;
        var sevenDayTrackW = SevenDayTrack.ActualWidth;

        // Delta layers: full current width (bright color shows through where prev doesn't cover)
        SetBarWidth(FiveHourDeltaFill, fiveHourTrackW, _lastUsage.FiveHourUtilization);
        SetBarWidth(SevenDayDeltaFill, sevenDayTrackW, _lastUsage.SevenDayUtilization);

        // Update delta colors based on threshold
        FiveHourDeltaFill.Background = GetDeltaBrush(_lastUsage.FiveHourUtilization, true);
        SevenDayDeltaFill.Background = GetDeltaBrush(_lastUsage.SevenDayUtilization, false);

        // Base layers: previous width (normal color covers delta underneath)
        // If usage decreased (reset), show no delta — just set prev to current
        var fiveHourPrev = _lastUsage.FiveHourUtilization >= _prevFiveHour ? _prevFiveHour : _lastUsage.FiveHourUtilization;
        var sevenDayPrev = _lastUsage.SevenDayUtilization >= _prevSevenDay ? _prevSevenDay : _lastUsage.SevenDayUtilization;

        AnimateBar(FiveHourFill, fiveHourTrackW, fiveHourPrev, true);
        AnimateBar(SevenDayFill, sevenDayTrackW, sevenDayPrev, false);
    }

    private void UpdateTimerBars()
    {
        if (!_showResetTimers) return;

        var fiveHourElapsed = GetElapsedPercentage(_lastUsage.FiveHourResetsAt, FiveHourWindow);
        var sevenDayElapsed = GetElapsedPercentage(_lastUsage.SevenDayResetsAt, SevenDayWindow);

        AnimateTimerBar(FiveHourTimerFill, FiveHourTimerTrack.ActualWidth, fiveHourElapsed);
        AnimateTimerBar(SevenDayTimerFill, SevenDayTimerTrack.ActualWidth, sevenDayElapsed);
    }

    private void UpdateTimerTooltips()
    {
        FiveHourTimerTrack.ToolTip = FormatTimerTooltip("5-Hour", _lastUsage.FiveHourResetsAt, FiveHourWindow);
        SevenDayTimerTrack.ToolTip = FormatTimerTooltip("7-Day", _lastUsage.SevenDayResetsAt, SevenDayWindow);
    }

    private static double GetElapsedPercentage(DateTime? resetsAt, TimeSpan totalWindow)
    {
        if (!resetsAt.HasValue) return 0;
        var remaining = resetsAt.Value - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return 100;
        var elapsed = totalWindow - remaining;
        if (elapsed.TotalSeconds <= 0) return 0;
        return Math.Min((elapsed / totalWindow) * 100, 100);
    }

    private static string FormatTimerTooltip(string label, DateTime? resetsAt, TimeSpan totalWindow)
    {
        if (!resetsAt.HasValue) return $"{label} reset: No data";
        var remaining = resetsAt.Value - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return $"{label}: Resetting now";
        var elapsed = totalWindow - remaining;
        var pct = Math.Min((elapsed / totalWindow) * 100, 100);
        return $"{label} reset: {pct:F0}% elapsed ({FormatTimeSpan(remaining)} left)";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static void SetBarWidth(Border fill, double trackWidth, double percentage)
    {
        if (trackWidth <= 0) return;
        fill.Width = trackWidth * (Math.Clamp(percentage, 0, 100) / 100.0);
    }

    private void AnimateBar(Border fill, double trackWidth, double percentage, bool isFiveHour)
    {
        if (trackWidth <= 0) return;
        var clamped = Math.Clamp(percentage, 0, 100);
        fill.Background = GetMeterBrush(clamped, isFiveHour);
        var animation = new DoubleAnimation
        {
            To = trackWidth * (clamped / 100.0),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        fill.BeginAnimation(WidthProperty, animation);
    }

    private static void AnimateTimerBar(Border fill, double trackWidth, double percentage)
    {
        if (trackWidth <= 0) return;
        var clamped = Math.Clamp(percentage, 0, 100);
        var animation = new DoubleAnimation
        {
            To = trackWidth * (clamped / 100.0),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        fill.BeginAnimation(WidthProperty, animation);
    }

    private static Brush GetMeterBrush(double percentage, bool isFiveHour)
    {
        if (percentage >= 90) return new SolidColorBrush(Color.FromRgb(244, 67, 54));
        if (percentage >= 70) return new SolidColorBrush(Color.FromRgb(255, 152, 0));
        return isFiveHour
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(33, 150, 243));
    }

    private static Brush GetDeltaBrush(double percentage, bool isFiveHour)
    {
        if (percentage >= 90) return new SolidColorBrush(Color.FromRgb(239, 154, 154)); // light red
        if (percentage >= 70) return new SolidColorBrush(Color.FromRgb(255, 183, 77));  // light orange
        return isFiveHour
            ? new SolidColorBrush(Color.FromRgb(129, 199, 132)) // light green
            : new SolidColorBrush(Color.FromRgb(100, 181, 246)); // light blue
    }

    private void SetTimerVisibility(bool visible)
    {
        _showResetTimers = visible;
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        FiveHourTimerPanel.Visibility = vis;
        SevenDayTimerPanel.Visibility = vis;
        Height = visible ? 80 : 68;
        if (visible) UpdateTimerBars();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        AppSettingsService.Current.WindowLeft = Left;
        AppSettingsService.Current.WindowTop = Top;
        AppSettingsService.Save();
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopMenuItem.IsChecked;
        AppSettingsService.Current.AlwaysOnTop = Topmost;
        AppSettingsService.Save();
    }

    private void MockData_Click(object sender, RoutedEventArgs e)
    {
        _useMockData = MockDataMenuItem.IsChecked;
        if (_useMockData)
        {
            _apiService.StopPolling();
            MockWatermark.Visibility = Visibility.Visible;
            PausedOverlay.Visibility = Visibility.Collapsed;
            _isPaused = false;
            PausePollingMenuItem.IsChecked = false;
            OnUsageUpdated(ClaudeApiService.GetMockData());
        }
        else
        {
            MockWatermark.Visibility = Visibility.Collapsed;
            ResumePolling();
        }
    }

    private void ShowResetTimers_Click(object sender, RoutedEventArgs e)
    {
        SetTimerVisibility(ShowResetTimersMenuItem.IsChecked);
    }

    private void PausePolling_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = PausePollingMenuItem.IsChecked;
        if (_isPaused)
        {
            _apiService.StopPolling();
            PausedOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            PausedOverlay.Visibility = Visibility.Collapsed;
            if (!_useMockData)
                ResumePolling();
        }
    }

    private void ResumePolling()
    {
        _apiService.StopPolling();
        if (AppSettingsService.HasCredentials)
            _apiService.StartPolling(AppSettingsService.Current.PollingIntervalSeconds);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            if (!_useMockData && !_isPaused)
                ResumePolling();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_useMockData)
        {
            OnUsageUpdated(ClaudeApiService.GetMockData());
            return;
        }
        if (!_isPaused)
            ResumePolling();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _apiService.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _apiService.Dispose();
        base.OnClosed(e);
    }
}
