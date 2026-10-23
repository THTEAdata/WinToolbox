using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinToolbox.Core;
using WinToolbox.Models;

namespace WinToolbox.Views;

public partial class ServiceView : UserControl, IAppPage
{
    private readonly ObservableCollection<ServiceItem> _items = new();
    private readonly ICollectionView _view;
    private bool _busy;
    private bool _firstLoad = true;
    private string _filter = "";

    public ServiceView()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterPredicate;
        _view.SortDescriptions.Add(new SortDescription(nameof(ServiceItem.DisplayName), ListSortDirection.Ascending));
        ServiceGrid.ItemsSource = _view;
        // 只由 OnActivated 触发加载，避免 Loaded 与导航各触发一次造成重复查询
    }

    public void OnActivated()
    {
        if (_firstLoad && !_busy) _ = LoadAsync();
    }

    public void OnDeactivated() { }

    public void OnClosed() { }

    // ---------------- 加载 ----------------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        Busy.Visibility = Visibility.Visible;
        FooterText.Text = "正在读取服务列表…";

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var list = await Task.Run(() => Wmi.Query(
                "SELECT Name,DisplayName,State,StartMode,StartName,Description,ProcessId FROM Win32_Service",
                m => new ServiceItem
                {
                    Name = Wmi.S(m, "Name"),
                    DisplayName = Wmi.S(m, "DisplayName"),
                    State = Wmi.S(m, "State"),
                    StartMode = Wmi.S(m, "StartMode"),
                    Account = Wmi.S(m, "StartName"),
                    Description = Wmi.S(m, "Description"),
                    ProcessId = Wmi.U32(m, "ProcessId")
                }));

            var existing = _items.ToDictionary(i => i.Name);
            var alive = new HashSet<string>(list.Count);

            // 批量填充时先解绑，避免每 Add 一次就触发整表重新布局
            ServiceGrid.ItemsSource = null;
            foreach (var s in list)
            {
                alive.Add(s.Name);
                if (existing.TryGetValue(s.Name, out var old))
                {
                    old.RefreshState(s.State);
                }
                else
                {
                    _items.Add(s);
                }
            }
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!alive.Contains(_items[i].Name)) _items.RemoveAt(i);
            }
            ServiceGrid.ItemsSource = _view;

            int running = _items.Count(i => i.State == "Running");
            FooterText.Text = $"共 {_items.Count} 个服务 · 运行中 {running} 个 · 最后刷新 {DateTime.Now:HH:mm:ss}";
            _firstLoad = false;
        }
        catch (Exception ex)
        {
            FooterText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private ServiceItem? Selected => ServiceGrid.SelectedItem as ServiceItem;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        _view.Refresh();
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrEmpty(_filter)) return true;
        if (obj is not ServiceItem s) return false;
        return s.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || s.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || s.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ServiceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = Selected;
        if (item == null)
        {
            FooterText.Text = "";
            return;
        }
        FooterText.Text = string.IsNullOrWhiteSpace(item.Description)
            ? $"{item.Name} · {item.DisplayName}"
            : $"{item.Name} · {item.Description}";
    }

    // ---------------- 服务操作 ----------------

    private static bool SafeName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Length < 260 &&
           name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '@' or '(' or ')' or ' ');

    private void HandleScResult(ServiceItem item, Shell.RunResult r, string successText)
    {
        bool ok = r.ExitCode == 0;
        if (!ok && r.Output.Contains("拒绝访问", StringComparison.Ordinal))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "操作被拒绝：需要管理员权限。\n请点击左下角「以管理员身份重启」后重试。",
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ok)
        {
            (Window.GetWindow(this) as MainWindow)?.SetStatus($"{successText}：{item.DisplayName}");
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this)!,
                $"操作失败：\n\n{r.Output}", "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _ = LoadAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) { RequireSelection(); return; }

        Busy.Visibility = Visibility.Visible;
        try
        {
            var r = await Shell.RunAsync("sc.exe", $"start \"{item.Name}\"", 30000);
            HandleScResult(item, r, "服务已启动");
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) { RequireSelection(); return; }

        if (item.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals("DcomLaunch", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(Window.GetWindow(this)!, "该为核心系统服务，禁止在此停止。", "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(Window.GetWindow(this)!,
            $"确定要停止服务吗？\n\n{item.DisplayName}（{item.Name}）\n\n依赖此服务的程序可能会停止工作。",
            "停止服务", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        Busy.Visibility = Visibility.Visible;
        try
        {
            var r = await Shell.RunAsync("sc.exe", $"stop \"{item.Name}\"", 30000);
            HandleScResult(item, r, "服务已停止");
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) { RequireSelection(); return; }

        Busy.Visibility = Visibility.Visible;
        try
        {
            await Shell.RunAsync("sc.exe", $"stop \"{item.Name}\"", 30000);
            await Task.Delay(600);
            var r = await Shell.RunAsync("sc.exe", $"start \"{item.Name}\"", 30000);
            HandleScResult(item, r, "服务已重启");
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private async void ApplyStartMode_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) { RequireSelection(); return; }

        string mode = (StartModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "demand";
        string label = mode switch { "auto" => "自动", "disabled" => "禁用", _ => "手动" };

        var result = MessageBox.Show(Window.GetWindow(this)!,
            $"将服务「{item.DisplayName}」的启动类型改为「{label}」？\n\n" +
            "禁用后该服务及其依赖功能将不再自动启动。",
            "修改启动类型", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        Busy.Visibility = Visibility.Visible;
        try
        {
            var r = await Shell.RunAsync("sc.exe", $"config \"{item.Name}\" start= {mode}", 30000);
            HandleScResult(item, r, $"启动类型已改为{label}");
        }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private void RequireSelection()
        => MessageBox.Show(Window.GetWindow(this)!, "请先在列表中选择一个服务。", "WinToolbox",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void OpenServicesMsc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "打开服务管理器失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ServiceGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenServicesMsc_Click(sender, e);
}
