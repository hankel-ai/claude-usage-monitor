using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClaudeUsageMonitor.Services;

namespace ClaudeUsageMonitor;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageMonitor", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Kill any existing instances so there is never more than one running.
        var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            if (proc.Id == current.Id) continue;
            try { proc.Kill(); proc.WaitForExit(2000); } catch { }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show(
                $"An error occurred:\n\n{args.Exception.Message}\n\nDetails written to:\n{CrashLogPath}",
                "Claude Usage Monitor - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppSettingsService.UpdateStartupPath();

        base.OnStartup(e);
    }

    private static void WriteCrashLog(Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(CrashLogPath,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
