using System.Windows;
using System.Windows.Controls;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class BatteryView : UserControl, IAppPage
{
    private bool _busy;
    private bool _loaded;

    public BatteryView()
    {
        InitializeComponent();
    }

    public void OnActivated()
    {
        if (!_loaded) _ = LoadAsync();
    }

    public void OnDeactivated() { }

    public void OnClosed() { }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshBtn.IsEnabled = false;
        SummaryText.Text = "正在查询设备电量…";

        try
        {
            var (devices, sys) = await Task.Run(() =>
            {
                var list = BatteryReader.Scan();
                var battery = BatteryReader.SystemBattery();
                return (list, battery);
            });

            var (hasBattery, percent, charging, note) = sys;
            if (hasBattery)
            {
                SystemBatteryText.Text = $"{percent}%" + (charging ? " · 充电中" : "");
                SystemBar.Value = Math.Clamp(percent, 0, 100);
                SystemBatteryNote.Text = charging ? "已接入电源" : "使用电池供电";
            }
            else
            {
                SystemBatteryText.Text = "—";
                SystemBar.Value = 0;
                SystemBatteryNote.Text = note;
            }

            DeviceList.ItemsSource = devices;

            int readable = devices.Count(d => d.Level.HasValue);
            SummaryText.Text = devices.Count == 0
                ? "未发现可查询的外设"
                : $"外设 {devices.Count} 个 · 可读电量 {readable} 个";

            EmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (devices.Count == 0)
                EmptyText.Text = "没有检测到带 Feature Report 的 HID 外设。";

            (Window.GetWindow(this) as MainWindow)?.SetStatus("电量信息已更新");
            _loaded = true;
        }
        catch (Exception ex)
        {
            SummaryText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshBtn.IsEnabled = true;
        }
    }
}
