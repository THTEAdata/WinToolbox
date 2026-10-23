using System.IO;
using Microsoft.Win32;

namespace WinToolbox.Core;

/// <summary>程序设置（持久化到 %LOCALAPPDATA%\WinToolbox\settings.ini）。</summary>
public sealed class AppSettingsData
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public bool AutoStart { get; set; }
    public bool RunAsAdmin { get; set; }
    public bool BatteryMonitor { get; set; } = true;
    public int BatteryIntervalMinutes { get; set; } = 5;
    public bool LowBatteryAlert { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
}

public static class AppSettings
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinToolbox");

    public static string FilePath { get; } = Path.Combine(Dir, "settings.ini");

    public static AppSettingsData Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        var data = new AppSettingsData();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || !line.Contains('=')) continue;
                    int eq = line.IndexOf('=');
                    string key = line[..eq].Trim().ToLowerInvariant();
                    string value = line[(eq + 1)..].Trim();

                    switch (key)
                    {
                        case "theme":
                            data.Theme = value.Equals("dark", StringComparison.OrdinalIgnoreCase) ? AppThemeMode.Dark
                                : value.Equals("light", StringComparison.OrdinalIgnoreCase) ? AppThemeMode.Light
                                : AppThemeMode.System;
                            break;
                        case "autostart": data.AutoStart = IsTrue(value); break;
                        case "runasadmin": data.RunAsAdmin = IsTrue(value); break;
                        case "battery_monitor": data.BatteryMonitor = IsTrue(value); break;
                        case "battery_interval":
                            if (int.TryParse(value, out int iv)) data.BatteryIntervalMinutes = Math.Clamp(iv, 1, 240);
                            break;
                        case "battery_alert": data.LowBatteryAlert = IsTrue(value); break;
                        case "battery_threshold":
                            if (int.TryParse(value, out int tv)) data.LowBatteryThreshold = Math.Clamp(tv, 5, 90);
                            break;
                    }
                }
            }
        }
        catch { }

        Current = data;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var lines = new[]
            {
                "theme=" + Current.Theme.ToString().ToLowerInvariant(),
                "autostart=" + (Current.AutoStart ? "1" : "0"),
                "runasadmin=" + (Current.RunAsAdmin ? "1" : "0"),
                "battery_monitor=" + (Current.BatteryMonitor ? "1" : "0"),
                "battery_interval=" + Current.BatteryIntervalMinutes,
                "battery_alert=" + (Current.LowBatteryAlert ? "1" : "0"),
                "battery_threshold=" + Current.LowBatteryThreshold
            };
            File.WriteAllLines(FilePath, lines);
        }
        catch { }

        Changed?.Invoke();
    }

    private static bool IsTrue(string value)
        => value is "1" or "true" or "yes" or "on" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>开机自启动（HKCU Run 键，无需管理员）。</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinToolbox";

    public static string ExePath
    {
        get
        {
            string path = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(path))
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "WinToolbox.exe");
                path = File.Exists(exe) ? exe : Path.ChangeExtension(Environment.GetCommandLineArgs()[0], ".exe");
            }
            return path;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
                key.SetValue(ValueName, "\"" + ExePath + "\"", RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, false);

            return true;
        }
        catch { return false; }
    }
}
