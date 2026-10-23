using System.Runtime.InteropServices;
using System.Text;

namespace WinToolbox.Core;

/// <summary>一个外设的电量信息。</summary>
public sealed class PeripheralBattery
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Kind { get; init; } = "";
    public int? Level { get; init; }
    public bool Charging { get; init; }
    public string Status { get; init; } = "";
    public string Note { get; init; } = "";
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }

    public int LevelValue => Level ?? 0;
    public string LevelText => Level.HasValue ? Level.Value + "%" : "—";
}

/// <summary>
/// 读取外设电量：系统电池（Win32）+ 雷蛇 HID 协议（Feature Report 0x00 / class 0x07 / cmd 0x80）。
/// 罗技等其它厂商协议不同，未实现；未知设备只列出并标注。
/// </summary>
public static class BatteryReader
{
    // ---------------- Win32 / HID ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUID
    {
        public uint Data1;
        public ushort Data2, Data3;
        public byte B0, B1, B2, B3, B4, B5, B6, B7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public GUID InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] Reserved2;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref GUID g, IntPtr e, IntPtr h, int f);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr i, ref GUID g, int idx,
        ref SP_DEVICE_INTERFACE_DATA d);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d,
        IntPtr buf, int size, out int needed, IntPtr info);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint creat,
        uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr p);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr h, StringBuilder b, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetFeature(IntPtr h, byte[] b, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetFeature(IntPtr h, byte[] b, int len);

    private static GUID HidGuid = new()
    {
        Data1 = 0x4D1E55B2, Data2 = 0xF16F, Data3 = 0x11CF,
        B0 = 0x88, B1 = 0xCB, B2 = 0x00, B3 = 0x11, B4 = 0x11, B5 = 0x00, B6 = 0x00, B7 = 0x30
    };

    private static readonly IntPtr InvalidHandle = new(-1);

    private sealed class HidInterface
    {
        public string Path = "";
        public ushort Vid;
        public ushort Pid;
        public ushort Usage;
        public ushort UsagePage;
        public ushort FeatureLength;
        public string Product = "";
    }

    private static List<HidInterface> EnumerateHid()
    {
        var list = new List<HidInterface>();
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == IntPtr.Zero || set == InvalidHandle) return list;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref did); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;

                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out _, IntPtr.Zero)) continue;

                    string path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4)) ?? "";
                    IntPtr h = CreateFile(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h == InvalidHandle) h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h == InvalidHandle) continue;

                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        HidD_GetAttributes(h, ref attr);

                        var name = new StringBuilder(256);
                        HidD_GetProductString(h, name, 256);

                        var iface = new HidInterface
                        {
                            Path = path, Vid = attr.VendorID, Pid = attr.ProductID,
                            Product = name.ToString()
                        };

                        if (HidD_GetPreparsedData(h, out IntPtr pre))
                        {
                            var caps = new HIDP_CAPS
                            {
                                Reserved = new ushort[17], Reserved2 = new ushort[10]
                            };
                            if (HidP_GetCaps(pre, ref caps) == 0x110000)
                            {
                                iface.Usage = caps.Usage;
                                iface.UsagePage = caps.UsagePage;
                                iface.FeatureLength = caps.FeatureReportByteLength;
                            }
                            HidD_FreePreparsedData(pre);
                        }

                        list.Add(iface);
                    }
                    finally { CloseHandle(h); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return list;
    }

    // ---------------- 对外接口 ----------------

    public static (bool HasBattery, int Percent, bool Charging, string Note) SystemBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return (false, -1, false, "无法读取");
        if (s.BatteryFlag == 128 || s.BatteryLifePercent == 255)
            return (false, -1, false, "本机没有电池（台式机）");

        return (true, s.BatteryLifePercent, s.ACLineStatus == 1, "");
    }

    /// <summary>扫描一次所有可查询的外设。</summary>
    public static List<PeripheralBattery> Scan()
    {
        var result = new List<PeripheralBattery>();
        var interfaces = EnumerateHid();

        // 每个设备只取一个"控制接口"来查询：优先有 91 字节 Feature Report 的雷蛇接口
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var iface in interfaces)
        {
            if (iface.FeatureLength == 0) continue;

            string key = $"{iface.Vid:X4}:{iface.Pid:X4}";
            if (!seen.Add(key)) continue;

            string name = string.IsNullOrWhiteSpace(iface.Product) ? $"HID 设备 {key}" : iface.Product;
            string vendor = VendorName(iface.Vid);
            string kind = KindName(iface.UsagePage, iface.Usage);

            if (iface.Vid == 0x1532)
            {
                var (ok, level, charging, note) = QueryRazer(iface);
                result.Add(new PeripheralBattery
                {
                    Name = name, Vendor = vendor, Kind = kind, Vid = iface.Vid, Pid = iface.Pid,
                    Level = ok ? level : null,
                    Charging = charging,
                    Status = ok ? "已读取" : note,
                    Note = ok ? "" : RazerHint(note)
                });
            }
            else
            {
                result.Add(new PeripheralBattery
                {
                    Name = name, Vendor = vendor, Kind = kind, Vid = iface.Vid, Pid = iface.Pid,
                    Status = "不支持",
                    Note = "该厂商使用私有协议，未提供标准电量查询"
                });
            }
        }

        return result
            .OrderByDescending(p => p.Level.HasValue)
            .ThenBy(p => p.Vendor)
            .ToList();
    }

    private static string RazerHint(string status) => status switch
    {
        "0x03" => "设备拒绝查询：可能处于休眠或未配对，移动鼠标后点「刷新」重试",
        "0x05" => "该设备不支持电量查询",
        "timeout" => "设备无响应（可能已休眠）",
        _ => "未能读取电量"
    };

    /// <summary>雷蛇报文校验：XOR frame[2..87]，结果写 frame[88]。</summary>
    private static byte RazerChecksum(byte[] frame)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= frame[i];
        return crc;
    }

    /// <summary>
    /// 雷蛇 HID 协议：90 字节帧 + XOR 校验，经 Feature Report(91 字节，含 report id 0x00) 收发。
    /// 电量：class 0x07 / cmd 0x80，返回 frame[9] = 0..255（255 = 100%），frame[10] = 充电标志。
    /// </summary>
    private static (bool Ok, int Level, bool Charging, string Note) QueryRazer(HidInterface iface)
    {
        if (iface.FeatureLength < 91)
            return (false, -1, false, "接口不支持 91 字节 Feature Report");

        IntPtr h = CreateFile(iface.Path, 0xC0000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == InvalidHandle) h = CreateFile(iface.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == InvalidHandle) return (false, -1, false, "无法打开设备");

        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[] frame = new byte[90];
                frame[0] = 0x00;                    // status
                frame[1] = (byte)(attempt % 31);    // transaction id：0..30 循环
                frame[5] = 0x02;                    // data size
                frame[6] = 0x07;                    // command class: power
                frame[7] = 0x80;                    // command: get battery
                frame[8] = 0x00;
                frame[88] = RazerChecksum(frame);

                byte[] buf = new byte[91];
                Array.Copy(frame, 0, buf, 1, 90);

                if (!HidD_SetFeature(h, buf, buf.Length))
                {
                    Thread.Sleep(120);
                    continue;
                }

                Thread.Sleep(80);

                byte[] resp = new byte[91];
                if (!HidD_GetFeature(h, resp, resp.Length))
                {
                    Thread.Sleep(150);
                    continue;
                }

                byte status = resp[1];
                if (status == 0x02)
                {
                    int raw = resp[10];
                    int level = (int)Math.Round(raw * 100.0 / 255);
                    return (true, Math.Clamp(level, 0, 100), resp[11] == 0x01, "");
                }

                if (status == 0x05) return (false, -1, false, "0x05");

                Thread.Sleep(140);
            }

            return (false, -1, false, "0x03");
        }
        catch (Exception)
        {
            return (false, -1, false, "timeout");
        }
        finally { CloseHandle(h); }
    }

    private static string VendorName(ushort vid) => vid switch
    {
        0x1532 => "Razer",
        0x046D => "Logitech",
        0x0B05 => "ASUS",
        0x1B1C => "Corsair",
        0x0951 => "Kingston",
        0x1038 => "SteelSeries",
        _ => $"VID {vid:X4}"
    };

    private static string KindName(ushort page, ushort usage)
    {
        if (page == 0x0001 && usage == 0x0002) return "鼠标";
        if (page == 0x0001 && usage == 0x0006) return "键盘";
        if (page == 0x0001 && usage == 0x0080) return "系统控制";
        if (page == 0x000C && usage == 0x0001) return "多媒体";
        if (page == 0x0001 && usage == 0x0000) return "输入设备";
        return $"HID {page:X4}/{usage:X4}";
    }
}
