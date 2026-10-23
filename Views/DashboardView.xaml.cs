using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class DashboardView : UserControl, IAppPage
{
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private bool _loaded;
    private bool _slowLoaded;

    private static string _osName = "";
    private static string _cpuName = "";
    private static string _gpuName = "";
    private static string _boardName = "";
    private static int _services;
    private static int _usbDevices;

    public DashboardView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        ThemeManager.Changed += OnThemeChanged;
        UpdateThemeText();
    }

    public void OnActivated()
    {
        _timer.Start();
        if (!_loaded) _ = RefreshAsync();
        UpdateThemeText();
    }

    public void OnDeactivated() => _timer.Stop();

    public void OnClosed()
    {
        _timer.Stop();
        ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateThemeText); return; }
        UpdateThemeText();
    }

    private void UpdateThemeText()
        => ThemeText.Text = ThemeManager.Mode == AppThemeMode.System
            ? "跟随系统"
            : ThemeManager.Mode == AppThemeMode.Dark ? "深色" : "浅色";

    // ---------------- 数据 ----------------

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            var fast = await Task.Run(GatherFast);
            ApplyFast(fast);

            if (!_slowLoaded)
            {
                _slowLoaded = true;
                var slow = await Task.Run(GatherSlow);
                ApplySlow(slow);
            }

            _loaded = true;
        }
        catch { }
        finally { _busy = false; }
    }

    private sealed record FastData(
        uint CpuLoad, ulong MemTotal, ulong MemAvail, int ProcCount, string TopProc,
        List<(string Name, long Free, long Total)> Disks,
        string NetName, string NetIp, string NetDns);

    private static FastData GatherFast()
    {
        uint cpu = 0;
        try
        {
            cpu = Wmi.Query("SELECT LoadPercentage FROM Win32_Processor", m => Wmi.U32(m, "LoadPercentage"))
                .FirstOrDefault();
        }
        catch { }

        var (total, avail) = Native.GetMemory();

        int procCount = 0;
        string topProc = "";
        try
        {
            var procs = Process.GetProcesses();
            procCount = procs.Length;
            var top = procs
                .Select(p => { try { return (Name: p.ProcessName, Mem: p.WorkingSet64); } catch { return (Name: "", Mem: 0L); } })
                .Where(x => x.Mem > 0)
                .OrderByDescending(x => x.Mem)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(top.Name))
                topProc = $"占用最高：{top.Name}  {Fmt.Size(top.Mem)}";
            foreach (var p in procs) p.Dispose();
        }
        catch { }

        var disks = new List<(string, long, long)>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.TotalSize <= 0) continue;
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    disks.Add(($"{d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), d.AvailableFreeSpace, d.TotalSize));
                }
                catch { }
            }
        }
        catch { }

        string netName = "", netIp = "", netDns = "";
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => !IsVirtual(n))
                .ToList();

            var ni = candidates
                .OrderByDescending(n => { try { return n.GetIPProperties().GatewayAddresses.Count > 0; } catch { return false; } })
                .ThenByDescending(n => n.Speed)
                .FirstOrDefault();

            if (ni != null)
            {
                var props = ni.GetIPProperties();
                netName = ni.Name;
                netIp = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault() ?? "无 IPv4";
                netDns = string.Join(", ", props.DnsAddresses.Select(a => a.ToString()).Take(2));
            }
        }
        catch { }

        return new FastData(cpu, total, avail, procCount, topProc, disks, netName, netIp, netDns);
    }

    private static bool IsVirtual(NetworkInterface ni)
    {
        string s = (ni.Name + " " + ni.Description).ToLowerInvariant();
        string[] bad = { "filter", "scheduler", "wan miniport", "npcap", "lightweight", "teredo", "isatap", "6to4", "bluetooth" };
        return bad.Any(k => s.Contains(k));
    }

    private sealed record SlowData(string Os, string Cpu, string Gpu, string Board, int Services, int Usb);

    private static SlowData GatherSlow()
    {
        string os = _osName, cpu = _cpuName, gpu = _gpuName, board = _boardName;

        if (string.IsNullOrEmpty(os))
        {
            os = Wmi.Query("SELECT Caption FROM Win32_OperatingSystem", m => Wmi.S(m, "Caption")).FirstOrDefault() ?? "";
            cpu = Wmi.Query("SELECT Name FROM Win32_Processor", m => Wmi.S(m, "Name")).FirstOrDefault() ?? "";
            gpu = Wmi.Query("SELECT Name FROM Win32_VideoController", m => Wmi.S(m, "Name")).FirstOrDefault() ?? "";
            var maker = Wmi.Query("SELECT Manufacturer,Product FROM Win32_BaseBoard",
                m => $"{Wmi.S(m, "Manufacturer")} {Wmi.S(m, "Product")}".Trim()).FirstOrDefault() ?? "";
            board = maker;

            _osName = os; _cpuName = cpu; _gpuName = gpu; _boardName = board;
        }

        _services = Wmi.Query("SELECT State FROM Win32_Service", m => Wmi.S(m, "State"))
            .Count(s => s == "Running");
        _usbDevices = Wmi.Query("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'", _ => 1).Count;

        return new SlowData(os, cpu, gpu, board, _services, _usbDevices);
    }

    private void ApplyFast(FastData d)
    {
        CpuBar.Value = Math.Clamp(d.CpuLoad, 0, 100);
        CpuText.Text = d.CpuLoad + "%";

        double memPct = d.MemTotal > 0 ? (d.MemTotal - d.MemAvail) * 100.0 / d.MemTotal : 0;
        MemBar.Value = Math.Clamp(memPct, 0, 100);
        MemText.Text = $"{memPct:0}%";

        ProcCountText.Text = d.ProcCount.ToString();
        TopProcText.Text = d.TopProc;

        var span = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = "已运行 " + Fmt.Uptime(span);

        DiskPanel.Children.Clear();
        long free = d.Disks.Sum(x => x.Free);
        long total = d.Disks.Sum(x => x.Total);
        DiskFreeText.Text = d.Disks.Count > 0 ? $"可用 {Fmt.Size(free)} / {Fmt.Size(total)}" : "";

        foreach (var disk in d.Disks.Take(3))
        {
            double usedPct = disk.Total > 0 ? (disk.Total - disk.Free) * 100.0 / disk.Total : 0;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

            var name = new TextBlock
            {
                Text = disk.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var bar = new ProgressBar
            {
                Height = 7, Maximum = 100, Value = Math.Clamp(usedPct, 0, 100),
                Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = usedPct > 90 ? (Brush)FindResource("DangerBrush")
                    : usedPct > 75 ? (Brush)FindResource("WarnBrush")
                    : (Brush)FindResource("AccentBrush")
            };
            var info = new TextBlock
            {
                Text = $"{Fmt.Size(disk.Free)} / {Fmt.Size(disk.Total)}", FontSize = 11.5,
                Foreground = (Brush)FindResource("TextDimBrush"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(info, 2);
            row.Children.Add(name);
            row.Children.Add(bar);
            row.Children.Add(info);
            DiskPanel.Children.Add(row);
        }

        NetNameText.Text = string.IsNullOrEmpty(d.NetName) ? "未连接" : d.NetName;
        NetIpText.Text = string.IsNullOrEmpty(d.NetIp) ? "—" : "IP  " + d.NetIp;
        NetDnsText.Text = string.IsNullOrEmpty(d.NetDns) ? "" : "DNS  " + d.NetDns;
    }

    private void ApplySlow(SlowData d)
    {
        OsText.Text = string.IsNullOrWhiteSpace(d.Os) ? "—" : d.Os;
        CpuModelText.Text = string.IsNullOrWhiteSpace(d.Cpu) ? "" : d.Cpu;
        GpuText.Text = string.IsNullOrWhiteSpace(d.Gpu) ? "" : "显卡  " + d.Gpu;
        BoardText.Text = string.IsNullOrWhiteSpace(d.Board) ? "" : "主板  " + d.Board;
        ServiceCountText.Text = d.Services.ToString();
        UsbCountText.Text = d.Usb.ToString();
    }

    // ---------------- 跳转 ----------------

    private void GoSys_Click(object sender, RoutedEventArgs e) => Navigate("sys");

    private void GoClean_Click(object sender, RoutedEventArgs e) => Navigate("clean");

    private void GoProcess_Click(object sender, RoutedEventArgs e) => Navigate("process");

    private void GoService_Click(object sender, RoutedEventArgs e) => Navigate("service");

    private void GoNetwork_Click(object sender, RoutedEventArgs e) => Navigate("network");

    private void GoUsb_Click(object sender, RoutedEventArgs e) => Navigate("usb");

    private void GoSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings");

    private void Navigate(string tag)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo(tag);

    // ---------------- 快捷操作 ----------------

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        var main = Window.GetWindow(this) as MainWindow;
        main?.SetStatus("正在刷新 DNS 缓存…");
        var r = await Shell.RunAsync("ipconfig.exe", "/flushdns", 15000);
        main?.SetStatus(r.ExitCode == 0 ? "DNS 缓存已刷新" : "刷新 DNS 失败：" + r.Output.Trim());
    }

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
    {
        Shell.OpenFolder(System.IO.Path.GetTempPath());
        (Window.GetWindow(this) as MainWindow)?.SetStatus("已打开临时目录");
    }

    private void OpenUsbTreeView_Click(object sender, RoutedEventArgs e)
    {
        string exe = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "UsbTreeView.exe");
        if (!System.IO.File.Exists(exe))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "未找到 UsbTreeView.exe，请放到：\n" + System.IO.Path.Combine(AppContext.BaseDirectory, "tools"),
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "启动 UsbTreeView 失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
        catch { }
    }
}
