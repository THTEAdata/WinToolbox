using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class UsbView : UserControl, IAppPage
{
    private bool _busy;
    private bool _loaded;

    public UsbView()
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
        CountText.Text = "正在读取 USB 拓扑…";

        try
        {
            var (roots, controllers, hubs, devices) = await Task.Run(Build);
            UsbTree.ItemsSource = roots;
            CountText.Text = $"控制器 {controllers} 个 · 集线器 {hubs} 个 · 设备 {devices} 个";
            (Window.GetWindow(this) as MainWindow)?.SetStatus("USB 拓扑已刷新");
            _loaded = true;
        }
        catch (Exception ex)
        {
            CountText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>用 CfgMgr32 的父子关系还原真实 USB 拓扑树。</summary>
    private static (List<UsbNode> Roots, int Controllers, int Hubs, int Devices) Build()
    {
        var pnp = Wmi.Query("SELECT Name,DeviceID,Status FROM Win32_PnPEntity",
            m => (Id: Wmi.S(m, "DeviceID"), Name: Wmi.S(m, "Name"), Status: Wmi.S(m, "Status")));

        var info = new Dictionary<string, (string Name, string Status)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pnp)
            if (!string.IsNullOrEmpty(p.Id)) info[p.Id] = (p.Name, p.Status);

        var usbIds = info.Keys
            .Where(k => k.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var nodes = new Dictionary<string, UsbNode>(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        UsbNode GetOrAdd(string id)
        {
            if (nodes.TryGetValue(id, out var existing)) return existing;
            info.TryGetValue(id, out var meta);
            string name = string.IsNullOrWhiteSpace(meta.Name) ? id : meta.Name;
            bool controller = id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                              || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("控制器", StringComparison.Ordinal);

            var node = new UsbNode
            {
                Name = name,
                Detail = id,
                Status = Translate(meta.Status ?? ""),
                IsController = controller
            };
            nodes[id] = node;
            return node;
        }

        foreach (var id in usbIds)
        {
            GetOrAdd(id);
            string current = id;
            for (int depth = 0; depth < 16; depth++)
            {
                string parent = Native.GetParentDeviceId(current);
                if (string.IsNullOrEmpty(parent)) break;
                GetOrAdd(parent);
                if (!parentOf.ContainsKey(current)) parentOf[current] = parent;
                current = parent;
                if (parent.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        foreach (var pair in parentOf)
        {
            if (nodes.TryGetValue(pair.Key, out var child) && nodes.TryGetValue(pair.Value, out var parent))
                parent.Children.Add(child);
        }

        var roots = nodes.Values
            .Where(n => !parentOf.ContainsKey(n.Detail))
            .ToList();

        SortTree(roots);

        int controllers = nodes.Values.Count(n => n.IsController);
        int hubs = nodes.Values.Count(n =>
            n.Detail != null && n.Detail.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)
            || (n.Name != null && n.Name.Contains("集线器", StringComparison.Ordinal)));
        int devices = usbIds.Count;

        if (roots.Count == 0)
        {
            roots.Add(new UsbNode { Name = "未检测到 USB 设备", Detail = "", Status = "", IsController = true });
        }

        return (roots, controllers, hubs, devices);
    }

    private static void SortTree(List<UsbNode> list)
    {
        list.Sort((a, b) =>
        {
            int cmp = (b.IsController ? 1 : 0).CompareTo(a.IsController ? 1 : 0);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        foreach (var node in list)
        {
            if (node.Children.Count <= 1) continue;
            var ordered = node.Children
                .OrderBy(c => c.IsController ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            node.Children.Clear();
            foreach (var child in ordered) node.Children.Add(child);
            SortTree(ordered);
        }
    }

    private static string Translate(string status) => status switch
    {
        "OK" => "正常",
        "Error" => "异常",
        "Degraded" => "降级",
        "" => "",
        _ => status
    };

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
        => SetExpanded(UsbTree, ExpandAll.IsChecked == true);

    private static void SetExpanded(ItemsControl parent, bool expanded)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            container.IsExpanded = expanded;
            SetExpanded(container, expanded);
        }
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "打开设备管理器失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenUsbTreeView_Click(object sender, RoutedEventArgs e)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "tools", "UsbTreeView.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(Window.GetWindow(this)!,
                "未找到 UsbTreeView.exe。\n\n请把它放到：\n" + Path.Combine(AppContext.BaseDirectory, "tools"),
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "启动 UsbTreeView 失败：" + ex.Message, "WinToolbox",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>USB 拓扑树节点。</summary>
public sealed class UsbNode
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsController { get; init; }
    public ObservableCollection<UsbNode> Children { get; } = new();
}
