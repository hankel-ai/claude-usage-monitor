using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeUsageMonitor.Models;
using ClaudeUsageMonitor.Services;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ClaudeUsageMonitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private static readonly double BaseContentWidth = 220;
    private static readonly double BaseContentHeight = 80;

    private readonly ClaudeApiService _apiService;
    private DispatcherTimer? _localTimer;

    // Pause state — any true flag suppresses polling
    private bool _isPaused;       // user-initiated via menu
    private bool _fiveHourMaxed;  // 5-hour meter hit 100%
    private bool _sevenDayMaxed;  // 7-day meter hit 100%
    private bool _authExpired;    // received 401; waiting for token refresh
    private bool _rateLimited;    // received 429; waiting for backoff window
    private DateTime _rateLimitedAt;    // local time the 429 was received (display)
    private DateTime _rateLimitedUntil; // UTC time the backoff window ends (auto-resume)

    private bool ShouldBePaused => _isPaused || _fiveHourMaxed || _sevenDayMaxed || _authExpired || _rateLimited;

    private bool _showResetTimers = true;
    private UsageData _lastUsage = new();
    private double _prevFiveHour;
    private double _prevSevenDay;
    private bool _hasReceivedData;
    private bool _suppressZoom;
    private DispatcherTimer? _hoverDelayTimer;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _isZoomed;
    private bool _tooltipsEnabled;
    private DateTime _lastClickTime;
    private DispatcherTimer? _singleClickTimer;

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
            var padX = (Width - BaseContentWidth) / 2;
            var padY = (Height - BaseContentHeight) / 2;
            Left = area.Right - BaseContentWidth - 10 - padX;
            Top = area.Bottom - BaseContentHeight - 10 - padY;
        }

        _apiService = new ClaudeApiService();
        _apiService.UsageUpdated += OnUsageUpdated;
        _apiService.ErrorOccurred += OnError;
        _apiService.AuthExpired += OnAuthExpired;
        _apiService.RateLimited += OnRateLimited;

        FiveHourTrack.SizeChanged += (_, _) => UpdateAllBars();
        SevenDayTrack.SizeChanged += (_, _) => UpdateAllBars();
        FiveHourTimerTrack.SizeChanged += (_, _) => UpdateTimerBars();
        SevenDayTimerTrack.SizeChanged += (_, _) => UpdateTimerBars();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _localTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _localTimer.Tick += OnLocalTimerTick;
        _localTimer.Start();

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
            UpdateBarTexts();
            FiveHourTrack.ToolTip = BuildTooltipWithDelta(usage.FiveHourTooltip, usage.FiveHourUtilization, _prevFiveHour);
            SevenDayTrack.ToolTip = BuildTooltipWithDelta(usage.SevenDayTooltip, usage.SevenDayUtilization, _prevSevenDay);
            UpdateTimerTooltips();

            // Auto-pause if either meter just hit 100%
            var prevFiveMaxed = _fiveHourMaxed;
            var prevSevenMaxed = _sevenDayMaxed;
            _fiveHourMaxed = usage.FiveHourUtilization >= 100;
            _sevenDayMaxed = usage.SevenDayUtilization >= 100;
            if ((_fiveHourMaxed && !prevFiveMaxed) || (_sevenDayMaxed && !prevSevenMaxed))
                ApplyPauseState();

            UpdateTrayIcon();
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
        Dispatcher.Invoke(() =>
        {
            _authExpired = true;
            ShowStatus("Token expired\nWaiting for refresh...");
            ApplyPauseState();
        });
    }

    private void OnRateLimited(DateTime backoffUntilUtc)
    {
        Dispatcher.Invoke(() =>
        {
            _rateLimited = true;
            _rateLimitedAt = DateTime.Now;
            _rateLimitedUntil = backoffUntilUtc;
            ApplyPauseState();
        });
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

        FiveHourTimerText.Text = GetTimerBarText(_lastUsage.FiveHourResetsAt);
        SevenDayTimerText.Text = GetTimerBarText(_lastUsage.SevenDayResetsAt);
    }

    private static string GetTimerBarText(DateTime? resetsAt)
    {
        if (!resetsAt.HasValue) return "";
        var remaining = resetsAt.Value - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return "resetting...";
        return FormatTimeSpan(remaining);
    }

    private void UpdateTimerTooltips()
    {
        FiveHourTimerTrack.ToolTip = FormatTimerTooltip("5-Hour", _lastUsage.FiveHourResetsAt, FiveHourWindow);
        SevenDayTimerTrack.ToolTip = FormatTimerTooltip("7-Day", _lastUsage.SevenDayResetsAt, SevenDayWindow);
    }

    private void UpdateBarTexts()
    {
        FiveHourBarText.Text = GetBarText(_lastUsage.FiveHourUtilization);
        SevenDayBarText.Text = GetBarText(_lastUsage.SevenDayUtilization);
    }

    private static string GetBarText(double utilization) =>
        utilization >= 100 ? "100%" : $"{utilization:F0}%";

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
        OuterBorder.Height = visible ? 80 : 68;
        if (visible) UpdateTimerBars();
    }

    private static readonly Brush DragBorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x44, 0x88, 0xFF));
    private static readonly Brush NormalBorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hoverDelayTimer?.Stop();
        _suppressZoom = true;
        AnimateScale(1.0, 100);

        // Highlight border during drag
        OuterBorder.BorderBrush = DragBorderBrush;
        OuterBorder.BorderThickness = new Thickness(2);

        var startLeft = Left;
        var startTop = Top;
        DragMove();

        // Restore border after drag
        OuterBorder.BorderBrush = NormalBorderBrush;
        OuterBorder.BorderThickness = new Thickness(1);

        if (Math.Abs(Left - startLeft) < 2 && Math.Abs(Top - startTop) < 2)
        {
            var now = DateTime.UtcNow;
            var doubleClickThreshold = TimeSpan.FromMilliseconds(
                System.Windows.Forms.SystemInformation.DoubleClickTime);

            if (now - _lastClickTime < doubleClickThreshold)
            {
                // Double-click — minimize to tray
                _singleClickTimer?.Stop();
                _lastClickTime = DateTime.MinValue;
                MinimizeToTray_Click(sender, e);
                return;
            }

            _lastClickTime = now;

            // Delay single-click action to distinguish from double-click
            _singleClickTimer?.Stop();
            _singleClickTimer = new DispatcherTimer { Interval = doubleClickThreshold };
            _singleClickTimer.Tick += (_, _) =>
            {
                _singleClickTimer.Stop();
                Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true });
            };
            _singleClickTimer.Start();
            return;
        }
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

    private void ShowResetTimers_Click(object sender, RoutedEventArgs e)
    {
        SetTimerVisibility(ShowResetTimersMenuItem.IsChecked);
    }

    private void PausePolling_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = PausePollingMenuItem.IsChecked;
        if (!_isPaused)
        {
            // User unchecked — clear all auto-pause flags so polling resumes
            _fiveHourMaxed = false;
            _sevenDayMaxed = false;
            _authExpired = false;
            _rateLimited = false;
            ShowStatus(null);
        }
        ApplyPauseState();
    }

    private void ApplyPauseState()
    {
        var paused = ShouldBePaused;
        PausePollingMenuItem.IsChecked = paused;
        PausedOverlay.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;

        if (_rateLimited)
        {
            PausedOverlayText.Text = $"429 Too Many Requests\n{_rateLimitedAt:M/d h:mm:ss tt}";
            PausedOverlayText.Visibility = Visibility.Visible;
        }
        else
        {
            PausedOverlayText.Visibility = Visibility.Collapsed;
        }

        if (paused)
        {
            _apiService.StopPolling();
        }
        else if (AppSettingsService.HasCredentials)
        {
            _apiService.StopPolling();
            _apiService.StartPolling(AppSettingsService.Current.PollingIntervalSeconds);
        }
    }

    /// <summary>
    /// Fires every 5 s: keeps timer bars ticking while paused, and checks whether
    /// any auto-pause condition has resolved so polling can resume.
    /// </summary>
    private void OnLocalTimerTick(object? sender, EventArgs e)
    {
        if (_showResetTimers)
            UpdateTimerBars();

        UpdateTrayIcon();

        var stateChanged = false;

        // 5-hour meter reset
        if (_fiveHourMaxed && _lastUsage.FiveHourResetsAt.HasValue &&
            DateTime.UtcNow >= _lastUsage.FiveHourResetsAt.Value)
        {
            _fiveHourMaxed = false;
            _lastUsage.FiveHourUtilization = 0;
            stateChanged = true;
        }

        // 7-day meter reset
        if (_sevenDayMaxed && _lastUsage.SevenDayResetsAt.HasValue &&
            DateTime.UtcNow >= _lastUsage.SevenDayResetsAt.Value)
        {
            _sevenDayMaxed = false;
            _lastUsage.SevenDayUtilization = 0;
            stateChanged = true;
        }

        // Auth expiry: check if a new valid token has appeared on disk
        if (_authExpired && IsNewTokenValid())
        {
            _authExpired = false;
            ShowStatus(null);
            stateChanged = true;
        }

        // 429 backoff window expired
        if (_rateLimited && DateTime.UtcNow >= _rateLimitedUntil)
        {
            _rateLimited = false;
            stateChanged = true;
        }

        if (stateChanged)
        {
            UpdateAllBars();
            UpdateBarTexts();
            ApplyPauseState();
        }
    }

    /// <summary>
    /// Returns true when the on-disk token is different from the one that caused the 401
    /// AND can be confirmed as not-yet-expired (via credentials file expiresAt or JWT decode).
    /// </summary>
    private bool IsNewTokenValid()
    {
        var token = AppSettingsService.GetOAuthToken();
        if (string.IsNullOrEmpty(token)) return false;
        if (token == _apiService.LastFailedToken) return false;

        // 1. Prefer explicit expiresAt field in credentials JSON
        var expiresAt = AppSettingsService.GetTokenExpiresAt();
        if (expiresAt.HasValue)
            return expiresAt.Value > DateTime.UtcNow.AddMinutes(1);

        // 2. Try decoding as JWT (Bearer tokens with header.payload.signature)
        if (TryDecodeJwtExpiry(token, out var jwtExpiry))
            return jwtExpiry > DateTime.UtcNow.AddMinutes(1);

        // 3. Token changed but we can't verify expiry — assume it's fresh
        return true;
    }

    /// <summary>
    /// Attempts to decode the JWT payload and extract the 'exp' Unix timestamp.
    /// Returns false if the string is not a valid JWT or has no 'exp' claim.
    /// </summary>
    private static bool TryDecodeJwtExpiry(string token, out DateTime expiry)
    {
        expiry = default;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var payload = parts[1];
            // Re-pad to valid base64
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
                             .Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var exp))
            {
                expiry = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime;
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            // Clear auth-expired flag in case the user refreshed credentials externally
            _authExpired = false;
            ShowStatus(null);
            ApplyPauseState();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldBePaused)
            ApplyPauseState();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeUsageMonitor", "log.txt");
        if (System.IO.File.Exists(logPath))
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_trayIcon == null)
        {
            _trayIcon = new WinForms.NotifyIcon();
            // Use the application icon from the exe, fall back to a system icon
            var exePath = Environment.ProcessPath;
            if (exePath != null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) _trayIcon.Icon = icon;
            }
            _trayIcon.Icon ??= System.Drawing.SystemIcons.Application;
            _trayIcon.Text = "Claude Usage Monitor";
            _trayIcon.Click += (_, _) => RestoreFromTray();

            var trayMenu = new WinForms.ContextMenuStrip();
            trayMenu.Items.Add("Restore", null, (_, _) => RestoreFromTray());
            trayMenu.Items.Add("Exit", null, (_, _) =>
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();
                _apiService.Dispose();
                Application.Current.Shutdown();
            });
            _trayIcon.ContextMenuStrip = trayMenu;
        }

        _trayIcon.Visible = true;
        UpdateTrayIcon();
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null || !_trayIcon.Visible) return;

        const int size = 16;
        const int barWidth = 6;
        const int gap = 2;
        const int leftX = 1;
        const int rightX = leftX + barWidth + gap;
        const int barTop = 1;
        const int barBottom = 14; // leaves 1px padding top and bottom
        int barHeight = barBottom - barTop;

        using var bmp = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.Clear(Drawing.Color.Transparent);

        // Draw background tracks
        var trackColor = Drawing.Color.FromArgb(180, 42, 42, 42);
        using (var trackBrush = new Drawing.SolidBrush(trackColor))
        {
            g.FillRectangle(trackBrush, leftX, barTop, barWidth, barHeight);
            g.FillRectangle(trackBrush, rightX, barTop, barWidth, barHeight);
        }

        // Left bar: 5-hour usage (fills bottom to top)
        var usagePct = Math.Clamp(_lastUsage.FiveHourUtilization, 0, 100) / 100.0;
        int usageFillH = (int)(barHeight * usagePct);
        if (usageFillH > 0)
        {
            var usageColor = _lastUsage.FiveHourUtilization >= 90
                ? Drawing.Color.FromArgb(244, 67, 54)    // red
                : _lastUsage.FiveHourUtilization >= 70
                    ? Drawing.Color.FromArgb(255, 152, 0) // orange
                    : Drawing.Color.FromArgb(76, 175, 80); // green
            using var brush = new Drawing.SolidBrush(usageColor);
            g.FillRectangle(brush, leftX, barBottom - usageFillH, barWidth, usageFillH);
        }

        // Right bar: 5-hour reset timer (fills bottom to top)
        var timerPct = GetElapsedPercentage(_lastUsage.FiveHourResetsAt, FiveHourWindow) / 100.0;
        int timerFillH = (int)(barHeight * timerPct);
        if (timerFillH > 0)
        {
            var timerColor = Drawing.Color.FromArgb(204, 51, 51); // #CC3333
            using var brush = new Drawing.SolidBrush(timerColor);
            g.FillRectangle(brush, rightX, barBottom - timerFillH, barWidth, timerFillH);
        }

        var oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = Drawing.Icon.FromHandle(bmp.GetHicon());
        if (oldIcon != null) NativeMethods.DestroyIcon(oldIcon.Handle);

        // Update tooltip with current stats
        var tip = $"5h: {_lastUsage.FiveHourUtilization:F0}%";
        if (_lastUsage.FiveHourResetsAt.HasValue)
        {
            var remaining = _lastUsage.FiveHourResetsAt.Value - DateTime.UtcNow;
            if (remaining.TotalSeconds > 0)
                tip += $" | resets in {FormatTimeSpan(remaining)}";
        }
        _trayIcon.Text = tip;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _apiService.Dispose();
        Application.Current.Shutdown();
    }

    private void SetTooltipsEnabled(bool enabled)
    {
        _tooltipsEnabled = enabled;
        ToolTipService.SetIsEnabled(FiveHourTrack, enabled);
        ToolTipService.SetIsEnabled(SevenDayTrack, enabled);
        ToolTipService.SetIsEnabled(FiveHourTimerTrack, enabled);
        ToolTipService.SetIsEnabled(SevenDayTimerTrack, enabled);
    }

    private void OuterBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _isZoomed = false;
        SetTooltipsEnabled(false);
        if (_suppressZoom) return;
        var delayMs = AppSettingsService.Current.HoverZoomDelayMs;
        if (delayMs <= 0)
        {
            AnimateScale(1.5, 150);
            _isZoomed = true;
            return;
        }
        _hoverDelayTimer?.Stop();
        _hoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _hoverDelayTimer.Tick += (_, _) =>
        {
            _hoverDelayTimer.Stop();
            if (!_suppressZoom)
            {
                AnimateScale(1.5, 150);
                _isZoomed = true;
            }
        };
        _hoverDelayTimer.Start();
    }

    private void OuterBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverDelayTimer?.Stop();
        _suppressZoom = false;
        _isZoomed = false;
        SetTooltipsEnabled(false);
        AnimateScale(1.0, 150);
    }

    private void OuterBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isZoomed && !_tooltipsEnabled)
            SetTooltipsEnabled(true);
    }

    private void AnimateScale(double targetScale, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        BorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        BorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    protected override void OnClosed(EventArgs e)
    {
        _localTimer?.Stop();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }
        _apiService.Dispose();
        base.OnClosed(e);

        // Safety net: force-kill the process so stale threadpool work
        // (e.g. in-flight HTTP continuations, WinForms interop windows)
        // cannot keep the process alive.
        Environment.Exit(0);
    }
}
