using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinToolbox.Core;
using WinToolbox.Views;

namespace WinToolbox;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _clockTimer;
    private bool _isAdmin;

    private static readonly (string Tag, string Title, string Subtitle)[] Pages =
    {
        ("dashboard", "概览", "全部模块与实时状态"),
        ("sys", "系统信息", "硬件、操作系统与运行状态"),
        ("clean", "系统清理", "清理临时文件、缓存与回收站，管理开机启动项"),
        ("process", "进程管理", "查看 CPU / 内存占用，结束异常进程"),
        ("service", "服务管理", "查看、启动、停止 Windows 服务并调整启动类型"),
        ("network", "网络诊断", "Ping、端口探测、路由追踪与本机网络信息"),
        ("usb", "USB 设备", "USB 控制器、集线器与设备拓扑"),
        ("battery", "电量", "外设电量与系统电池（无需厂商驱动）"),
        ("settings", "设置", "外观主题与程序信息")
    };

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            ApplyChromeSafe();
            ThemeManager.Changed += OnThemeChanged;
        };

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateFooter();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isAdmin = Native.IsAdmin();
        ApplyAdminUi();
        UpdateThemeButtons();
        Nav.SelectedIndex = 0;
        _clockTimer.Start();
        UpdateFooter();
    }

    private void ApplyAdminUi()
    {
        var dot = _isAdmin ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("WarnBrush");
        AdminDot.Fill = dot;
        HeaderDot.Fill = dot;
        AdminLabel.Text = _isAdmin ? "管理员" : "普通权限";
        HeaderStatus.Text = _isAdmin ? "管理员权限" : "普通权限";
        ElevateBtn.Visibility = _isAdmin ? Visibility.Collapsed : Visibility.Visible;

        var version = typeof(MainWindow).Assembly.GetName().Version;
        AdminLabel.ToolTip = $"WinToolbox v{version?.ToString(3)} · {Environment.OSVersion.VersionString}";
    }

    private void UpdateFooter()
    {
        var (total, avail) = Native.GetMemory();
        if (total > 0)
        {
            double used = total - avail;
            MemoryText.Text = $"内存 {Fmt.Size((ulong)used)} / {Fmt.Size(total)}（{used * 100.0 / total:0}%）";
        }
        else
        {
            MemoryText.Text = "";
        }
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>供首页磁贴跳转。</summary>
    public void NavigateTo(string tag)
    {
        for (int i = 0; i < Nav.Items.Count; i++)
        {
            if (Nav.Items[i] is ListBoxItem item && (item.Tag as string) == tag)
            {
                Nav.SelectedIndex = i;
                return;
            }
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item) return;
        string tag = item.Tag as string ?? "dashboard";

        var meta = Pages.FirstOrDefault(p => p.Tag == tag);
        PageTitle.Text = string.IsNullOrEmpty(meta.Title) ? "概览" : meta.Title;
        PageSubtitle.Text = string.IsNullOrEmpty(meta.Subtitle) ? "" : meta.Subtitle;

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = CreatePage(tag);
            _pages[tag] = page;
        }

        if (PageHost.Content is IAppPage previous && !ReferenceEquals(previous, page))
            previous.OnDeactivated();

        PageHost.Content = page;
        (page as IAppPage)?.OnActivated();
        SetStatus($"就绪 · {PageTitle.Text}");
    }

    private static UserControl CreatePage(string tag) => tag switch
    {
        "sys" => new SystemInfoView(),
        "clean" => new CleanupView(),
        "process" => new ProcessView(),
        "service" => new ServiceView(),
        "network" => new NetworkView(),
        "usb" => new UsbView(),
        "battery" => new BatteryView(),
        "settings" => new SettingsView(),
        _ => new DashboardView()
    };

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Light);

    private void ThemeDark_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.Dark);

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemeMode.System);

    private void SetTheme(AppThemeMode mode)
    {
        ThemeManager.SetMode(mode);
        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        var activeBg = (Brush)FindResource("AccentDimBrush");

        void Style(Button button, bool active)
        {
            button.Background = active ? activeBg : Brushes.Transparent;
            button.Foreground = active ? accent : dim;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        Style(ThemeLightBtn, ThemeManager.Mode == AppThemeMode.Light);
        Style(ThemeDarkBtn, ThemeManager.Mode == AppThemeMode.Dark);
        Style(ThemeSystemBtn, ThemeManager.Mode == AppThemeMode.System);
    }

    private void OnThemeChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplyChromeSafe();
            UpdateThemeButtons();
        });
    }

    private void ApplyChromeSafe()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) ThemeManager.ApplyChrome(handle);
        }
        catch { }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int index = (int)e.Key - (int)Key.D1;
            if (index < Nav.Items.Count) Nav.SelectedIndex = index;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R)
        {
            (PageHost.Content as IAppPage)?.OnActivated();
            e.Handled = true;
        }
    }

    private void ElevateBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "部分清理和服务操作需要管理员权限。\n\n是否以管理员身份重新启动 WinToolbox？\n（会弹出 UAC 确认框，当前窗口将关闭）",
            "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (Native.RestartElevated())
            Application.Current.Shutdown();
        else
            MessageBox.Show(this, "提权启动被取消或失败。\n可以右键 WinToolbox.exe →「以管理员身份运行」。",
                "WinToolbox", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        ThemeManager.Changed -= OnThemeChanged;
        foreach (var page in _pages.Values)
            (page as IAppPage)?.OnClosed();
    }
}
