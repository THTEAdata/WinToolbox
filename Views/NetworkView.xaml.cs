using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinToolbox.Core;

namespace WinToolbox.Views;

public partial class NetworkView : UserControl, IAppPage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private CancellationTokenSource? _cts;
    private bool _firstLoad = true;
    private bool _running;

    public NetworkView()
    {
        InitializeComponent();
    }

    public void OnActivated()
    {
        if (_firstLoad) LoadAdapters();
    }

    public void OnDeactivated() { }

    public void OnClosed() => _cts?.Cancel();

    // ---------------- 输出 ----------------

    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Log(text));
            return;
        }
        OutputBox.AppendText(text + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => OutputBox.Clear();

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(OutputBox.Text)) Clipboard.SetText(OutputBox.Text);
            (Window.GetWindow(this) as MainWindow)?.SetStatus("输出已复制到剪贴板");
        }
        catch { }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        Log("—— 已请求停止 ——");
    }

    private DateTime _startedAt;

    private bool Begin()
    {
        // 看门狗：上一个任务异常残留超过 2 分钟时自动释放，避免“点不动”
        if (_running && DateTime.Now - _startedAt < TimeSpan.FromMinutes(2))
        {
            Log("已有任务正在执行，请先等待完成或点击「停止」。");
            return false;
        }
        _running = true;
        _startedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        Busy.Visibility = Visibility.Visible;
        return true;
    }

    private void End()
    {
        _running = false;
        Busy.Visibility = Visibility.Collapsed;
        _cts?.Dispose();
        _cts = null;
    }

    private string ValidHostOrLog()
    {
        string host = HostBox.Text.Trim();
        if (!Shell.IsSafeHost(host))
        {
            Log("主机名不合法：只能包含字母、数字、点、短横线、下划线或冒号。");
            return "";
        }
        return host;
    }

    // ---------------- 本机网络 ----------------

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => LoadAdapters();

    private void LoadAdapters()
    {
        _firstLoad = false;
        AdapterPanel.Children.Clear();

        List<NetworkInterface> list;
        try
        {
            list = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => !IsFilterAdapter(n))
                .Where(n =>
                {
                    try
                    {
                        var p = n.GetIPProperties();
                        bool hasV4 = p.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        bool hasGateway = p.GatewayAddresses.Count > 0;
                        return hasV4 || hasGateway || (n.OperationalStatus == OperationalStatus.Up && n.Speed > 0);
                    }
                    catch { return false; }
                })
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .ThenBy(n => n.Name)
                .ToList();
        }
        catch
        {
            list = new List<NetworkInterface>();
        }

        if (list.Count == 0)
        {
            AdapterPanel.Children.Add(new TextBlock
            {
                Text = "未检测到网络适配器",
                Foreground = (Brush)FindResource("TextDimBrush"),
                FontSize = 12.5
            });
            return;
        }

        bool first = true;
        foreach (var ni in list.Take(4))
        {
            if (!first)
            {
                AdapterPanel.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(0, 8, 0, 8),
                    Background = (Brush)FindResource("BorderBrush")
                });
            }
            first = false;

            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).ToList();
            string mac = "";
            try { mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); } catch { }
            string gw = props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "";
            string dns = string.Join(", ", props.DnsAddresses.Select(a => a.ToString()));
            bool up = ni.OperationalStatus == OperationalStatus.Up;

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = ni.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new Border
            {
                Style = (Style)FindResource("Tag"),
                Background = up
                    ? new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xE6))
                    : (Brush)FindResource("Surface3Brush"),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = up ? "已连接" : "未连接", FontSize = 10.5,
                    Foreground = up ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("TextDimBrush")
                }
            });
            AdapterPanel.Children.Add(head);

            AddKv(AdapterPanel, "IPv4", v4.Count > 0 ? string.Join(", ", v4) : "-");
            if (!string.IsNullOrEmpty(gw)) AddKv(AdapterPanel, "网关", gw);
            if (!string.IsNullOrEmpty(dns)) AddKv(AdapterPanel, "DNS", dns);
            if (!string.IsNullOrEmpty(mac)) AddKv(AdapterPanel, "MAC", mac);
            AddKv(AdapterPanel, "速率", Fmt.Speed(ni.Speed));
        }
    }

    private static bool IsFilterAdapter(NetworkInterface ni)
    {
        string s = (ni.Name + " " + ni.Description).ToLowerInvariant();
        string[] bad =
        {
            "filter", "scheduler", "loopback", "wan miniport", "npcap", "lightweight",
            "teredo", "isatap", "6to4", "qos packet", "bluetooth"
        };
        return bad.Any(k => s.Contains(k));
    }

    private void AddKv(Panel host, string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock { Text = key, FontSize = 11.5, Foreground = (Brush)FindResource("TextMuteBrush") };
        var v = new TextBlock
        {
            Text = value, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextDimBrush")
        };
        Grid.SetColumn(v, 1);
        g.Children.Add(k);
        g.Children.Add(v);
        host.Children.Add(g);
    }

    // ---------------- Ping ----------------

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        string host = ValidHostOrLog();
        if (host.Length == 0 || !Begin()) return;

        try
        {
            Log("");
            Log($"=== Ping {host} ===");

            var all = new System.Text.StringBuilder();
            int code = await Shell.RunStreamingAsync("ping.exe", $"-n 4 -w 2000 {host}",
                line => { all.AppendLine(line); Log(line); }, _cts!.Token);

            string text = all.ToString();
            bool noReply = code != 0
                           || text.Contains("100%", StringComparison.Ordinal)
                           || text.Contains("请求超时", StringComparison.Ordinal)
                           || text.Contains("无法访问目标主机", StringComparison.Ordinal)
                           || text.Contains("Request timed out", StringComparison.OrdinalIgnoreCase);

            if (noReply)
            {
                Log("—— 未收到有效回复 ——");
                Log("提示：目标主机不可达、被防火墙拦截，或本机禁止了 ICMP(Ping) 请求。");
                Log("可以改用下方「TCP 端口」检测目标端口是否开放。");
            }
            else
            {
                Log("—— 连通正常 ——");
            }
        }
        catch (Exception ex)
        {
            Log("Ping 执行失败：" + ex.Message);
        }
        finally { End(); }
    }

    // ---------------- DNS ----------------

    private async void Dns_Click(object sender, RoutedEventArgs e)
    {
        string host = ValidHostOrLog();
        if (host.Length == 0 || !Begin()) return;

        try
        {
            Log("");
            Log($"=== 解析 {host} ===");
            var sw = Stopwatch.StartNew();
            var addrs = await Dns.GetHostAddressesAsync(host);
            sw.Stop();
            if (addrs.Length == 0)
            {
                Log("未解析到任何地址。");
            }
            else
            {
                foreach (var a in addrs)
                    Log($"  {a}  ({a.AddressFamily})");
                Log($"—— {addrs.Length} 条记录，耗时 {sw.ElapsedMilliseconds} ms ——");
            }
        }
        catch (Exception ex)
        {
            Log("解析失败：" + ex.Message);
        }
        finally { End(); }
    }

    // ---------------- TCP 端口 ----------------

    private async void Port_Click(object sender, RoutedEventArgs e)
    {
        string host = ValidHostOrLog();
        if (host.Length == 0) return;

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            Log("端口号无效：应为 1 - 65535 之间的整数。");
            return;
        }
        if (!Begin()) return;

        try
        {
            Log("");
            Log($"=== 检测 {host}:{port} ===");
            var sw = Stopwatch.StartNew();
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(4000);
            try
            {
                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                Log($"✔ 端口开放，连接成功（{sw.ElapsedMilliseconds} ms）");
            }
            catch (OperationCanceledException)
            {
                Log("✘ 连接超时（4 秒）：端口可能被防火墙拦截或未开放。");
            }
            catch (SocketException ex)
            {
                Log($"✘ 连接失败：{ex.SocketErrorCode}（端口未开放或被拒绝）");
            }
        }
        finally { End(); }
    }

    // ---------------- HTTP ----------------

    private async void Http_Click(object sender, RoutedEventArgs e)
    {
        string host = HostBox.Text.Trim();
        string url = host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log("网址无效：请输入域名或完整 URL（以 http/https 开头）。");
            return;
        }
        if (!Begin()) return;

        try
        {
            Log("");
            Log($"=== HTTP {url} ===");
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts!.Token);
            sw.Stop();
            Log($"状态：{(int)resp.StatusCode} {resp.ReasonPhrase}（{sw.ElapsedMilliseconds} ms）");
            Log($"协议：{resp.Version}  内容类型：{resp.Content.Headers.ContentType}");
            if (resp.Headers.Server.Count > 0)
                Log("服务器：" + string.Join(" ", resp.Headers.Server));
            if (resp.Headers.Location != null)
                Log("跳转到：" + resp.Headers.Location);
        }
        catch (OperationCanceledException)
        {
            Log("请求超时或被取消。");
        }
        catch (Exception ex)
        {
            Log("请求失败：" + ex.Message);
        }
        finally { End(); }
    }

    // ---------------- 路由追踪 ----------------

    private async void Tracert_Click(object sender, RoutedEventArgs e)
    {
        string host = ValidHostOrLog();
        if (host.Length == 0 || !Begin()) return;

        try
        {
            Log("");
            Log($"=== 路由追踪 {host} ===");
            var ct = _cts!.Token;
            var all = new System.Text.StringBuilder();
            int code = await Shell.RunStreamingAsync("tracert.exe", $"-d -h 20 -w 800 {host}",
                line => { all.AppendLine(line); Log(line); }, ct);
            Log(code == 0 ? "—— 追踪完成 ——" : "—— 追踪结束（可能被中断）——");

            string text = all.ToString();
            if (text.Contains("Transmit error", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("传输失败", StringComparison.Ordinal) ||
                text.Contains("请求超时", StringComparison.Ordinal))
            {
                Log("提示：出现「Transmit error / code 5」通常表示 ICMP 被防火墙或安全软件拦截；");
                Log("可尝试以管理员身份运行工具箱，或在防火墙放行 ICMP 后重试。");
            }
        }
        catch (Exception ex)
        {
            Log("追踪失败：" + ex.Message);
        }
        finally { End(); }
    }

    // ---------------- 常用命令 ----------------

    private async void RunCommandAsync(string title, string exe, string args, int maxLines = 400)
    {
        if (!Begin()) return;
        try
        {
            Log("");
            Log($"=== {title} ===");
            var r = await Shell.RunAsync(exe, args, 30000, _cts!.Token);
            var lines = r.Output.Replace("\r\n", "\n").Split('\n');
            int take = Math.Min(lines.Length, maxLines);
            for (int i = 0; i < take; i++) Log(lines[i]);
            if (lines.Length > maxLines) Log($"…… 输出过长，仅显示前 {maxLines} 行（共 {lines.Length} 行）");
        }
        catch (Exception ex)
        {
            Log("执行失败：" + ex.Message);
        }
        finally { End(); }
    }

    private void Ipconfig_Click(object sender, RoutedEventArgs e)
        => RunCommandAsync("ipconfig /all", "ipconfig.exe", "/all");

    private void Netstat_Click(object sender, RoutedEventArgs e)
        => RunCommandAsync("netstat -ano", "netstat.exe", "-ano", 300);

    private void Arp_Click(object sender, RoutedEventArgs e)
        => RunCommandAsync("arp -a", "arp.exe", "-a");

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        if (!Begin()) return;
        try
        {
            Log("");
            Log("=== 刷新 DNS 缓存 ===");
            var r = await Shell.RunAsync("ipconfig.exe", "/flushdns", 15000);
            foreach (var line in r.Output.Split('\n')) Log(line.TrimEnd());
        }
        finally { End(); }
    }

    private void OpenAdapters_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true }); }
        catch (Exception ex) { Log("打开网络连接失败：" + ex.Message); }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true }); }
        catch (Exception ex) { Log("打开网络设置失败：" + ex.Message); }
    }
}
