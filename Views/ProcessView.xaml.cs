using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WinToolbox.Core;
using WinToolbox.Models;

namespace WinToolbox.Views;

public partial class ProcessView : UserControl, IAppPage
{
    private readonly ObservableCollection<ProcessItem> _items = new();
    private readonly Dictionary<int, TimeSpan> _prevCpu = new();
    private readonly Dictionary<string, (string Company, string Description)> _metaCache = new();
    private readonly DispatcherTimer _timer;
    private readonly ICollectionView _view;

    private DateTime _lastSample = DateTime.UtcNow;
    private bool _busy;
    private bool _firstLoad = true;
    private string _filter = "";

    public ProcessView()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterPredicate;
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.Name), ListSortDirection.Ascending));
        ProcGrid.ItemsSource = _view;
        ProcGrid.SelectionChanged += (_, _) => UpdateSelection();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
        // 只由 OnActivated 触发首次加载，避免重复刷新
    }

    public void OnActivated()
    {
        if (_firstLoad) _ = RefreshAsync(silent: false);
        if (AutoRefresh.IsChecked == true) _timer.Start();
    }

    public void OnDeactivated() => _timer.Stop();

    public void OnClosed() => _timer.Stop();

    // ---------------- 刷新 ----------------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(silent: false);

    private async Task RefreshAsync(bool silent)
    {
        if (_busy) return;
        _busy = true;
        if (!silent)
        {
            Busy.Visibility = Visibility.Visible;
            FooterText.Text = "正在读取进程列表…";
        }

        try
        {
            var snaps = await Task.Run(Snapshot);
            bool bulk = _firstLoad;
            if (bulk) ProcGrid.ItemsSource = null;
            Merge(snaps);
            if (bulk) ProcGrid.ItemsSource = _view;
            FooterText.Text = $"共 {_items.Count} 个进程 · 内存合计 {Fmt.Size((long)_items.Sum(i => i.MemMb * 1048576))} · " +
                              $"最后刷新 {DateTime.Now:HH:mm:ss}";
            _firstLoad = false;
        }
        catch (Exception ex)
        {
            FooterText.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private List<ProcSnapshot> Snapshot()
    {
        var now = DateTime.UtcNow;
        double elapsedMs = Math.Max(200, (now - _lastSample).TotalMilliseconds);
        _lastSample = now;

        var result = new List<ProcSnapshot>(400);
        var next = new Dictionary<int, TimeSpan>(400);
        int cpuCount = Math.Max(1, Environment.ProcessorCount);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid;
                string name;
                try { pid = p.Id; name = p.ProcessName; } catch { continue; }

                TimeSpan? totalCpu = null;
                try { totalCpu = p.TotalProcessorTime; } catch { }

                double cpu = 0;
                if (totalCpu.HasValue)
                {
                    if (_prevCpu.TryGetValue(pid, out var prev))
                    {
                        double delta = (totalCpu.Value - prev).TotalMilliseconds;
                        if (delta > 0) cpu = delta / elapsedMs / cpuCount * 100.0;
                    }
                    next[pid] = totalCpu.Value;
                }

                double memMb = 0;
                try { memMb = p.WorkingSet64 / 1048576.0; } catch { }

                int threads = 0, handles = 0;
                try { threads = p.Threads.Count; } catch { }
                try { handles = p.HandleCount; } catch { }

                string path = Native.GetProcessPath(pid);

                string company = "", desc = "";
                if (!string.IsNullOrEmpty(path))
                {
                    if (_metaCache.TryGetValue(path, out var meta))
                    {
                        company = meta.Company;
                        desc = meta.Description;
                    }
                    else
                    {
                        try
                        {
                            var fvi = FileVersionInfo.GetVersionInfo(path);
                            company = fvi.CompanyName ?? "";
                            desc = fvi.FileDescription ?? "";
                            _metaCache[path] = (company, desc);
                        }
                        catch { }
                    }
                }

                DateTime? started = null;
                try { started = p.StartTime; } catch { }

                result.Add(new ProcSnapshot(pid, name, path, company, desc, started, cpu, memMb, threads, handles));
            }
            finally { p.Dispose(); }
        }

        _prevCpu.Clear();
        foreach (var kv in next) _prevCpu[kv.Key] = kv.Value;
        return result;
    }

    private void Merge(List<ProcSnapshot> snaps)
    {
        var existing = _items.ToDictionary(i => i.Pid);
        var alive = new HashSet<int>(snaps.Count);

        foreach (var s in snaps)
        {
            alive.Add(s.Pid);
            if (existing.TryGetValue(s.Pid, out var item))
            {
                item.RefreshMetrics(s.Cpu, s.MemMb, s.Threads, s.Handles);
            }
            else
            {
                var added = new ProcessItem
                {
                    Pid = s.Pid, Name = s.Name, Path = s.Path, Company = s.Company,
                    Description = s.Description, Started = s.Started
                };
                added.RefreshMetrics(s.Cpu, s.MemMb, s.Threads, s.Handles);
                _items.Add(added);
            }
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!alive.Contains(_items[i].Pid)) _items.RemoveAt(i);
        }
    }

    private ProcessItem? Selected => ProcGrid.SelectedItem as ProcessItem;

    private void UpdateSelection()
    {
        var item = Selected;
        SelectedText.Text = item == null
            ? ""
            : $"已选：{item.Name} (PID {item.Pid}) · 内存 {item.MemText}";
    }

    // ---------------- 搜索 ----------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        _view.Refresh();
        UpdateSelection();
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrEmpty(_filter)) return true;
        if (obj is not ProcessItem p) return false;
        return p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || p.Pid.ToString() == _filter
               || (p.Company?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (p.Description?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void AutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    // ---------------- 操作 ----------------

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null)
        {
            MessageBox.Show(Window.GetWindow(this)!, "请先在列表中选择一个进程。", "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (item.Pid == Environment.ProcessId)
        {
            MessageBox.Show(Window.GetWindow(this)!, "不能结束工具箱自身的进程。", "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(Window.GetWindow(this)!,
            $"确定要结束进程吗？\n\n名称：{item.Name}\nPID：{item.Pid}\n路径：{Fmt.Nz(item.Path)}\n\n" +
            "未保存的数据可能会丢失。",
            "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            using var p = Process.GetProcessById(item.Pid);
            p.Kill(entireProcessTree: true);
            (Window.GetWindow(this) as MainWindow)?.SetStatus($"已结束进程 {item.Name} (PID {item.Pid})");
            _ = RefreshAsync(silent: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "结束进程失败：" + ex.Message +
                "\n\n如果这是系统进程，请点击左下角「以管理员身份重启」后再试。",
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "无法定位该进程的文件路径（可能权限不足或进程已退出）。",
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Shell.OpenPath(item.Path);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        string text = $"名称：{item.Name}\r\nPID：{item.Pid}\r\nCPU：{item.CpuText}%\r\n内存：{item.MemText}\r\n" +
                      $"线程：{item.Threads}\r\n句柄：{item.Handles}\r\n启动：{item.StartedText}\r\n" +
                      $"公司：{item.Company}\r\n描述：{item.Description}\r\n路径：{item.Path}";
        try
        {
            Clipboard.SetText(text);
            (Window.GetWindow(this) as MainWindow)?.SetStatus("进程信息已复制");
        }
        catch { }
    }

    private void ProcGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenLocation_Click(sender, e);

    private sealed record ProcSnapshot(int Pid, string Name, string Path, string Company, string Description,
        DateTime? Started, double Cpu, double MemMb, int Threads, int Handles);
}
