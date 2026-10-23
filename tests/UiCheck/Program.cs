using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UiCheck;

/// <summary>
/// 无窗口 UI 校验：启动真实的 MainWindow，逐个切换五个页面，
/// 等待数据加载后离屏渲染成 PNG。既能验证 XAML/模板/资源，也能产出交付截图。
/// 用法: dotnet run --project tests\UiCheck -c Release -- &lt;输出目录&gt;
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--nettest") { NetTest.Run(); return 0; }
        if (args.Length > 0 && args[0] == "--bench")
        {
            Bench();
            return 0;
        }

        if (args.Length > 0 && args[0] == "--check-settings")
        {
            WinToolbox.Core.AppSettings.Load();
            var c = WinToolbox.Core.AppSettings.Current;
            Console.WriteLine($"file={WinToolbox.Core.AppSettings.FilePath}");
            Console.WriteLine($"theme={c.Theme} autostart={c.AutoStart} admin={c.RunAsAdmin} monitor={c.BatteryMonitor} interval={c.BatteryIntervalMinutes} alert={c.LowBatteryAlert} threshold={c.LowBatteryThreshold}");
            Console.WriteLine($"autostart_registry={WinToolbox.Core.AutoStart.IsEnabled()} exe={WinToolbox.Core.AutoStart.ExePath}");
            return 0;
        }

        string outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "docs");
        Directory.CreateDirectory(outDir);

        var app = new WinToolbox.App();
        app.InitializeComponent();

        // 真实应用由 Application.Run 安装 DispatcherSynchronizationContext；
        // 这里必须补上，否则 await 的续体会跑到线程池，导致跨线程访问 UI。
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        int failed = 0;
        WinToolbox.MainWindow? win = null;

        try
        {
            win = new WinToolbox.MainWindow();
            // 放到屏幕外显示：既能完成真实布局/数据加载，又不打扰桌面
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.ShowInTaskbar = false;
            win.Left = -20000;
            win.Top = -20000;
            win.Show();
            Pump(2500);
            Console.WriteLine("PASS  MainWindow opened (offscreen)");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("FAIL  MainWindow: " + Describe(ex));
            return 1;
        }

        var content = win.Content as FrameworkElement;
        if (content == null)
        {
            Console.WriteLine("FAIL  window content is null");
            return 1;
        }

        var nav = win.FindName("Nav") as ListBox;
        string[] names = { "dashboard", "sys", "cleanup", "process", "service", "network", "usb", "battery", "settings" };
        int[] timeouts = { 20000, 20000, 45000, 25000, 25000, 10000, 20000, 12000, 6000 };


        int themeArg = Array.IndexOf(args, "--theme");
        if (themeArg >= 0 && themeArg + 1 < args.Length)
        {
            var mode = args[themeArg + 1].Equals("dark", StringComparison.OrdinalIgnoreCase)
                ? WinToolbox.Core.AppThemeMode.Dark : WinToolbox.Core.AppThemeMode.Light;
            WinToolbox.Core.ThemeManager.SetMode(mode);
            Pump(300);
            Console.WriteLine("theme=" + WinToolbox.Core.ThemeManager.Mode);

        }

        int start = 0, end = names.Length - 1;
        int pageArg = Array.IndexOf(args, "--page");
        if (pageArg >= 0 && pageArg + 1 < args.Length && int.TryParse(args[pageArg + 1], out int only))
        {
            start = end = Math.Clamp(only, 0, names.Length - 1);
        }

        for (int i = start; i <= end; i++)
        {
            try
            {
                nav!.SelectedIndex = i;
                Pump(400);

                var page = FindPage(win);
                bool ready = WaitUntil(() => IsReady(names[i], page), timeouts[i]);
                Pump(500);

                string file = Path.Combine(outDir, $"shot-{i + 1}-{names[i]}.png");
                double w = content.ActualWidth > 0 ? content.ActualWidth : 1304;
                double h = content.ActualHeight > 0 ? content.ActualHeight : 812;
                Render(content, w, h, file);
                Console.WriteLine($"{(ready ? "PASS" : "WARN")}  page[{i}] {names[i]} ready={ready} -> {Path.GetFileName(file)}");
                DumpStatus(names[i], page);

                if (names[i] == "network") DumpNetwork(page!);
                if (names[i] == "process") Console.WriteLine("      process rows=" + ((page as WinToolbox.Views.ProcessView)?.FindName("ProcGrid") as DataGrid)?.Items.Count);
                if (names[i] == "service") Console.WriteLine("      service rows=" + ((page as WinToolbox.Views.ServiceView)?.FindName("ServiceGrid") as DataGrid)?.Items.Count);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  page[{i}] {names[i]}: " + Describe(ex));
            }
        }

        try { win.Close(); } catch { }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "RESULT: all pages rendered" : $"RESULT: {failed} failure(s)");
        return failed == 0 ? 0 : 1;
    }

    private static void Bench()
    {
        var sw = Stopwatch.StartNew();
        var procs = Process.GetProcesses();
        Console.WriteLine($"GetProcesses: {sw.ElapsedMilliseconds} ms, count={procs.Length}");

        sw.Restart();
        int ok = 0;
        foreach (var p in procs)
        {
            try { var _ = p.WorkingSet64; ok++; } catch { }
        }
        Console.WriteLine($"WorkingSet: {sw.ElapsedMilliseconds} ms, ok={ok}");

        sw.Restart();
        int mod = 0;
        foreach (var p in procs)
        {
            try { var _ = p.MainModule?.FileName; mod++; } catch { }
        }
        Console.WriteLine($"MainModule: {sw.ElapsedMilliseconds} ms, ok={mod}");

        sw.Restart();
        foreach (var p in procs)
        {
            try { var _ = p.TotalProcessorTime; } catch { }
        }
        Console.WriteLine($"TotalProcessorTime: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        int vi = 0;
        foreach (var p in procs)
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) { var f = FileVersionInfo.GetVersionInfo(path); vi++; }
            }
            catch { }
        }
        Console.WriteLine($"MainModule+FileVersionInfo: {sw.ElapsedMilliseconds} ms, ok={vi}");

        sw.Restart();
        var services = WinToolbox.Core.Wmi.Query(
            "SELECT Name,DisplayName,State,StartMode,StartName,Description,ProcessId FROM Win32_Service",
            m => 1);
        Console.WriteLine($"Win32_Service (with Description): {sw.ElapsedMilliseconds} ms, count={services.Count}");

        sw.Restart();
        var services2 = WinToolbox.Core.Wmi.Query(
            "SELECT Name,DisplayName,State,StartMode,StartName,ProcessId FROM Win32_Service",
            m => 1);
        Console.WriteLine($"Win32_Service (no Description): {sw.ElapsedMilliseconds} ms, count={services2.Count}");

        sw.Restart();
        var cpu = WinToolbox.Core.Wmi.Query("SELECT Name FROM Win32_Processor", m => 1);
        Console.WriteLine($"Win32_Processor: {sw.ElapsedMilliseconds} ms, count={cpu.Count}");

        sw.Restart();
        long tempSize = 0;
        int tempCount = 0;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "*", opts))
        {
            try { tempSize += new FileInfo(f).Length; tempCount++; } catch { }
            if (tempCount > 20000) break;
        }
        Console.WriteLine($"Temp scan: {sw.ElapsedMilliseconds} ms, count={tempCount}, {WinToolbox.Core.Fmt.Size(tempSize)}");

        foreach (var p in procs) p.Dispose();
    }

    private static FrameworkElement? FindPage(WinToolbox.MainWindow win)
        => (win.FindName("PageHost") as ContentControl)?.Content as FrameworkElement;

    private static bool IsReady(string page, FrameworkElement? view)
    {
        if (view == null) return false;
        switch (page)
        {
            case "dashboard":
                return (view.FindName("ProcCountText") as TextBlock)?.Text != "—"
                       && (view.FindName("OsText") as TextBlock)?.Text != "—";
            case "sys":
                return (view.FindName("RefreshLabel") as TextBlock)?.Text.StartsWith("最后刷新") == true;
            case "cleanup":
                return (view.FindName("SummaryText") as TextBlock)?.Text.Contains("已选择") == true
                       || (view.FindName("SummaryText") as TextBlock)?.Text.Contains("未选择") == true
                       || (view.FindName("SummaryText") as TextBlock)?.Text.StartsWith("扫描完成") == true;
            case "process":
                return ((view.FindName("ProcGrid") as DataGrid)?.Items.Count ?? 0) > 5;
            case "service":
                return ((view.FindName("ServiceGrid") as DataGrid)?.Items.Count ?? 0) > 5;
            case "network":
                return ((view.FindName("AdapterPanel") as Panel)?.Children.Count ?? 0) > 0;
            case "battery":
                return (view.FindName("SummaryText") as TextBlock)?.Text.Contains("正在查询") == false;
            case "usb":
                return (view.FindName("CountText") as TextBlock)?.Text.Contains("控制器") == true;
            default:
                return true;
        }
    }

    private static void DumpStatus(string page, FrameworkElement? view)
    {
        if (view == null) { Console.WriteLine("      page=null"); return; }
        Console.WriteLine($"      view={view.GetType().Name} hash={view.GetHashCode()}");
        var footer = view.FindName("FooterText") as TextBlock;
        var summary = view.FindName("SummaryText") as TextBlock;
        var refresh = view.FindName("RefreshLabel") as TextBlock;
        if (refresh != null) Console.WriteLine("      refresh='" + refresh.Text + "'");
        if (summary != null) Console.WriteLine("      summary='" + summary.Text + "'");
        if (footer != null) Console.WriteLine("      footer='" + footer.Text + "'");
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(view) > 1) { /* no-op */ }
    }

    private static void DumpNetwork(FrameworkElement view)
    {
        DumpTextBox(view, "HostBox");
        DumpTextBox(view, "PortBox");
    }

    private static void DumpTextBox(FrameworkElement view, string name)
    {
        var tb = view.FindName(name) as TextBox;
        if (tb == null) { Console.WriteLine($"      {name}: not found"); return; }
        Console.WriteLine($"      {name}: text='{tb.Text}' size={tb.ActualWidth:0}x{tb.ActualHeight:0} " +
                          $"viewport={tb.ViewportHeight:0} extent={tb.ExtentHeight:0} fg={tb.Foreground} visible={tb.IsVisible}");
        int texts = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(tb);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is TextBlock t2 && t2.Text.Length > 0)
            {
                texts++;
                Console.WriteLine($"        TextBlock '{t2.Text}' fg={t2.Foreground} size={t2.ActualWidth:0}x{t2.ActualHeight:0}");
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        if (texts == 0) Console.WriteLine("        (no TextBlock inside textbox visual tree)");
    }

    private static void Render(FrameworkElement element, double width, double height, string path)
    {
        int pw = Math.Max(1, (int)Math.Round(width));
        int ph = Math.Max(1, (int)Math.Round(height));

        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);

        // 先铺主题背景，避免透明区域在查看器里显示成白色
        var bg = Application.Current?.Resources["BgGradient"] as System.Windows.Media.Brush
                 ?? System.Windows.Media.Brushes.White;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
        rtb.Render(dv);

        rtb.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>轮询直到条件满足或超时，期间持续跑消息循环。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Pump(120);
        }
        return condition();
    }

    /// <summary>跑一段消息循环，让 async 数据加载与 DispatcherTimer 真正执行。</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        for (int i = 0; i < 2; i++)
        {
            var idle = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.InvokeAsync(() => idle.Continue = false, DispatcherPriority.ContextIdle);
            Dispatcher.PushFrame(idle);
        }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return $"{inner.GetType().Name} - {inner.Message}";
    }
}
