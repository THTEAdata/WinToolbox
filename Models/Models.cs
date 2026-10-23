using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinToolbox.Models;

/// <summary>轻量 INotifyPropertyChanged 基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class ProcessItem : ObservableObject
{
    private double _cpu;
    private double _memMb;
    private int _threads;
    private int _handles;

    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Company { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTime? Started { get; init; }
    public bool Elevated { get; init; }

    public double Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public double MemMb { get => _memMb; set => Set(ref _memMb, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public int Handles { get => _handles; set => Set(ref _handles, value); }

    public string CpuText => $"{Cpu:0.0}";
    public string MemText => $"{MemMb:0.0} MB";
    public string StartedText => Started.HasValue ? Started.Value.ToString("MM-dd HH:mm") : "-";

    public void RefreshMetrics(double cpu, double memMb, int threads, int handles)
    {
        Cpu = cpu; MemMb = memMb; Threads = threads; Handles = handles;
        Raise(nameof(CpuText));
        Raise(nameof(MemText));
    }
}

public sealed class ServiceItem : ObservableObject
{
    private string _state = "";

    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string StartMode { get; init; } = "";
    public string Account { get; init; } = "";
    public string Description { get; init; } = "";
    public uint ProcessId { get; init; }

    public string State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateText)); } }

    public string StateText => State switch
    {
        "Running" => "运行中",
        "Stopped" => "已停止",
        "Paused" => "已暂停",
        "Start Pending" => "启动中",
        "Stop Pending" => "停止中",
        _ => string.IsNullOrEmpty(State) ? "-" : State
    };

    public string StartModeText => StartMode switch
    {
        "Auto" => "自动",
        "Manual" => "手动",
        "Disabled" => "已禁用",
        "Boot" => "引导",
        "System" => "系统",
        _ => string.IsNullOrEmpty(StartMode) ? "-" : StartMode
    };

    public void RefreshState(string state) => State = state;
}

public sealed class StartupItem : ObservableObject
{
    private bool _enabled;

    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";      // 显示用位置
    public string Scope { get; init; } = "";       // HKCU / HKLM / UserFolder / CommonFolder
    public string KeyPath { get; init; } = "";     // 注册表路径
    public string ValueName { get; init; } = "";   // 注册表值名
    public string FilePath { get; init; } = "";    // 启动文件夹内的文件路径
    public string CurrentPath { get; set; } = "";  // 当前实际所在路径（启用/禁用切换后更新）
    public bool IsRegistry => !string.IsNullOrEmpty(KeyPath);
    public bool NeedsAdmin => Scope is "HKLM" or "CommonFolder";

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Raise(nameof(StateText)); } } }
    public string StateText => Enabled ? "已启用" : "已禁用";

    /// <summary>启用时需要把值写到哪个备份位置（与条目同作用域）。</summary>
    public string BackupRoot => Scope switch
    {
        "HKLM" => @"SOFTWARE\WinToolbox\DisabledRun",
        _ => @"SOFTWARE\WinToolbox\DisabledRun"
    };
}

public enum CleanKind { Folder, Files, RecycleBin }

public sealed class CleanItem : ObservableObject
{
    private bool _selected = true;
    private long _size;
    private int _count;
    private bool _scanned;
    private string _note = "";

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Path { get; init; } = "";
    public CleanKind Kind { get; init; }
    public string SearchPattern { get; init; } = "*";
    public bool NeedsAdmin { get; init; }
    public bool Risk { get; init; }

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }
    public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(CountText)); } }
    public bool Scanned { get => _scanned; set { if (Set(ref _scanned, value)) Raise(nameof(SizeText)); } }
    public string Note { get => _note; set => Set(ref _note, value); }

    public string SizeText => Scanned ? Core.Fmt.Size(Size) : "待扫描";
    public string CountText => Kind == CleanKind.RecycleBin ? $"{Count} 项" : $"{Count} 个文件";
}
