using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class SystemInfoView : UserControl, IAppPage
{
    private bool _loaded;
    private bool _busy;
    private string _dump = "";

    public SystemInfoView()
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
        Busy.Visibility = Visibility.Visible;
        RefreshLabel.Text = "正在读取系统信息…";

        try
        {
            var snap = await Task.Run(Gather);
            Render(snap.Os, snap.Cpu, snap.Mem, snap.Modules, snap.Gpus, snap.Board, snap.Drives, snap.Disks, snap.Nets);
            RefreshLabel.Text = $"最后刷新：{DateTime.Now:HH:mm:ss}";
            (Window.GetWindow(this) as MainWindow)?.SetStatus("系统信息已刷新");
            _loaded = true;
        }
        catch (Exception ex)
        {
            RefreshLabel.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- 采集 ----------------

    private static OsSnapshot GatherOs()
    {
        var os = Wmi.Query("SELECT Caption,Version,BuildNumber,InstallDate,LastBootUpTime,CSName,RegisteredUser FROM Win32_OperatingSystem",
            m => new OsSnapshot(
                Wmi.S(m, "Caption"), Wmi.S(m, "Version"), Wmi.S(m, "BuildNumber"),
                Wmi.D(m, "InstallDate"), Wmi.D(m, "LastBootUpTime"),
                Wmi.S(m, "CSName"), Wmi.S(m, "RegisteredUser")))
            .FirstOrDefault();

        var cs = Wmi.Query("SELECT Manufacturer,Model,UserName,Domain,TotalPhysicalMemory FROM Win32_ComputerSystem",
            m => new { Maker = Wmi.S(m, "Manufacturer"), Model = Wmi.S(m, "Model"), User = Wmi.S(m, "UserName"), Domain = Wmi.S(m, "Domain") })
            .FirstOrDefault();

        os ??= new OsSnapshot(Environment.OSVersion.VersionString, Environment.OSVersion.Version.ToString(),
            Environment.OSVersion.Version.Build.ToString(), null, null, Environment.MachineName, Environment.UserName);

        return os with
        {
            Machine = string.IsNullOrWhiteSpace(os.Machine) ? Environment.MachineName : os.Machine,
            User = cs?.User is { Length: > 0 } u ? u : Environment.UserName,
            Domain = cs?.Domain ?? "",
            Model = string.Join(" ", new[] { cs?.Maker, cs?.Model }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };
    }

    private static CpuSnapshot GatherCpu()
    {
        var c = Wmi.Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,LoadPercentage FROM Win32_Processor",
            m => new CpuSnapshot(Wmi.S(m, "Name"), Wmi.U32(m, "NumberOfCores"), Wmi.U32(m, "NumberOfLogicalProcessors"),
                Wmi.U32(m, "MaxClockSpeed"), Wmi.U32(m, "LoadPercentage"))).FirstOrDefault();

        if (c is null)
        {
            string name = "";
            try
            {
                name = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", "") as string ?? "";
            }
            catch { }
            c = new CpuSnapshot(name, (uint)Environment.ProcessorCount, (uint)Environment.ProcessorCount, 0, 0);
        }
        return c;
    }

    private static MemorySnapshot GatherMemory()
    {
        var (total, avail) = Native.GetMemory();
        if (total == 0)
        {
            total = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", m => Wmi.U64(m, "TotalPhysicalMemory"))
                .FirstOrDefault();
            avail = 0;
        }
        return new MemorySnapshot(total, avail);
    }

    private static List<RamModule> GatherRamModules()
        => Wmi.Query("SELECT Capacity,Speed,Manufacturer,PartNumber,ConfiguredClockSpeed FROM Win32_PhysicalMemory",
            m => new RamModule(Wmi.U64(m, "Capacity"), Wmi.U32(m, "Speed"), Wmi.S(m, "Manufacturer"),
                Wmi.S(m, "PartNumber"), Wmi.U32(m, "ConfiguredClockSpeed")));

    private static List<GpuSnapshot> GatherGpus()
        => Wmi.Query("SELECT Name,DriverVersion,AdapterRAM FROM Win32_VideoController",
            m => new GpuSnapshot(Wmi.S(m, "Name"), Wmi.S(m, "DriverVersion"), Wmi.U64(m, "AdapterRAM")));

    private static BoardSnapshot GatherBoard()
    {
        var bb = Wmi.Query("SELECT Manufacturer,Product FROM Win32_BaseBoard",
            m => new { Maker = Wmi.S(m, "Manufacturer"), Product = Wmi.S(m, "Product") }).FirstOrDefault();
        var bios = Wmi.Query("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS",
            m => new { Maker = Wmi.S(m, "Manufacturer"), Ver = Wmi.S(m, "SMBIOSBIOSVersion"), Date = Wmi.D(m, "ReleaseDate") }).FirstOrDefault();
        return new BoardSnapshot(bb?.Maker ?? "-", bb?.Product ?? "-", bios?.Maker ?? "-", bios?.Ver ?? "-", bios?.Date);
    }

    private static (List<DriveSnapshot>, List<DiskSnapshot>) GatherDisks()
    {
        var disks = Wmi.Query("SELECT Model,Size,InterfaceType,MediaType FROM Win32_DiskDrive",
            m => new DiskSnapshot(Wmi.S(m, "Model"), Wmi.U64(m, "Size"), Wmi.S(m, "InterfaceType"), Wmi.S(m, "MediaType")));

        var drives = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives().OrderBy(x => x.Name))
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.NoRootDirectory or DriveType.Unknown) continue;
                if (d.TotalSize <= 0) continue;
                drives.Add(new DriveSnapshot(d.Name.TrimEnd('\\'), d.VolumeLabel, d.DriveFormat,
                    d.TotalSize, d.AvailableFreeSpace, d.DriveType.ToString()));
            }
            catch { }
        }
        return (drives, disks);
    }

    private static bool IsFilterAdapter(NetworkInterface ni)
    {
        string s = (ni.Name + " " + ni.Description).ToLowerInvariant();
        string[] bad =
        {
            "filter", "scheduler", "wan miniport", "npcap", "lightweight",
            "teredo", "isatap", "6to4", "qos packet", "bluetooth"
        };
        return bad.Any(k => s.Contains(k));
    }

    private static List<NetSnapshot> GatherNetwork()
    {
        var list = new List<NetSnapshot>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (IsFilterAdapter(ni)) continue;

                var ip = ni.GetIPProperties();
                var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToList();
                var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.ToString()).ToList();
                if (v4.Count == 0 && ni.OperationalStatus != OperationalStatus.Up) continue;

                string mac = "";
                try { mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); } catch { }

                string gw = ip.GatewayAddresses.Select(g => g.Address.ToString()).FirstOrDefault() ?? "";
                string dns = string.Join(", ", ip.DnsAddresses.Select(a => a.ToString()));
                string type = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "以太网",
                    NetworkInterfaceType.Wireless80211 => "无线",
                    NetworkInterfaceType.Ppp => "拨号/VPN",
                    _ => ni.NetworkInterfaceType.ToString()
                };

                list.Add(new NetSnapshot(ni.Name, ni.Description, type,
                    ni.OperationalStatus == OperationalStatus.Up ? "已连接" : ni.OperationalStatus.ToString(),
                    ni.Speed, string.Join(", ", v4), string.Join(", ", v6), mac, gw, dns));
            }
        }
        catch { }
        return list;
    }

    private static Snapshot Gather()
    {
        var os = GatherOs();
        var cpu = GatherCpu();
        var mem = GatherMemory();
        var modules = GatherRamModules();
        var gpus = GatherGpus();
        var board = GatherBoard();
        var (drives, disks) = GatherDisks();
        var nets = GatherNetwork();
        return new Snapshot(os, cpu, mem, modules, gpus, board, drives, disks, nets);
    }

    // ---------------- 渲染 ----------------

    private void Render(OsSnapshot os, CpuSnapshot cpu, MemorySnapshot mem, List<RamModule> modules,
        List<GpuSnapshot> gpus, BoardSnapshot board, List<DriveSnapshot> drives, List<DiskSnapshot> disks,
        List<NetSnapshot> nets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 设备 ==");
        sb.AppendLine($"计算机名：{os.Machine}");
        sb.AppendLine($"当前用户：{os.User}");
        sb.AppendLine($"系统：{os.Caption}");
        sb.AppendLine($"版本：{os.Version} (Build {os.Build})");
        sb.AppendLine($"安装时间：{Fmt.DateTime(os.Installed)}");
        sb.AppendLine($"上次启动：{Fmt.DateTime(os.Boot)}");
        sb.AppendLine($"型号：{os.Model}");

        OsTag.Text = $"Build {os.Build}";
        DeviceLeft.Children.Clear();
        DeviceRight.Children.Clear();
        AddRow(DeviceLeft, "计算机名", string.IsNullOrWhiteSpace(os.Machine) ? Environment.MachineName : os.Machine);
        AddRow(DeviceLeft, "当前用户", string.IsNullOrWhiteSpace(os.User) ? Environment.UserName : os.User);
        AddRow(DeviceLeft, "操作系统", Fmt.Nz(os.Caption));
        AddRow(DeviceLeft, "系统版本", $"{os.Version} (Build {os.Build})");
        AddRow(DeviceLeft, "系统架构", Environment.Is64BitOperatingSystem ? "64 位" : "32 位");
        AddRow(DeviceRight, "安装时间", Fmt.DateTime(os.Installed));
        AddRow(DeviceRight, "上次启动", Fmt.DateTime(os.Boot));
        AddRow(DeviceRight, "运行时长", os.Boot.HasValue ? Fmt.Uptime(DateTime.Now - os.Boot.Value) : "-");
        AddRow(DeviceRight, "设备型号", string.IsNullOrWhiteSpace(os.Model) ? "-" : os.Model);
        AddRow(DeviceRight, "处理器核心", $"{cpu.Cores} 核 / {cpu.Threads} 线程");

        // CPU
        CpuPanel.Children.Clear();
        AddRow(CpuPanel, "型号", Fmt.Nz(cpu.Name));
        AddRow(CpuPanel, "核心 / 线程", $"{cpu.Cores} 核 / {cpu.Threads} 线程");
        AddRow(CpuPanel, "基准频率", cpu.MaxClock > 0 ? $"{cpu.MaxClock} MHz" : "-");
        AddRow(CpuPanel, "当前负载", $"{cpu.Load}%");
        sb.AppendLine().AppendLine("== 处理器 ==");
        sb.AppendLine($"型号：{cpu.Name}");
        sb.AppendLine($"核心/线程：{cpu.Cores}/{cpu.Threads}");
        sb.AppendLine($"频率：{cpu.MaxClock} MHz，负载：{cpu.Load}%");

        // 内存
        MemPanel.Children.Clear();
        double usedBytes = mem.Total > mem.Available ? mem.Total - mem.Available : 0;
        double pct = mem.Total > 0 ? usedBytes * 100.0 / mem.Total : 0;
        MemPercent.Text = $"{pct:0}%";
        MemBar.Value = Math.Clamp(pct, 0, 100);
        AddRow(MemPanel, "总容量", Fmt.Size(mem.Total));
        AddRow(MemPanel, "已使用", $"{Fmt.Size((ulong)usedBytes)}（{pct:0}%）");
        AddRow(MemPanel, "可用", Fmt.Size(mem.Available));
        AddRow(MemPanel, "物理内存条", modules.Count > 0 ? $"{modules.Count} 条" : "-");
        sb.AppendLine().AppendLine("== 内存 ==");
        sb.AppendLine($"总量：{Fmt.Size(mem.Total)}，已用：{Fmt.Size((ulong)usedBytes)}（{pct:0}%）");
        foreach (var m in modules)
            sb.AppendLine($"  {Fmt.Size(m.Capacity)} {m.Speed}MHz {m.Manufacturer} {m.PartNumber}".TrimEnd());

        // 显卡
        GpuPanel.Children.Clear();
        if (gpus.Count == 0) AddRow(GpuPanel, "显卡", "未检测到");
        for (int i = 0; i < Math.Min(gpus.Count, 3); i++)
        {
            var g = gpus[i];
            if (i > 0) AddRow(GpuPanel, " ", "");
            AddRow(GpuPanel, i == 0 ? "型号" : $"型号 {i + 1}", Fmt.Nz(g.Name));
            AddRow(GpuPanel, "显存", g.Vram > 0 ? Fmt.Size(g.Vram) : "-");
            AddRow(GpuPanel, "驱动版本", Fmt.Nz(g.Driver));
        }
        sb.AppendLine().AppendLine("== 显卡 ==");
        foreach (var g in gpus) sb.AppendLine($"{g.Name} | 显存 {Fmt.Size(g.Vram)} | 驱动 {g.Driver}");

        // 主板 / BIOS
        BoardPanel.Children.Clear();
        AddRow(BoardPanel, "主板厂商", Fmt.Nz(board.BoardMaker));
        AddRow(BoardPanel, "主板型号", Fmt.Nz(board.BoardProduct));
        AddRow(BoardPanel, "BIOS 厂商", Fmt.Nz(board.BiosMaker));
        AddRow(BoardPanel, "BIOS 版本", Fmt.Nz(board.BiosVersion));
        AddRow(BoardPanel, "BIOS 日期", Fmt.DateTime(board.BiosDate));
        sb.AppendLine().AppendLine("== 主板 ==");
        sb.AppendLine($"{board.BoardMaker} {board.BoardProduct} | BIOS {board.BiosVersion} {Fmt.DateTime(board.BiosDate)}");

        // 磁盘
        DiskPanel.Children.Clear();
        long totalAll = drives.Sum(d => d.Total);
        long freeAll = drives.Sum(d => d.Free);
        DiskSummary.Text = drives.Count > 0
            ? $"{drives.Count} 个分区 · 可用 {Fmt.Size(freeAll)} / {Fmt.Size(totalAll)}"
            : "";

        if (disks.Count > 0)
        {
            foreach (var disk in disks.Take(6))
            {
                DiskPanel.Children.Add(new TextBlock
                {
                    Text = $"物理磁盘：{disk.Model} · {Fmt.Size(disk.Size)} · {disk.Interface}",
                    FontSize = 12, Foreground = DimBrush, Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        sb.AppendLine().AppendLine("== 磁盘 ==");
        foreach (var d in drives)
        {
            double used = d.Total - d.Free;
            double dpct = d.Total > 0 ? used * 100.0 / d.Total : 0;
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = $"{d.Name}  {d.Label}  ({d.Format})",
                FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var info = new TextBlock
            {
                Text = $"{Fmt.Size(d.Free)} 可用 / {Fmt.Size(d.Total)}",
                FontSize = 12, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(info, 1);
            head.Children.Add(name);
            head.Children.Add(info);

            var bar = new ProgressBar
            {
                Height = 6, Margin = new Thickness(0, 7, 0, 0), Maximum = 100,
                Value = Math.Clamp(dpct, 0, 100),
                Foreground = dpct > 90 ? DangerBrush : dpct > 75 ? WarnBrush : AccentBrush
            };
            block.Children.Add(head);
            block.Children.Add(bar);
            DiskPanel.Children.Add(block);

            sb.AppendLine($"{d.Name} {d.Label} {d.Format} | 可用 {Fmt.Size(d.Free)} / {Fmt.Size(d.Total)}（{dpct:0}% 已用）");
        }

        // 网络
        NetPanel.Children.Clear();
        sb.AppendLine().AppendLine("== 网络 ==");
        if (nets.Count == 0)
        {
            NetPanel.Children.Add(new TextBlock { Text = "未检测到活动网络适配器", Foreground = DimBrush, FontSize = 13 });
        }
        foreach (var n in nets)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = n.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new Border
            {
                Style = (Style)FindResource("Tag"),
                Background = n.Status == "已连接"
                    ? new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xE6))
                    : Surface2Brush,
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = $"{n.Type} · {n.Status}", FontSize = 11,
                    Foreground = n.Status == "已连接" ? SuccessBrush : DimBrush
                }
            });
            block.Children.Add(head);
            block.Children.Add(new TextBlock
            {
                Text = n.Desc, FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(0, 3, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });
            AddRow(block, "IPv4", Fmt.Nz(n.Ip4));
            AddRow(block, "IPv6", Fmt.Nz(n.Ip6));
            if (!string.IsNullOrEmpty(n.Gateway)) AddRow(block, "默认网关", n.Gateway);
            AddRow(block, "MAC", Fmt.Nz(n.Mac));
            AddRow(block, "链路速率", Fmt.Speed(n.Speed));
            if (!string.IsNullOrEmpty(n.Dns)) AddRow(block, "DNS", n.Dns);
            NetPanel.Children.Add(block);

            sb.AppendLine($"{n.Name} [{n.Type} {n.Status}] {n.Ip4} MAC {n.Mac} 速率 {Fmt.Speed(n.Speed)}");
        }

        _dump = sb.ToString();
    }

    private void AddRow(Panel host, string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock
        {
            Text = key, FontSize = 12, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var v = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value)
                ? (string.IsNullOrWhiteSpace(key) ? "" : "-")
                : value,
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(v, 1);
        g.Children.Add(k);
        g.Children.Add(v);
        host.Children.Add(g);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_dump)) return;
        try
        {
            Clipboard.SetText(_dump);
            (Window.GetWindow(this) as MainWindow)?.SetStatus("系统信息已复制到剪贴板");
        }
        catch { }
    }

    private Brush DimBrush => (Brush)FindResource("TextDimBrush");
    private Brush AccentBrush => (Brush)FindResource("AccentBrush");
    private Brush DangerBrush => (Brush)FindResource("DangerBrush");
    private Brush WarnBrush => (Brush)FindResource("WarnBrush");
    private Brush SuccessBrush => (Brush)FindResource("SuccessBrush");
    private Brush Surface2Brush => (Brush)FindResource("Surface2Brush");

    // ---------------- 数据结构 ----------------

    private sealed record OsSnapshot(string Caption, string Version, string Build, DateTime? Installed, DateTime? Boot,
        string Machine, string User)
    {
        public string Domain { get; init; } = "";
        public string Model { get; init; } = "";
    }

    private sealed record CpuSnapshot(string Name, uint Cores, uint Threads, uint MaxClock, uint Load);
    private sealed record RamModule(ulong Capacity, uint Speed, string Manufacturer, string PartNumber, uint Configured);
    private sealed record GpuSnapshot(string Name, string Driver, ulong Vram);
    private sealed record BoardSnapshot(string BoardMaker, string BoardProduct, string BiosMaker, string BiosVersion, DateTime? BiosDate);
    private sealed record DriveSnapshot(string Name, string Label, string Format, long Total, long Free, string DriveType);
    private sealed record DiskSnapshot(string Model, ulong Size, string Interface, string Media);
    private sealed record NetSnapshot(string Name, string Desc, string Type, string Status, long Speed,
        string Ip4, string Ip6, string Mac, string Gateway, string Dns);

    private sealed record Snapshot(OsSnapshot Os, CpuSnapshot Cpu, MemorySnapshot Mem, List<RamModule> Modules,
        List<GpuSnapshot> Gpus, BoardSnapshot Board, List<DriveSnapshot> Drives, List<DiskSnapshot> Disks,
        List<NetSnapshot> Nets);

    private sealed record MemorySnapshot(ulong Total, ulong Available);
}
