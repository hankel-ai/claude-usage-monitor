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
        HoverDelaySlider.Value = AppSettingsService.Current.HoverZoomDelayMs;
        StartupCheckBox.IsChecked = AppSettingsService.GetLaunchOnStartup();

        LiteLLMKeyBox.Password = AppSettingsService.Current.LiteLLMApiKey;
        LiteLLMBaseUrlBox.Text = AppSettingsService.Current.LiteLLMBaseUrl;
        LiteLLMBudgetBox.Text = AppSettingsService.Current.LiteLLMMonthlyBudget
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        SpendIntervalSlider.Value = AppSettingsService.Current.LiteLLMPollingIntervalSeconds;

        OpenRouterKeyBox.Password = AppSettingsService.Current.OpenRouterApiKey;
        OpenRouterBudgetBox.Text = AppSettingsService.Current.OpenRouterMonthlyBudget
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        OpenRouterIntervalSlider.Value = AppSettingsService.Current.OpenRouterPollingIntervalSeconds;

        UpdateIntervalLabel();
        UpdateHoverDelayLabel();
        UpdateSpendIntervalLabel();
        UpdateOpenRouterIntervalLabel();
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

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateHoverDelayLabel();
    }

    private void SpendIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSpendIntervalLabel();
    }

    private void UpdateSpendIntervalLabel()
    {
        if (SpendIntervalLabel == null) return;
        var seconds = (int)SpendIntervalSlider.Value;
        if (seconds >= 60)
        {
            var min = seconds / 60;
            var sec = seconds % 60;
            SpendIntervalLabel.Text = sec > 0 ? $"{min}m {sec}s" : $"{min} min";
        }
        else
        {
            SpendIntervalLabel.Text = $"{seconds}s";
        }
    }

    private void OpenRouterIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpenRouterIntervalLabel();
    }

    private void UpdateOpenRouterIntervalLabel()
    {
        if (OpenRouterIntervalLabel == null) return;
        var seconds = (int)OpenRouterIntervalSlider.Value;
        if (seconds >= 60)
        {
            var min = seconds / 60;
            var sec = seconds % 60;
            OpenRouterIntervalLabel.Text = sec > 0 ? $"{min}m {sec}s" : $"{min} min";
        }
        else
        {
            OpenRouterIntervalLabel.Text = $"{seconds}s";
        }
    }

    private void UpdateHoverDelayLabel()
    {
        if (HoverDelayLabel == null) return;
        var ms = (int)HoverDelaySlider.Value;
        HoverDelayLabel.Text = ms == 0 ? "Instant" : $"{ms}ms";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Current.PollingIntervalSeconds = (int)IntervalSlider.Value;
        AppSettingsService.Current.HoverZoomDelayMs = (int)HoverDelaySlider.Value;

        AppSettingsService.Current.LiteLLMApiKey = LiteLLMKeyBox.Password.Trim();
        var baseUrl = LiteLLMBaseUrlBox.Text.Trim();
        AppSettingsService.Current.LiteLLMBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://litellm.example.com" : baseUrl;
        AppSettingsService.Current.LiteLLMPollingIntervalSeconds = (int)SpendIntervalSlider.Value;
        if (double.TryParse(LiteLLMBudgetBox.Text.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var budget) && budget > 0)
        {
            AppSettingsService.Current.LiteLLMMonthlyBudget = budget;
        }

        AppSettingsService.Current.OpenRouterApiKey = OpenRouterKeyBox.Password.Trim();
        AppSettingsService.Current.OpenRouterPollingIntervalSeconds = (int)OpenRouterIntervalSlider.Value;
        if (double.TryParse(OpenRouterBudgetBox.Text.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var orBudget) && orBudget > 0)
        {
            AppSettingsService.Current.OpenRouterMonthlyBudget = orBudget;
        }

        AppSettingsService.Save();
        AppSettingsService.SetLaunchOnStartup(StartupCheckBox.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
