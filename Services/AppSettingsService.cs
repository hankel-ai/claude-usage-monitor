using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Services;

public class AppSettingsData
{
    public int PollingIntervalSeconds { get; set; } = 120;
    public bool AlwaysOnTop { get; set; } = true;
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public int HoverZoomDelayMs { get; set; } = 300;

    // --- LiteLLM Vertex spend meter ---
    public string LiteLLMApiKey { get; set; } = "";
    public string LiteLLMBaseUrl { get; set; } = "https://litellm.example.com";
    public double LiteLLMMonthlyBudget { get; set; } = 500;
    public bool ShowLiteLLMSpend { get; set; } = true;
    public string LiteLLMSpendWindow { get; set; } = "MTD";
}

public static class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageMonitor");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly string ClaudeCredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    private static AppSettingsData? _current;

    public static AppSettingsData Current
    {
        get
        {
            if (_current == null) Load();
            return _current!;
        }
    }

    public static string WebView2DataPath => Path.Combine(SettingsDir, "WebView2Data");

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _current = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
            }
            else
            {
                _current = new AppSettingsData();
            }
        }
        catch
        {
            _current = new AppSettingsData();
        }
    }

    public static void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsDir))
                Directory.CreateDirectory(SettingsDir);

            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Reads the OAuth access token from Claude Code's credentials file.
    /// Path: ~/.claude/.credentials.json
    /// Format: { "claudeAiOauth": { "accessToken": "sk-ant-oat01-..." } }
    /// </summary>
    public static string? GetOAuthToken()
    {
        try
        {
            if (!File.Exists(ClaudeCredentialsPath)) return null;

            var json = File.ReadAllText(ClaudeCredentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var token))
            {
                var value = token.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasCredentials => !string.IsNullOrEmpty(GetOAuthToken());

    /// <summary>True when a LiteLLM Vertex API key has been configured.</summary>
    public static bool HasLiteLLMKey => !string.IsNullOrWhiteSpace(Current.LiteLLMApiKey);

    /// <summary>
    /// Reads the token expiry from claudeAiOauth.expiresAt (Unix milliseconds).
    /// Returns null if the field is absent or the file can't be read.
    /// </summary>
    public static DateTime? GetTokenExpiresAt()
    {
        try
        {
            if (!File.Exists(ClaudeCredentialsPath)) return null;
            var json = File.ReadAllText(ClaudeCredentialsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("expiresAt", out var exp))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()).UtcDateTime;
            }
            return null;
        }
        catch { return null; }
    }

    public static string CredentialsFilePath => ClaudeCredentialsPath;

    // --- Launch on Startup (HKCU registry, no admin needed) ---

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ClaudeUsageMonitor";

    public static bool GetLaunchOnStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    public static void SetLaunchOnStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// If startup is enabled, update the registry path to the current exe location.
    /// Call on app launch so the startup entry stays correct after moving the exe.
    /// </summary>
    public static void UpdateStartupPath()
    {
        if (GetLaunchOnStartup())
            SetLaunchOnStartup(true);
    }
}
