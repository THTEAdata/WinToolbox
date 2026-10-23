using System.Windows.Threading;

namespace WinToolbox.Core;

/// <summary>定时检查外设电量，低电量时触发提醒事件。</summary>
public static class BatteryMonitor
{
    private static DispatcherTimer? _timer;
    private static bool _busy;
    private static readonly Dictionary<string, DateTime> LastAlert = new();

    /// <summary>低电量提醒（参数为需要提醒的设备）。</summary>
    public static event Action<List<PeripheralBattery>>? LowBattery;

    public static DateTime? LastCheck { get; private set; }

    public static List<PeripheralBattery> LastDevices { get; } = new();

    /// <summary>根据设置启动/停止/重设定时器。</summary>
    public static void Apply()
    {
        Stop();

        var s = AppSettings.Current;
        if (!s.BatteryMonitor) return;

        int minutes = Math.Clamp(s.BatteryIntervalMinutes, 1, 240);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _timer.Tick += (_, _) => CheckNow();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>立即检查一次（后台线程读取，回调到 UI 线程）。</summary>
    public static async void CheckNow()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var devices = await Task.Run(BatteryReader.Scan);
            LastCheck = DateTime.Now;
            LastDevices.Clear();
            LastDevices.AddRange(devices);

            var s = AppSettings.Current;
            if (!s.LowBatteryAlert) return;

            var low = devices
                .Where(d => d.Level.HasValue && !d.Charging && d.Level.Value <= s.LowBatteryThreshold)
                .ToList();

            if (low.Count == 0) return;

            var now = DateTime.Now;
            var toNotify = new List<PeripheralBattery>();
            foreach (var device in low)
            {
                string key = device.Name + "|" + device.Level;
                if (LastAlert.TryGetValue(device.Name, out var last) && (now - last) < TimeSpan.FromMinutes(30))
                    continue;
                LastAlert[device.Name] = now;
                toNotify.Add(device);
            }

            if (toNotify.Count > 0) LowBattery?.Invoke(toNotify);
        }
        catch { }
        finally { _busy = false; }
    }
}
