using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UiCheck;

internal static class NetTest
{
    public static void Run()
    {
        var app = new WinToolbox.App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var view = new WinToolbox.Views.NetworkView();
        var win = new Window { Content = view, Width = 1000, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false };
        win.Show();
        Pump(1200);

        var host = (TextBox)view.FindName("HostBox")!;
        var port = (TextBox)view.FindName("PortBox")!;
        var output = (TextBox)view.FindName("OutputBox")!;

        host.Text = "127.0.0.1";
        port.Text = "135";
        Call(view, "Ping_Click"); Pump(7000);
        Console.WriteLine("=== PING ===");
        Console.WriteLine(output.Text.Trim());
        output.Clear();

        Call(view, "Dns_Click"); Pump(5000);
        Console.WriteLine("=== DNS ===");
        Console.WriteLine(output.Text.Trim());
        output.Clear();

        Call(view, "Port_Click"); Pump(6000);
        Console.WriteLine("=== PORT ===");
        Console.WriteLine(output.Text.Trim());
        output.Clear();

        host.Text = "www.baidu.com";
        port.Text = "443";
        Call(view, "Http_Click"); Pump(15000);
        Console.WriteLine("=== HTTP ===");
        Console.WriteLine(output.Text.Trim());
        output.Clear();

        Call(view, "FlushDns_Click"); Pump(5000);
        Console.WriteLine("=== FLUSHDNS ===");
        Console.WriteLine(output.Text.Trim());
        output.Clear();

        Call(view, "Tracert_Click"); Pump(15000);
        Console.WriteLine("=== TRACERT ===");
        Console.WriteLine(output.Text.Trim());

        win.Close();
    }

    private static void Call(object view, string method)
    {
        var mi = view.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi == null) { Console.WriteLine("missing " + method); return; }
        mi.Invoke(view, new object?[] { view, new RoutedEventArgs() });
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
