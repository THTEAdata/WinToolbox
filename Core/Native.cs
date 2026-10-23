using System.IO;
using System.Runtime.InteropServices;

namespace WinToolbox.Core;

/// <summary>Win32 互操作封装：内存、回收站、标题栏着色、UAC 提权。</summary>
internal static class Native
{
    // ---------- 物理内存 ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static (ulong Total, ulong Available) GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        return (m.ullTotalPhys, m.ullAvailPhys);
    }

    // ---------- 回收站 ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    public static (long Items, long Bytes) GetRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    public static void EmptyRecycleBin()
        => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    // ---------- 深色标题栏 ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyChrome(IntPtr hwnd, bool dark)
    {
        if (dark)
        {
            TrySet(hwnd, 20, 1);                      // DWMWA_USE_IMMERSIVE_DARK_MODE
            TrySet(hwnd, 35, 0x0017_0F0B);            // 标题栏底色 #0B0F17
            TrySet(hwnd, 36, 0x00F5_ECE7);            // 标题文字 #E7ECF5
            TrySet(hwnd, 34, 0x0040_2D23);            // 边框色 #232D40
        }
        else
        {
            TrySet(hwnd, 20, 0);
            TrySet(hwnd, 35, 0x00F9_F5F3);            // 标题栏底色 #F3F5F9
            TrySet(hwnd, 36, 0x001B_1B1B);            // 标题文字 #1B1B1B
            TrySet(hwnd, 34, 0x00EE_E7E3);            // 边框色 #E3E7EE
        }
    }

    private static void TrySet(IntPtr hwnd, int attr, int value)
    {
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch { /* 旧系统忽略 */ }
    }

    // ---------- 管理员判定 / 提权重启 ----------
    private const int TokenElevation = 20;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool IsAdmin()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)) return false;
            return elevated != 0;
        }
        catch { return false; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    /// <summary>以管理员身份重新启动自身（触发 UAC）。</summary>
    public static bool RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    // ---------- 进程路径 ----------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags,
        System.Text.StringBuilder buffer, ref int size);

    /// <summary>比 Process.MainModule 快一到两个数量级，且对更多进程可见。</summary>
    public static string GetProcessPath(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
        }
        catch { return ""; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    // ---------- 用 Shell API 打开目录 / 定位文件（避免直接拉起 explorer.exe 导致 0x142） ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    public static bool OpenFolderShell(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            IntPtr pidl = ILCreateFromPath(folder);
            if (pidl == IntPtr.Zero) return false;
            try { return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0; }
            finally { ILFree(pidl); }
        }
        catch { return false; }
    }

    public static bool RevealFileShell(string file)
    {
        try
        {
            string full = Path.GetFullPath(file);
            string? dir = Path.GetDirectoryName(full);
            if (dir == null || !Directory.Exists(dir)) return false;
            if (!File.Exists(full)) return OpenFolderShell(dir);

            IntPtr pidlFolder = ILCreateFromPath(dir);
            IntPtr pidlFile = ILCreateFromPath(full);
            if (pidlFolder == IntPtr.Zero || pidlFile == IntPtr.Zero)
            {
                if (pidlFolder != IntPtr.Zero) ILFree(pidlFolder);
                if (pidlFile != IntPtr.Zero) ILFree(pidlFile);
                return false;
            }
            try { return SHOpenFolderAndSelectItems(pidlFolder, 1, new[] { pidlFile }, 0) == 0; }
            finally { ILFree(pidlFolder); ILFree(pidlFile); }
        }
        catch { return false; }
    }

    // ---------- 设备树（CfgMgr32） ----------
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, System.Text.StringBuilder buffer,
        uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    public static string GetDeviceInstanceId(uint devNode)
    {
        if (CM_Get_Device_ID_Size(out uint len, devNode, 0) != 0 || len == 0) return "";
        var sb = new System.Text.StringBuilder((int)len + 2);
        return CM_Get_Device_IDW(devNode, sb, (uint)sb.Capacity, 0) == 0 ? sb.ToString() : "";
    }

    /// <summary>返回父设备的实例 ID；已到根则返回空串。</summary>
    public static string GetParentDeviceId(string deviceInstanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, deviceInstanceId, 0) != 0) return "";
            if (CM_Get_Parent(out uint parent, node, 0) != 0) return "";
            return GetDeviceInstanceId(parent);
        }
        catch { return ""; }
    }

    // ---------- 窗口闪烁 / 前台 ----------
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    public static IntPtr FindWindowByTitle(string title)
    {
        try { return FindWindow(null, title); } catch { return IntPtr.Zero; }
    }

    public static void ActivateWindow(IntPtr hwnd)
    {
        try { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); } catch { }
    }
}
