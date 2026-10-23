using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class SettingsView : UserControl, IAppPage
{
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        ThemeManager.Changed += OnThemeChanged;
        UpdateUi();
    }

    public void OnActivated() => UpdateUi();

    public void OnDeactivated() { }

    public void OnClosed() => ThemeManager.Changed -= OnThemeChanged;

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(UpdateUi);
            return;
        }
        UpdateUi();
    }

    private void UpdateUi()
    {
        _loading = true;

        try
        {
            // 外观
            var mode = ThemeManager.Mode;
            LightBtn.Style = (Style)FindResource(mode == AppThemeMode.Light ? "BtnPrimary" : "Btn");
            DarkBtn.Style = (Style)FindResource(mode == AppThemeMode.Dark ? "BtnPrimary" : "Btn");
            SystemBtn.Style = (Style)FindResource(mode == AppThemeMode.System ? "BtnPrimary" : "Btn");

            string actual = ThemeManager.IsDark ? "深色" : "浅色";
            CurrentText.Text = mode == AppThemeMode.System
                ? $"当前：跟随系统（系统为{actual}）"
                : $"当前：{mode switch { AppThemeMode.Dark => "深色", _ => "浅色" }}";

            // 启动与权限
            var s = AppSettings.Current;
            AutoStartChk.IsChecked = AutoStart.IsEnabled();
            AutoStartPath.Text = "启动项：" + AutoStart.ExePath;

            AdminChk.IsChecked = s.RunAsAdmin;
            AdminStateText.Text = Native.IsAdmin()
                ? "当前以管理员身份运行。"
                : s.RunAsAdmin
                    ? "已开启：下次启动会请求管理员权限（UAC）。"
                    : "当前以普通权限运行；部分清理/服务操作需要管理员。";

            // 电量监控
            MonitorChk.IsChecked = s.BatteryMonitor;
            AlertChk.IsChecked = s.LowBatteryAlert;
            SelectByTag(IntervalBox, s.BatteryIntervalMinutes);
            SelectByTag(ThresholdBox, s.LowBatteryThreshold);

            LastCheckText.Text = BatteryMonitor.LastCheck is { } t
                ? "上次检查：" + t.ToString("HH:mm:ss")
                : "尚未检查";

            // 关于
            var version = typeof(SettingsView).Assembly.GetName().Version;
            VersionText.Text = "WinToolbox " + (version?.ToString(3) ?? "1.0.0");
            RuntimeText.Text = ".NET " + Environment.Version + "（自包含运行时）";
            PathText.Text = AppContext.BaseDirectory.TrimEnd('\\');
            SettingsText.Text = AppSettings.FilePath;
        }
        finally { _loading = false; }
    }

    private static void SelectByTag(ComboBox box, int value)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int v) && v == value)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 1;
    }

    // ---------------- 外观 ----------------

    private void Light_Click(object sender, RoutedEventArgs e) => Switch(AppThemeMode.Light);

    private void Dark_Click(object sender, RoutedEventArgs e) => Switch(AppThemeMode.Dark);

    private void System_Click(object sender, RoutedEventArgs e) => Switch(AppThemeMode.System);

    private void Switch(AppThemeMode mode)
    {
        ThemeManager.SetMode(mode);
        UpdateUi();
    }

    // ---------------- 启动与权限 ----------------

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        bool enable = AutoStartChk.IsChecked == true;
        if (!AutoStart.Set(enable))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "写入开机启动项失败。可以手动把程序快捷方式放进「启动」文件夹：\n" +
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutoStartChk.IsChecked = !enable;
            return;
        }

        AppSettings.Current.AutoStart = AutoStart.IsEnabled();
        AppSettings.Save();
        UpdateUi();
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.RunAsAdmin = AdminChk.IsChecked == true;
        AppSettings.Save();
        UpdateUi();
    }

    // ---------------- 电量监控 ----------------

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.BatteryMonitor = MonitorChk.IsChecked == true;
        AppSettings.Save();
        BatteryMonitor.Apply();
        UpdateUi();
    }

    private void Alert_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.LowBatteryAlert = AlertChk.IsChecked == true;
        AppSettings.Save();
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            AppSettings.Current.BatteryIntervalMinutes = minutes;
            AppSettings.Save();
            BatteryMonitor.Apply();
            UpdateUi();
        }
    }

    private void Threshold_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThresholdBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int percent))
        {
            AppSettings.Current.LowBatteryThreshold = percent;
            AppSettings.Save();
        }
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        LastCheckText.Text = "正在检查…";
        BatteryMonitor.CheckNow();

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var devices = BatteryMonitor.LastDevices;
            int readable = devices.Count(d => d.Level.HasValue);
            LastCheckText.Text = BatteryMonitor.LastCheck is { } t
                ? $"上次检查：{t:HH:mm:ss} · 可读 {readable}/{devices.Count}"
                : "尚未检查";
        };
        timer.Start();
    }

    // ---------------- 目录 ----------------

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e)
        => Shell.OpenFolder(AppContext.BaseDirectory);

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppSettings.FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                Shell.OpenFolder(dir);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "打开配置目录失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
