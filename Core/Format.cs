using System.Globalization;

namespace WinToolbox.Core;

/// <summary>显示格式化。</summary>
public static class Fmt
{
    public static string Size(long bytes)
    {
        if (bytes < 0) return "-";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }

    public static string Size(ulong bytes) => Size((long)Math.Min(bytes, long.MaxValue));

    public static string Speed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "-";
        if (bitsPerSecond >= 1_000_000_000) return $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps";
        if (bitsPerSecond >= 1_000_000) return $"{bitsPerSecond / 1_000_000d:0.##} Mbps";
        if (bitsPerSecond >= 1_000) return $"{bitsPerSecond / 1_000d:0.##} Kbps";
        return $"{bitsPerSecond} bps";
    }

    public static string Uptime(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0) return "-";
        var parts = new List<string>();
        if (ts.Days > 0) parts.Add($"{ts.Days} 天");
        if (ts.Hours > 0) parts.Add($"{ts.Hours} 小时");
        if (ts.Minutes > 0) parts.Add($"{ts.Minutes} 分");
        if (parts.Count == 0) parts.Add($"{Math.Max(1, ts.Seconds)} 秒");
        return string.Join(" ", parts);
    }

    public static string DateTime(DateTime? dt)
        => dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "-";

    public static string Percent(double v) => $"{v:0.0}%";

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>把数值型 WMI 字段（如 MHz、MB）安全转为字符串。</summary>
    public static string Nz(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();
}
