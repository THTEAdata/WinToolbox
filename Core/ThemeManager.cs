using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinToolbox.Core;

public enum AppThemeMode { System, Light, Dark }

/// <summary>主题管理：浅色 / 深色 / 跟随系统，选择持久化到本机设置文件。</summary>
public static class ThemeManager
{
    private static DispatcherTimer? _watcher;
    private static bool _appliedDark;

    public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    /// <summary>当前实际生效的外观是否为深色。</summary>
    public static bool IsDark => Mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => !SystemUsesLightTheme()
    };

    public static string ModeText => Mode switch
    {
        AppThemeMode.Dark => "深色",
        AppThemeMode.Light => "浅色",
        _ => "跟随系统"
    };

    public static event Action? Changed;

    public static void Initialize()
    {
        AppSettings.Load();
        Mode = AppSettings.Current.Theme;
        Apply();
        StartWatcher();
    }

    public static void SetMode(AppThemeMode mode)
    {
        Mode = mode;
        AppSettings.Current.Theme = mode;
        AppSettings.Save();
        Apply();
    }

    /// <summary>切换调色板字典；样式里全部使用 DynamicResource，因此无需重建界面。</summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        bool dark = IsDark;
        var dict = app.Resources.MergedDictionaries;
        var existing = dict.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("Palette."));

        var fresh = new ResourceDictionary
        {
            // 用 pack URI 指向本程序集，避免宿主程序集（如校验工具）解析相对路径失败
            Source = new Uri(
                dark
                    ? "pack://application:,,,/WinToolbox;component/Themes/Palette.Dark.xaml"
                    : "pack://application:,,,/WinToolbox;component/Themes/Palette.Light.xaml",
                UriKind.Absolute)
        };

        if (existing != null)
        {
            int index = dict.IndexOf(existing);
            dict[index] = fresh;
        }
        else
        {
            dict.Add(fresh);
        }

        _appliedDark = dark;
        Changed?.Invoke();
    }

    public static void ApplyChrome(IntPtr hwnd) => Native.ApplyChrome(hwnd, _appliedDark);

    private static void StartWatcher()
    {
        _watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watcher.Tick += (_, _) =>
        {
            if (Mode != AppThemeMode.System) return;
            if (IsDark == _appliedDark) return;
            Apply();
        };
        _watcher.Start();
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1) != 0;
        }
        catch { return true; }
    }


    public static string SettingsPath => AppSettings.FilePath;
}
