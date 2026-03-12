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
    private readonly ClaudeApiService _apiService;
    private bool _useMockData;
    private UsageData _lastUsage = new();

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

        FiveHourTrack.SizeChanged += (_, _) => UpdateBarWidths();
        SevenDayTrack.SizeChanged += (_, _) => UpdateBarWidths();

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
            FiveHourTrack.ToolTip = "No Claude Code credentials found";
            SevenDayTrack.ToolTip = "Log in via Claude Code first";
        }
    }

    private void OnUsageUpdated(UsageData usage)
    {
        Dispatcher.Invoke(() =>
        {
            _lastUsage = usage;
            UpdateBarWidths();
            FiveHourTrack.ToolTip = usage.FiveHourTooltip;
            SevenDayTrack.ToolTip = usage.SevenDayTooltip;
        });
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

    private void UpdateBarWidths()
    {
        AnimateBar(FiveHourFill, FiveHourTrack.ActualWidth, _lastUsage.FiveHourUtilization, true);
        AnimateBar(SevenDayFill, SevenDayTrack.ActualWidth, _lastUsage.SevenDayUtilization, false);
    }

    private void AnimateBar(Border fill, double trackWidth, double percentage, bool isFiveHour)
    {
        if (trackWidth <= 0) return;

        var clamped = Math.Min(Math.Max(percentage, 0), 100);
        var targetWidth = trackWidth * (clamped / 100.0);
        fill.Background = GetMeterBrush(clamped, isFiveHour);

        var animation = new DoubleAnimation
        {
            To = targetWidth,
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
            OnUsageUpdated(ClaudeApiService.GetMockData());
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            _apiService.StopPolling();
            if (AppSettingsService.HasCredentials)
                _apiService.StartPolling(AppSettingsService.Current.PollingIntervalSeconds);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_useMockData)
        {
            OnUsageUpdated(ClaudeApiService.GetMockData());
            return;
        }
        _apiService.StopPolling();
        if (AppSettingsService.HasCredentials)
            _apiService.StartPolling(AppSettingsService.Current.PollingIntervalSeconds);
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
