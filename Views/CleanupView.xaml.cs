using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using WinToolbox.Core;
using WinToolbox.Models;

namespace WinToolbox.Views;

public partial class CleanupView : UserControl, IAppPage
{
    private readonly ObservableCollection<CleanItem> _items = new();
    private readonly ObservableCollection<StartupItem> _startup = new();
    private CancellationTokenSource? _cts;
    private bool _scanned;
    private bool _busy;
    private bool _scanning;
    private bool _startupLoaded;

    private const string RunHkcu = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunHklm32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public CleanupView()
    {
        InitializeComponent();
        CleanList.ItemsSource = _items;
        StartupList.ItemsSource = _startup;
        StartupList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(StartupToggle_Click));

        BuildItems();
        // 只由 OnActivated 触发首次扫描，避免 Loaded 与导航重复扫描
    }

    public void OnActivated()
    {
        if (!_scanned && !_scanning) _ = ScanAsync();
        if (!_startupLoaded) LoadStartup();
    }

    public void OnDeactivated() { }

    public void OnClosed()
    {
        _cts?.Cancel();
    }

    // ================= 清理项定义 =================

    private void BuildItems()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Add(new CleanItem
        {
            Name = "用户临时文件",
            Description = "程序运行产生的临时文件、安装包残留（%TEMP%）",
            Path = Path.GetTempPath(),
            Kind = CleanKind.Folder
        });
        Add(new CleanItem
        {
            Name = "Windows 临时文件",
            Description = "系统级临时目录，部分文件正在被占用，清理时会自动跳过",
            Path = Path.Combine(win, "Temp"),
            Kind = CleanKind.Folder,
            NeedsAdmin = true
        });
        Add(new CleanItem
        {
            Name = "错误报告与崩溃转储",
            Description = "Windows 错误报告存档与程序崩溃 dump 文件",
            Path = Path.Combine(programData, @"Microsoft\Windows\WER"),
            Kind = CleanKind.Folder,
            NeedsAdmin = true
        });
        Add(new CleanItem
        {
            Name = "缩略图与图标缓存",
            Description = "资源管理器生成的缩略图缓存，清理后会自动重建",
            Path = Path.Combine(local, @"Microsoft\Windows\Explorer"),
            Kind = CleanKind.Files,
            SearchPattern = "*.db"
        });
        Add(new CleanItem
        {
            Name = "Windows 更新缓存",
            Description = "已下载的系统更新安装包，清理不影响系统稳定性",
            Path = Path.Combine(win, @"SoftwareDistribution\Download"),
            Kind = CleanKind.Folder,
            NeedsAdmin = true
        });
        Add(new CleanItem
        {
            Name = "系统预读取（Prefetch）",
            Description = "加速程序启动的预读取数据，清理后首次启动会稍慢",
            Path = Path.Combine(win, "Prefetch"),
            Kind = CleanKind.Folder,
            NeedsAdmin = true,
            Risk = true
        });
        Add(new CleanItem
        {
            Name = "崩溃转储文件",
            Description = "应用程序崩溃时生成的 dump 文件（%LOCALAPPDATA%\\CrashDumps）",
            Path = Path.Combine(local, "CrashDumps"),
            Kind = CleanKind.Folder
        });
        Add(new CleanItem
        {
            Name = "回收站",
            Description = "已删除但尚未永久删除的文件",
            Path = "回收站",
            Kind = CleanKind.RecycleBin
        });

        foreach (var item in _items) item.PropertyChanged += (_, _) => UpdateSummary();
    }

    private void Add(CleanItem item)
    {
        item.PropertyChanged += (_, _) => UpdateSummary();
        _items.Add(item);
    }

    // ================= 扫描 =================

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy || _scanning) return;
        _busy = true;
        _scanning = true;
        Busy.Visibility = Visibility.Visible;
        ScanBtn.IsEnabled = false;
        CleanBtn.IsEnabled = false;
        SummaryText.Text = "正在扫描…";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            foreach (var item in _items)
            {
                if (ct.IsCancellationRequested) break;
                SummaryText.Text = $"正在扫描：{item.Name}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (size, count) = await Task.Run(() => Measure(item, ct), ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    item.Size = size;
                    item.Count = count;
                    item.Scanned = true;
                    item.Selected = size > 0;
                });
            }
            SummaryText.Text = "扫描完成";
            _scanned = true;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy = false;
            _scanning = false;
            Busy.Visibility = Visibility.Collapsed;
            ScanBtn.IsEnabled = true;
            CleanBtn.IsEnabled = true;
            UpdateSummary();
        }
    }

    private static (long Size, int Count) Measure(CleanItem item, CancellationToken ct)
    {
        if (item.Kind == CleanKind.RecycleBin)
        {
            var (items, bytes) = Native.GetRecycleBin();
            return (bytes, (int)Math.Min(items, int.MaxValue));
        }

        long size = 0;
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (item.Kind == CleanKind.Files)
            {
                if (!Directory.Exists(item.Path)) return (0, 0);
                foreach (var f in Directory.EnumerateFiles(item.Path, item.SearchPattern, SearchOption.TopDirectoryOnly))
                {
                    if (ct.IsCancellationRequested) break;
                    try { size += new FileInfo(f).Length; count++; } catch { }
                    if (count % 1000 == 0 && sw.ElapsedMilliseconds > 6000) break;
                }
            }
            else
            {
                if (!Directory.Exists(item.Path)) return (0, 0);
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    // 跳过符号链接/联接点，避免 Windows\Temp 之类的目录被无限递归
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };
                foreach (var f in Directory.EnumerateFiles(item.Path, "*", opts))
                {
                    if (ct.IsCancellationRequested) break;
                    try { size += new FileInfo(f).Length; count++; } catch { }
                    if (count % 1000 == 0 && sw.ElapsedMilliseconds > 6000) break;
                }
            }
        }
        catch { }
        return (size, count);
    }

    // ================= 清理 =================

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected && i.Scanned && i.Size > 0).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "没有选中任何可清理的项目。", "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long total = selected.Sum(i => i.Size);
        var names = string.Join("\n", selected.Select(i => $"· {i.Name}（{Fmt.Size(i.Size)}）"));
        var result = MessageBox.Show(Window.GetWindow(this)!,
            $"即将清理以下 {selected.Count} 项，预计释放 {Fmt.Size(total)}：\n\n{names}\n\n" +
            "清理会永久删除这些文件，正在被占用的文件会自动跳过。是否继续？",
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        CleanBtn.IsEnabled = false;
        ScanBtn.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        SummaryText.Text = "正在清理…";

        try
        {
            long freed = 0;
            int deleted = 0, skipped = 0;

            foreach (var item in selected)
            {
                SummaryText.Text = $"正在清理：{item.Name}";
                var (f, d, s) = await Task.Run(() => Purge(item));
                freed += f; deleted += d; skipped += s;
            }

            SummaryText.Text = $"清理完成：释放 {Fmt.Size(freed)}，删除 {deleted} 个文件，跳过 {skipped} 个";
            (Window.GetWindow(this) as MainWindow)?.SetStatus($"清理完成，释放 {Fmt.Size(freed)}");
            MessageBox.Show(Window.GetWindow(this)!,
                $"清理完成。\n\n释放空间：{Fmt.Size(freed)}\n删除文件：{deleted} 个\n跳过（被占用）：{skipped} 个",
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Information);

            _scanned = false;
            await ScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "清理时出错：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CleanBtn.IsEnabled = true;
            ScanBtn.IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private static (long Freed, int Deleted, int Skipped) Purge(CleanItem item)
    {
        if (item.Kind == CleanKind.RecycleBin)
        {
            var (bytes, count) = Native.GetRecycleBin();
            Native.EmptyRecycleBin();
            return (bytes, (int)Math.Min(count, int.MaxValue), 0);
        }

        long freed = 0;
        int deleted = 0, skipped = 0;

        // 安全阀：拒绝在盘符根目录或系统关键目录上直接删除
        if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Length < 8 || item.Path.EndsWith(":\\"))
            return (0, 0, 0);

        try
        {
            IEnumerable<string> files = item.Kind == CleanKind.Files
                ? Directory.EnumerateFiles(item.Path, item.SearchPattern, SearchOption.TopDirectoryOnly)
                : Directory.EnumerateFiles(item.Path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                });

            foreach (var f in files)
            {
                try
                {
                    long len = new FileInfo(f).Length;
                    File.Delete(f);
                    freed += len;
                    deleted++;
                }
                catch { skipped++; }
            }

            if (item.Kind == CleanKind.Folder && Directory.Exists(item.Path))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(item.Path, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(p => p.Length))
                    {
                        if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                        try { Directory.Delete(dir); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }

        return (freed, deleted, skipped);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool check = SelectAll.IsChecked == true;
        foreach (var i in _items) i.Selected = check;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        // 扫描过程中保留“正在扫描：xxx”的进度提示，不被属性变更覆盖
        if (_scanning) return;

        var sel = _items.Where(i => i.Selected && i.Scanned).ToList();
        long total = sel.Sum(i => i.Size);
        if (!_scanned)
        {
            SummaryText.Text = "尚未扫描";
            return;
        }
        SummaryText.Text = sel.Count == 0
            ? "未选择任何项目"
            : $"已选择 {sel.Count} 项 · 预计释放 {Fmt.Size(total)}";
    }

    // ================= 快捷操作 =================

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        QuickStatus.Text = "正在刷新 DNS…";
        var r = await Shell.RunAsync("ipconfig.exe", "/flushdns", 15000);
        QuickStatus.Text = r.Output.Contains("成功", StringComparison.Ordinal) || r.ExitCode == 0
            ? "DNS 缓存已刷新"
            : "刷新失败：" + r.Output.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
        => Shell.OpenFolder(Path.GetTempPath());

    private void OpenRecycle_Click(object sender, RoutedEventArgs e)
        => Shell.OpenFolder(@"shell:RecycleBinFolder");

    // ================= 启动项 =================

    private void LoadStartup()
    {
        _startupLoaded = true;
        _startup.Clear();

        ReadRunKey(Registry.CurrentUser, RunHkcu, "HKCU", "用户级 Run");
        ReadRunKey(Registry.LocalMachine, RunHklm, "HKLM", "系统级 Run");
        ReadRunKey(Registry.LocalMachine, RunHklm32, "HKLM", "系统级 Run (32 位)");
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "UserFolder", "用户启动文件夹");
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "CommonFolder", "公共启动文件夹");
        ReadDisabledRegistry();
        ReadDisabledFolder();

        StartupSummary.Text = $"共 {_startup.Count} 项，已启用 {_startup.Count(s => s.Enabled)} 项";
    }

    private void ReadRunKey(RegistryKey root, string path, string scope, string label)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string cmd = key.GetValue(name)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                _startup.Add(new StartupItem
                {
                    Name = name,
                    Command = cmd,
                    Source = $"注册表 · {label}",
                    Scope = scope,
                    KeyPath = path,
                    ValueName = name,
                    Enabled = true
                });
            }
        }
        catch { }
    }

    private void ReadStartupFolder(string folder, string scope, string label)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".ini" or ".txt") continue;
                _startup.Add(new StartupItem
                {
                    Name = Path.GetFileName(f),
                    Command = f,
                    Source = label,
                    Scope = scope,
                    FilePath = f,
                    CurrentPath = f,
                    Enabled = true
                });
            }
        }
        catch { }
    }

    private string DisabledKeyFor(StartupItem item)
        => @"SOFTWARE\WinToolbox\DisabledRun\" + item.KeyPath.Replace('\\', '_');

    private void ReadDisabledRegistry()
    {
        foreach (var item in EnumerateDisabledRegistry())
        {
            if (_startup.Any(s => s.Scope == item.Scope && s.KeyPath == item.KeyPath && s.ValueName == item.ValueName))
                continue;
            item.Enabled = false;
            _startup.Add(item);
        }
    }

    private List<StartupItem> EnumerateDisabledRegistry()
    {
        var result = new List<StartupItem>();
        foreach (var (root, scope, label, basePath) in new[]
                 {
                     (Registry.CurrentUser, "HKCU", RunHkcu, RunHkcu),
                     (Registry.LocalMachine, "HKLM", RunHklm, RunHklm),
                     (Registry.LocalMachine, "HKLM", RunHklm32, RunHklm32)
                 })
        {
            string backupPath = @"SOFTWARE\WinToolbox\DisabledRun\" + basePath.Replace('\\', '_');
            try
            {
                using var key = root.OpenSubKey(backupPath);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    string cmd = key.GetValue(name)?.ToString() ?? "";
                    result.Add(new StartupItem
                    {
                        Name = name,
                        Command = cmd,
                        Source = $"注册表 · {label}（已禁用）",
                        Scope = scope,
                        KeyPath = basePath,
                        ValueName = name,
                        Enabled = false
                    });
                }
            }
            catch { }
        }
        return result;
    }

    private static string BackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinToolbox", "DisabledStartup");

    private void ReadDisabledFolder()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return;
            foreach (var f in Directory.EnumerateFiles(BackupDir))
            {
                string fileName = Path.GetFileName(f);
                string scope = "UserFolder";
                string display = fileName;
                if (fileName.StartsWith("CommonFolder__", StringComparison.Ordinal))
                {
                    scope = "CommonFolder";
                    display = fileName["CommonFolder__".Length..];
                }
                else if (fileName.StartsWith("UserFolder__", StringComparison.Ordinal))
                {
                    display = fileName["UserFolder__".Length..];
                }

                _startup.Add(new StartupItem
                {
                    Name = display,
                    Command = "（已禁用的启动文件夹项目）",
                    Source = "启动文件夹（已禁用）",
                    Scope = scope,
                    FilePath = f,
                    CurrentPath = f,
                    Enabled = false
                });
            }
        }
        catch { }
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not CheckBox chk || chk.DataContext is not StartupItem item) return;
        bool enable = chk.IsChecked == true;
        if (!ApplyStartupChange(item, enable))
        {
            // 回滚界面状态
            chk.IsChecked = !enable;
            item.Enabled = !enable;
        }
        StartupSummary.Text = $"共 {_startup.Count} 项，已启用 {_startup.Count(s => s.Enabled)} 项";
    }

    private bool ApplyStartupChange(StartupItem item, bool enable)
    {
        try
        {
            if (item.IsRegistry)
            {
                var root = item.Scope == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                string backupPath = DisabledKeyFor(item);

                if (enable)
                {
                    using var backup = root.OpenSubKey(backupPath);
                    string? cmd = backup?.GetValue(item.ValueName)?.ToString();
                    if (string.IsNullOrEmpty(cmd)) return false;

                    using var target = root.CreateSubKey(item.KeyPath, writable: true);
                    target.SetValue(item.ValueName, cmd, RegistryValueKind.String);
                    backup!.DeleteValue(item.ValueName, throwOnMissingValue: false);
                    item.Enabled = true;
                    return true;
                }
                else
                {
                    using var target = root.OpenSubKey(item.KeyPath, writable: true);
                    string? cmd = target?.GetValue(item.ValueName)?.ToString();
                    if (cmd == null) return false;

                    using var backup = root.CreateSubKey(backupPath, writable: true);
                    backup.SetValue(item.ValueName, cmd, RegistryValueKind.String);
                    target!.DeleteValue(item.ValueName, throwOnMissingValue: false);
                    item.Enabled = false;
                    return true;
                }
            }

            string current = string.IsNullOrEmpty(item.CurrentPath) ? item.FilePath : item.CurrentPath;
            if (!string.IsNullOrEmpty(current) && File.Exists(current))
            {
                Directory.CreateDirectory(BackupDir);
                string originalDir = item.Scope == "CommonFolder"
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                    : Environment.GetFolderPath(Environment.SpecialFolder.Startup);

                if (enable)
                {
                    string dest = Path.Combine(originalDir, item.Name);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(current, dest);
                    item.CurrentPath = dest;
                    item.Enabled = true;
                }
                else
                {
                    string dest = Path.Combine(BackupDir, item.Scope + "__" + item.Name);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(current, dest);
                    item.CurrentPath = dest;
                    item.Enabled = false;
                }
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "没有权限修改该项，请点击左下角「以管理员身份重启」后再试。", "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "修改启动项失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void StartupRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not StartupItem item) return;

        string? target = item.FilePath;
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(item.Command))
        {
            string cmd = item.Command.Trim();
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 1) target = cmd[1..end];
            }
            else
            {
                int sp = cmd.IndexOf(' ');
                target = sp > 0 ? cmd[..sp] : cmd;
            }
        }
        if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
            Shell.OpenPath(target);
    }

    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => LoadStartup();

    private void OpenStartupFolder_Click(object sender, RoutedEventArgs e)
        => Shell.OpenFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup));

    private void OpenStartupSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps")
            { UseShellExecute = true });
        }
        catch { }
    }

    private Brush DangerBrush => (Brush)FindResource("DangerBrush");
}
