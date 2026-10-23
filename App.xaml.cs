using System.IO;
using System.Windows;
using WinToolbox.Core;
using WinToolbox.Views;

namespace WinToolbox;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 开发者自检开关：--openfolder <目录> / --revealfile <文件>
        if (e.Args.Length >= 2 && e.Args[0].Equals("--openfolder", StringComparison.OrdinalIgnoreCase))
        {
            string target = e.Args[1];
            bool ok = Native.OpenFolderShell(target);
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "wintoolbox-openfolder.log");
                File.WriteAllText(log, $"target={target}\nsh-api={ok}\nexists={Directory.Exists(target)}\n");
            }
            catch { }
            Environment.Exit(ok ? 0 : 2);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0].Equals("--revealfile", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(Native.RevealFileShell(e.Args[1]) ? 0 : 2);
            return;
        }

        // 开发者自检开关：--set-autostart on|off
        if (e.Args.Length >= 2 && e.Args[0].Equals("--set-autostart", StringComparison.OrdinalIgnoreCase))
        {
            AppSettings.Load();
            bool on = e.Args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
            bool ok = AutoStart.Set(on);
            AppSettings.Current.AutoStart = AutoStart.IsEnabled();
            AppSettings.Save();
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "wintoolbox-autostart.log");
                File.WriteAllText(log, $"requested={on}\nregistry={AutoStart.IsEnabled()}\nok={ok}\nexe={AutoStart.ExePath}\n");
            }
            catch { }
            Environment.Exit(ok ? 0 : 2);
            return;
        }

        // 启动时自动提权（设置里开启后生效；用户取消 UAC 则继续以普通权限运行）
        AppSettings.Load();
        if (AppSettings.Current.RunAsAdmin && !Native.IsAdmin()
            && !e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            if (Native.RestartElevated())
            {
                Shutdown();
                return;
            }
        }

        // 单实例：重复双击时把已运行窗口提到前台，而不是再开一个
        _singleInstance = new Mutex(true, @"Local\WinToolbox.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            var handle = Native.FindWindowByTitle("WinToolbox · 电脑工具箱");
            if (handle != IntPtr.Zero) Native.ActivateWindow(handle);
            Shutdown();
            return;
        }

        ThemeManager.Initialize();

        BatteryMonitor.LowBattery += ShowBatteryToast;
        BatteryMonitor.Apply();

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "程序遇到一个未处理的错误：\n\n" + args.Exception.Message,
                "WinToolbox",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                try
                {
                    MessageBox.Show("程序遇到一个未处理的错误：\n\n" + ex.Message, "WinToolbox",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
        };
    }

    private static void ShowBatteryToast(List<PeripheralBattery> devices)
    {
        try
        {
            string body = "以下设备电量偏低：\n" +
                          string.Join("\n", devices.Select(d => $"· {d.Name}：{d.Level}%"));
            ToastWindow.Show(body);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BatteryMonitor.Stop();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
