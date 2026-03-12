using System.Windows;
using System.Windows.Media;
using ClaudeUsageMonitor.Services;

namespace ClaudeUsageMonitor;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        IntervalSlider.Value = AppSettingsService.Current.PollingIntervalSeconds;
        UpdateIntervalLabel();
        UpdateAuthStatus();
    }

    private void UpdateAuthStatus()
    {
        var hasToken = AppSettingsService.HasCredentials;

        AuthStatusDot.Fill = hasToken
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(244, 67, 54));

        AuthStatusLabel.Text = hasToken
            ? "OAuth token found"
            : "No credentials found";

        CredPathLabel.Text = $"Path: {AppSettingsService.CredentialsFilePath}";
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateIntervalLabel();
    }

    private void UpdateIntervalLabel()
    {
        if (IntervalLabel == null) return;
        var seconds = (int)IntervalSlider.Value;
        if (seconds >= 60)
        {
            var min = seconds / 60;
            var sec = seconds % 60;
            IntervalLabel.Text = sec > 0 ? $"{min}m {sec}s" : $"{min} min";
        }
        else
        {
            IntervalLabel.Text = $"{seconds}s";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Current.PollingIntervalSeconds = (int)IntervalSlider.Value;
        AppSettingsService.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
