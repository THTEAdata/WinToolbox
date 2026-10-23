using System.Management;

namespace WinToolbox.Core;

/// <summary>WMI / CIM 查询封装：任何查询失败都返回空集合，不抛到 UI 层。</summary>
public static class Wmi
{
    public static Task<List<T>> QueryAsync<T>(string wql, Func<ManagementBaseObject, T> map)
        => Task.Run(() => Query(wql, map));

    public static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map)
    {
        var list = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                try { list.Add(map(mo)); }
                catch { /* 单个对象字段缺失，跳过 */ }
                finally { mo.Dispose(); }
            }
        }
        catch { /* 该 WMI 类在某系统上不可用，视为无数据 */ }
        return list;
    }

    public static string S(ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }

    public static uint U32(ManagementBaseObject o, string prop)
    {
        try { return o[prop] is null ? 0u : Convert.ToUInt32(o[prop]); } catch { return 0; }
    }

    public static ulong U64(ManagementBaseObject o, string prop)
    {
        try { return o[prop] is null ? 0ul : Convert.ToUInt64(o[prop]); } catch { return 0; }
    }

    public static DateTime? D(ManagementBaseObject o, string prop)
    {
        try
        {
            var raw = o[prop]?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return ManagementDateTimeConverter.ToDateTime(raw);
        }
        catch { return null; }
    }

    public static bool Available()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            foreach (ManagementBaseObject _ in results) return true;
        }
        catch { }
        return false;
    }
}
