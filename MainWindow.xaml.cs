using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace network_test;

public partial class MainWindow : Window
{
    private const int SampleWindow = 30; // 图表显示最近 30 秒
    private readonly DispatcherTimer _monitorTimer;
    private readonly List<double> _dlHistory = new();
    private readonly List<double> _ulHistory = new();
    private readonly List<Rectangle> _signalRects = new();
    private long _lastRecv, _lastSent;
    private bool _firstSample = true;
    private bool _updatingNav;
    private bool _checking;
    private int _detailCounter;
    private readonly HttpClient _http = CreateHttpClient();

    /// <summary>带浏览器 UA 的 HttpClient：部分镜像站（如 USTC）会拒绝空 User-Agent 的请求（403）。</summary>
    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return c;
    }
    private CancellationTokenSource? _speedCts;
    private readonly ObservableCollection<CheckResult> _checkResults = new();
    private ChartParts _statChart = null!;
    private ChartParts _speedChart = null!;

    // 最近一次测速的数值结果（用于评分）
    private double _resDlMbps, _resUlMbps;
    private double _resPingMs = -1, _resJitterMs = -1;

    /// <summary>可选的测速端点。{bytes} 会被替换成请求的字节数。</summary>
    private sealed class SpeedEndpoint
    {
        public string Name { get; init; } = "";
        public string Hint { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        /// <summary>为 null 表示该端点不支持上传测速</summary>
        public string? UploadUrl { get; init; }

        public string BuildDownloadUrl(int chunkBytes) => DownloadUrl.Replace("{bytes}", chunkBytes.ToString());
    }

    private static readonly SpeedEndpoint[] Endpoints =
    {
        new()
        {
            Name = "Cloudflare（全球 CDN）· 首选",
            Hint = "默认端点，唯一支持上传测速；海外节点自动就近，国内部分网络会被限速。",
            DownloadUrl = "https://speed.cloudflare.com/__down?bytes={bytes}",
            UploadUrl = "https://speed.cloudflare.com/__up",
        },
        new()
        {
            Name = "南京大学镜像（NJU）",
            Hint = "仅下载测速，国内教育网节点。",
            DownloadUrl = "https://mirrors.nju.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "西安交大镜像（XJTU）",
            Hint = "仅下载测速，国内教育网节点。",
            DownloadUrl = "https://mirrors.xjtu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "中山大学镜像（SYSU）",
            Hint = "仅下载测速，华南教育网节点。",
            DownloadUrl = "https://mirror.sysu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "哈工大镜像（HIT）",
            Hint = "仅下载测速，东北教育网节点。",
            DownloadUrl = "https://mirrors.hit.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "齐鲁工大镜像（QLU）",
            Hint = "仅下载测速，山东教育网节点。",
            DownloadUrl = "https://mirrors.qlu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "吉林大学镜像（JLU）",
            Hint = "仅下载测速，东北教育网节点。",
            DownloadUrl = "https://mirrors.jlu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
        new()
        {
            Name = "华中科大镜像（HUST）",
            Hint = "仅下载测速，武汉教育网节点。",
            DownloadUrl = "https://mirrors.hust.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        },
    };

    private SpeedEndpoint SelectedEndpoint
        => Endpoints[Math.Clamp(EndpointCombo.SelectedIndex, 0, Endpoints.Length - 1)];

    public MainWindow()
    {
        InitializeComponent();
        CheckList.ItemsSource = _checkResults;
        ResetChecksToPending();
        InitSignalBars();

        EndpointCombo.ItemsSource = Endpoints.Select(e => e.Name).ToArray();
        EndpointCombo.SelectedIndex = 0;
        UpdateEndpointHint();

        _statChart = new ChartParts(ChartBorder, GridLinesCanvas, ChartRecvArea, ChartSendArea,
            ChartRecv, ChartSend, ChartMaxText, ChartHalfText);
        _speedChart = new ChartParts(SpeedChartBorder, SpeedGridLinesCanvas, SpeedChartRecvArea, SpeedChartSendArea,
            SpeedChartRecv, SpeedChartSend, SpeedChartMaxText, SpeedChartHalfText);

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += MonitorTick;
        _monitorTimer.Start();
        Loaded += OnLoaded;
        ChartBorder.SizeChanged += (_, _) => DrawChart(_statChart);
        SpeedChartBorder.SizeChanged += (_, _) =>
        {
            if (PageSpeed.Visibility == Visibility.Visible) DrawChart(_speedChart);
        };
        StateChanged += OnWindowStateChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    // ==================== 标题栏 ====================

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // 最大化用 E923（还原图标），普通状态用 E922（最大化图标）
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // 最大化时不希望窗体边框压到任务栏，WindowChrome 已处理，这里只刷新图表
        DrawChart(_statChart);
        if (PageSpeed.Visibility == Visibility.Visible) DrawChart(_speedChart);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Navigate(0);
        _ = UpdateConnectionInfoAsync();
    }

    // ==================== 导航 ====================

    private void Navigate(int index, bool force = false)
    {
        bool noNetwork = !NetworkInterface.GetIsNetworkAvailable();
        // 没有网络时，点其他选项自动跳转到“连接状态检测”
        if (!force && index != 2 && noNetwork)
        {
            index = 2;
            noNetwork = true;
        }

        HintText.Visibility = index == 2 && noNetwork ? Visibility.Visible : Visibility.Collapsed;

        _updatingNav = true;
        try
        {
            NavStatus.IsChecked = index == 0;
            NavSpeed.IsChecked = index == 1;
            NavConnect.IsChecked = index == 2;
        }
        finally { _updatingNav = false; }

        PageStatus.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageSpeed.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageConnect.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        PageTitleText.Text = index switch { 0 => "网络状态", 1 => "网络测速", _ => "连接状态检测" };

        if (index == 0) _ = UpdateConnectionInfoAsync();
        if (index == 1) DrawChart(_speedChart); // 切到测速页时立刻补画一次吞吐量
        // 连接状态检测不再自动执行，只有点击「开始检测」按钮才运行
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingNav) return;
        if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int idx))
            Navigate(idx);
    }

    // 断网时自动跳到“连接状态检测”
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!e.IsAvailable) Navigate(2, force: true);
        });
    }

    // ==================== 页面1：网络状态 ====================

    private void MonitorTick(object? sender, EventArgs e)
    {
        long recv = 0, sent = 0;
        foreach (var nic in GetActiveInterfaces())
        {
            try
            {
                var st = nic.GetIPv4Statistics();
                recv += st.BytesReceived;
                sent += st.BytesSent;
            }
            catch { /* 忽略单个网卡取值失败 */ }
        }

        if (!_firstSample)
        {
            double dl = Math.Max(0, recv - _lastRecv);
            double ul = Math.Max(0, sent - _lastSent);
            RecvSpeedText.Text = FormatBits(dl * 8);
            SendSpeedText.Text = FormatBits(ul * 8);
            _dlHistory.Add(dl);
            _ulHistory.Add(ul);
            while (_dlHistory.Count > SampleWindow)
            {
                _dlHistory.RemoveAt(0);
                _ulHistory.RemoveAt(0);
            }
            DrawChart(_statChart);
            if (PageSpeed.Visibility == Visibility.Visible) DrawChart(_speedChart);
        }
        _lastRecv = recv;
        _lastSent = sent;
        _firstSample = false;

        _detailCounter++;
        if (_detailCounter % 5 == 0) _ = UpdateConnectionInfoAsync(); // 每 5 秒刷新适配器详情
    }

    private static double NiceScale(double maxMbps)
    {
        double[] candidates = { 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        foreach (var c in candidates)
            if (maxMbps <= c) return c;
        return 10000;
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Brush brush)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = 1 };

    /// <summary>一处吞吐量图表的 UI 元素集合（页面1 与 测速页 各一套）。</summary>
    private sealed class ChartParts
    {
        public Border Border { get; }
        public Canvas GridLines { get; }
        public Polygon RecvArea { get; }
        public Polygon SendArea { get; }
        public Polyline Recv { get; }
        public Polyline Send { get; }
        public TextBlock MaxText { get; }
        public TextBlock HalfText { get; }

        public ChartParts(Border border, Canvas gridLines, Polygon recvArea, Polygon sendArea,
            Polyline recv, Polyline send, TextBlock maxText, TextBlock halfText)
        {
            Border = border;
            GridLines = gridLines;
            RecvArea = recvArea;
            SendArea = sendArea;
            Recv = recv;
            Send = send;
            MaxText = maxText;
            HalfText = halfText;
        }
    }

    private void DrawChart(ChartParts p)
    {
        double w = p.Border.ActualWidth, h = p.Border.ActualHeight;
        if (w < 10 || h < 10) return;

        const double topPad = 6, bottomPad = 6;
        double plotH = h - topPad - bottomPad;
        double baseline = h - bottomPad;

        double[] send = _ulHistory.Select(v => v * 8 / 1e6).ToArray(); // Mbps
        double[] recv = _dlHistory.Select(v => v * 8 / 1e6).ToArray();
        double peak = Math.Max(send.DefaultIfEmpty(0).Max(), recv.DefaultIfEmpty(0).Max());
        double scale = NiceScale(peak);

        // 网格线（横向 4 等分，纵向 6 等分 = 每 5 秒）
        p.GridLines.Children.Clear();
        var gb = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32));
        for (int i = 1; i < 4; i++)
        {
            double y = topPad + plotH * i / 4;
            p.GridLines.Children.Add(MakeLine(0, y, w, y, gb));
        }
        for (int i = 1; i < 6; i++)
        {
            double x = w * i / 6;
            p.GridLines.Children.Add(MakeLine(x, 0, x, h, gb));
        }

        // 右侧刻度标签（顶部 = 满刻度，中间 = 半刻度）
        Canvas.SetTop(p.MaxText, 3);
        Canvas.SetRight(p.MaxText, 6);
        p.MaxText.Text = FormatBits(scale * 1e6);
        Canvas.SetTop(p.HalfText, topPad + plotH / 2 - 8);
        Canvas.SetRight(p.HalfText, 6);
        p.HalfText.Text = FormatBits(scale * 1e6 / 2);

        int n = _dlHistory.Count;
        if (n < 2)
        {
            p.Recv.Points = new PointCollection();
            p.Send.Points = new PointCollection();
            p.RecvArea.Points = new PointCollection();
            p.SendArea.Points = new PointCollection();
            return;
        }

        int nMax = Math.Max(n, SampleWindow);
        var recvCurve = new List<Point>();
        var sendCurve = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            double x = (nMax - n + i) * (w - 12) / (nMax - 1) + 6;
            recvCurve.Add(new Point(x, baseline - recv[i] / scale * plotH));
            sendCurve.Add(new Point(x, baseline - send[i] / scale * plotH));
        }

        p.Recv.Points = new PointCollection(recvCurve);
        p.Send.Points = new PointCollection(sendCurve);

        var recvArea = new List<Point>(recvCurve) { new(recvCurve[^1].X, baseline), new(recvCurve[0].X, baseline) };
        var sendArea = new List<Point>(sendCurve) { new(sendCurve[^1].X, baseline), new(sendCurve[0].X, baseline) };
        p.RecvArea.Points = new PointCollection(recvArea);
        p.SendArea.Points = new PointCollection(sendArea);
    }

    private void InitSignalBars()
    {
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle
            {
                Width = 9,
                Height = 8 + i * 5,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 0),
            };
            SignalBars.Children.Add(r);
            _signalRects.Add(r);
        }
    }

    private void SetSignalBars(int bars)
    {
        for (int i = 0; i < 4; i++)
            _signalRects[i].Fill = new SolidColorBrush(
                i < bars ? Color.FromRgb(0xE8, 0xA3, 0x3D) : Color.FromRgb(0x4A, 0x4A, 0x4A));
    }

    private static NetworkInterface? PickPrimaryNic()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault();

    private async Task UpdateConnectionInfoAsync()
    {
        try
        {
            var nic = PickPrimaryNic();
            if (nic == null)
            {
                ConnectionTitleText.Text = "无网络连接";
                AdapterDescText.Text = "";
                AdapterNameText.Text = "—";
                SsidText.Text = "—";
                ConnTypeText.Text = "—";
                Ipv4Text.Text = "—";
                Ipv6Text.Text = "—";
                SetSignalBars(0);
                return;
            }

            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            ConnectionTitleText.Text = wifi ? "Wi-Fi"
                : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "以太网" : "网络";
            AdapterDescText.Text = nic.Description;
            AdapterNameText.Text = nic.Name;

            var props = nic.GetIPProperties();
            Ipv4Text.Text = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
            Ipv6Text.Text = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)?
                .Address.ToString() ?? "—";

            if (wifi)
            {
                var attr = await Task.Run(() =>
                {
                    Guid? gid = Guid.TryParse(nic.Id.Trim('{', '}'), out var g) ? g : null;
                    return gid.HasValue ? WlanHelper.GetCurrentConnection(gid.Value) : null;
                });
                if (attr != null)
                {
                    SsidText.Text = string.IsNullOrEmpty(attr.Ssid) ? "—" : attr.Ssid;
                    ConnTypeText.Text = attr.PhyType;
                    SetSignalBars(attr.Quality >= 75 ? 4 : attr.Quality >= 50 ? 3 : attr.Quality >= 25 ? 2 : attr.Quality > 0 ? 1 : 0);
                }
                else
                {
                    SsidText.Text = "—";
                    ConnTypeText.Text = "Wi-Fi";
                    SetSignalBars(0);
                }
            }
            else
            {
                SsidText.Text = "—";
                ConnTypeText.Text = "以太网";
                SetSignalBars(0);
            }
        }
        catch { /* 刷新失败不影响程序运行 */ }
    }

    // ==================== 页面2：网络测速 ====================

    private void EndpointCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointHintText == null) return; // 初始化早期可能尚未就绪
        UpdateEndpointHint();
    }

    private void UpdateEndpointHint()
    {
        var ep = SelectedEndpoint;
        EndpointHintText.Text = ep.Hint;
    }

    private async void StartSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) { Navigate(2); return; }

        _speedCts = new CancellationTokenSource();
        var ct = _speedCts.Token;
        StartSpeedTestButton.IsEnabled = false;
        EndpointCombo.IsEnabled = false;
        CancelSpeedTestButton.Visibility = Visibility.Visible;
        SpeedDlText.Text = "—";
        SpeedUlText.Text = "—";
        SpeedPingText.Text = "— ms";
        SpeedJitterText.Text = "抖动 — ms";
        DlDetailText.Text = "—";
        UlDetailText.Text = "—";
        ResetScore();
        _resDlMbps = 0;
        _resUlMbps = -1;
        _resPingMs = -1;
        _resJitterMs = -1;
        var endpoint = SelectedEndpoint;
        try
        {
            SpeedStatusText.Text = "正在测试延迟…";
            await RunPingTest(ct);

            if (!ct.IsCancellationRequested)
            {
                SpeedStatusText.Text = $"正在测试下载速度…（{endpoint.Name}）";
                SpeedProgress.Value = 0;
                await RunDownloadTest(ct, endpoint);
            }

            if (!ct.IsCancellationRequested)
            {
                if (endpoint.UploadUrl == null)
                {
                    // 该端点没有上传接口：直接跳过上传测速，不改用其他节点
                    SpeedUlText.Text = "未测试";
                    UlDetailText.Text = "该测速端点不提供上传接口，已跳过上传测速";
                }
                else
                {
                    SpeedStatusText.Text = $"正在测试上传速度…（{endpoint.Name}）";
                    SpeedProgress.Value = 0;
                    await RunUploadTest(ct, endpoint);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                string score = ShowScore();
                SpeedStatusText.Text = endpoint.UploadUrl == null
                    ? $"测速完成（该端点不含上传测速），综合评分 {score}"
                    : $"测速完成，综合评分 {score}";
            }
            else
            {
                SpeedStatusText.Text = "测速已取消。";
            }
        }
        catch (OperationCanceledException)
        {
            SpeedStatusText.Text = "测速已取消。";
        }
        catch (Exception ex)
        {
            SpeedStatusText.Text = $"测速失败：{ex.Message}";
        }
        finally
        {
            StartSpeedTestButton.IsEnabled = true;
            EndpointCombo.IsEnabled = true;
            CancelSpeedTestButton.Visibility = Visibility.Collapsed;
            SpeedProgress.Value = 0;
            _speedCts?.Dispose();
            _speedCts = null;
        }
    }

    private void CancelSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        _speedCts?.Cancel();
    }

    private async Task RunPingTest(CancellationToken ct)
    {
        var times = new List<long>();
        for (int i = 0; i < 4; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var p = new Ping();
                var reply = await p.SendPingAsync("223.5.5.5", 2000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch { /* 单次 ping 失败忽略 */ }
            await Task.Delay(150, ct);
        }
        if (times.Count == 0)
        {
            SpeedPingText.Text = "超时";
            SpeedJitterText.Text = "抖动 — ms";
            _resPingMs = -1;
            _resJitterMs = -1;
            return;
        }
        double avg = times.Average();
        double jitter = times.Count > 1
            ? times.Skip(1).Zip(times, (a, b) => Math.Abs(a - b)).Average()
            : 0;
        _resPingMs = avg;
        _resJitterMs = jitter;
        SpeedPingText.Text = $"{avg:F0} ms";
        SpeedJitterText.Text = $"抖动 {jitter:F0} ms";
    }

    private async Task RunDownloadTest(CancellationToken ct, SpeedEndpoint endpoint)
    {
        const int chunkBytes = 10_000_000;
        const int durationSec = 10;
        string url = endpoint.BuildDownloadUrl(chunkBytes);
        long total = 0;
        var sw = Stopwatch.StartNew();
        var buf = new byte[81920];
        while (sw.Elapsed.TotalSeconds < durationSec && !ct.IsCancellationRequested)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                double inst = total / sw.Elapsed.TotalSeconds;
                SpeedDlText.Text = FormatSpeed(inst);
                SpeedProgress.Value = Math.Min(100, sw.Elapsed.TotalSeconds / durationSec * 100);
                if (sw.Elapsed.TotalSeconds >= durationSec) break;
            }
        }
        sw.Stop();
        if (total > 0 && sw.Elapsed.TotalSeconds > 0.2)
        {
            double avg = total / sw.Elapsed.TotalSeconds;
            _resDlMbps = avg * 8 / 1_000_000;
            SpeedDlText.Text = FormatSpeed(avg);
            DlDetailText.Text = $"共下载 {FormatBytes(total)}，约 {avg * 8 / 1_000_000:F1} Mbps";
        }
    }

    private async Task RunUploadTest(CancellationToken ct, SpeedEndpoint endpoint)
    {
        const int chunkBytes = 5_000_000;
        const int durationSec = 8;
        string uploadUrl = endpoint.UploadUrl!; // 调用前已确认该端点支持上传
        var data = new byte[chunkBytes];
        Random.Shared.NextBytes(data);
        long total = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < durationSec && !ct.IsCancellationRequested)
        {
            using var content = new ByteArrayContent(data);
            using var resp = await _http.PostAsync(uploadUrl, content, ct);
            resp.EnsureSuccessStatusCode();
            total += chunkBytes;
            double inst = total / sw.Elapsed.TotalSeconds;
            SpeedUlText.Text = FormatSpeed(inst);
            SpeedProgress.Value = Math.Min(100, sw.Elapsed.TotalSeconds / durationSec * 100);
        }
        sw.Stop();
        if (total > 0 && sw.Elapsed.TotalSeconds > 0.2)
        {
            double avg = total / sw.Elapsed.TotalSeconds;
            _resUlMbps = avg * 8 / 1_000_000;
            SpeedUlText.Text = FormatSpeed(avg);
            UlDetailText.Text = $"共上传 {FormatBytes(total)}，约 {avg * 8 / 1_000_000:F1} Mbps";
        }
    }

    // ==================== 网络评分 ====================

    /// <summary>下载速度得分（满分 45）：5 Mbps 起评分，200 Mbps 拿满分</summary>
    private static int ScoreDownload(double mbps)
        => (int)Math.Round(45 * Math.Clamp(Math.Log10(1 + Math.Max(0, mbps)) / Math.Log10(1 + 200), 0, 1));

    /// <summary>上传速度得分（满分 20）：50 Mbps 拿满分</summary>
    private static int ScoreUpload(double mbps)
        => (int)Math.Round(20 * Math.Clamp(Math.Log10(1 + Math.Max(0, mbps)) / Math.Log10(1 + 50), 0, 1));

    /// <summary>延迟得分（满分 25）：≤20 ms 满分，≥200 ms 归零</summary>
    private static int ScorePing(double ms)
        => ms < 0 ? 0 : (int)Math.Round(25 * Math.Clamp(1 - (ms - 20) / 180, 0, 1));

    /// <summary>抖动得分（满分 10）：≤5 ms 满分，≥50 ms 归零</summary>
    private static int ScoreJitter(double ms)
        => ms < 0 ? 0 : (int)Math.Round(10 * Math.Clamp(1 - (ms - 5) / 45, 0, 1));

    private void ResetScore()
    {
        var grey = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        ScoreText.Text = "—";
        ScoreText.Foreground = grey;
        GradeText.Text = "待测速";
        GradeText.Foreground = grey;
        GradeBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
        ScoreBar.Value = 0;
        ScoreBar.Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x97));
        ScoreCommentText.Text = "完成一次测速后，会根据下载速度、上传速度、延迟和抖动给出综合评分。";
        ScoreDlText.Text = "下载 —/45";
        ScoreUlText.Text = "上传 —/20";
        ScorePingText.Text = "延迟 —/25";
        ScoreJitterText.Text = "抖动 —/10";
    }

    /// <summary>按本次测速结果算分并刷新评分卡，返回“86 分（良好）”这样的描述。</summary>
    private string ShowScore()
    {
        int sDl = ScoreDownload(_resDlMbps);
        int sPing = ScorePing(_resPingMs);
        int sJit = ScoreJitter(_resJitterMs);
        // 端点为镜像站时未测上传：剩余 80 分按比例折算成百分制，避免把“没测”算成 0 分
        bool hasUpload = _resUlMbps >= 0;
        int sUl = hasUpload ? ScoreUpload(_resUlMbps) : 0;
        int sum = sDl + sUl + sPing + sJit;
        int total = hasUpload ? sum : (int)Math.Round(sum * 100.0 / 80);

        string grade, comment;
        Color color;
        if (total >= 90)
        {
            grade = "优秀"; color = Color.FromRgb(0x6F, 0xCF, 0x97);
            comment = "适合 4K 视频、云游戏与大型文件传输，网络质量很好。";
        }
        else if (total >= 75)
        {
            grade = "良好"; color = Color.FromRgb(0x6F, 0xCF, 0x97);
            comment = "适合高清视频、视频会议与日常办公，连接稳定。";
        }
        else if (total >= 60)
        {
            grade = "一般"; color = Color.FromRgb(0xF0, 0xB4, 0x29);
            comment = "满足网页浏览与标清视频，多人共用或大文件传输会比较吃力。";
        }
        else if (total >= 40)
        {
            grade = "较差"; color = Color.FromRgb(0xFF, 0x9F, 0x5A);
            comment = "速度或延迟不理想，建议靠近路由器、重启设备或更换网络环境。";
        }
        else
        {
            grade = "差"; color = Color.FromRgb(0xFF, 0x6B, 0x6B);
            comment = "网络质量较差，下载上传速度低且延迟高，建议检查线路或联系运营商。";
        }

        var brush = new SolidColorBrush(color);
        ScoreText.Text = total.ToString();
        ScoreText.Foreground = brush;
        GradeText.Text = grade;
        GradeText.Foreground = brush;
        GradeBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        ScoreBar.Value = total;
        ScoreBar.Foreground = brush;
        ScoreCommentText.Text = hasUpload ? comment : comment + "（本次使用的端点不含上传测速，评分只按下载、延迟和抖动折算。）";
        ScoreDlText.Text = $"下载 {sDl}/45";
        ScoreUlText.Text = hasUpload ? $"上传 {sUl}/20" : "上传 未测";
        ScorePingText.Text = $"延迟 {sPing}/25";
        ScoreJitterText.Text = $"抖动 {sJit}/10";

        return $"{total} 分（{grade}）";
    }

    // ==================== 页面3：连接状态检测 ====================

    /// <summary>检查前先把 6 个检测项以灰色「待检测」状态列出。</summary>
    private void ResetChecksToPending()
    {
        _checkResults.Clear();
        string[] names =
        {
            "本地网卡状态", "网关连通性", "公共 DNS", "互联网连通性 (Ping)", "DNS 解析", "网页访问 (HTTP)",
        };
        foreach (string name in names)
            _checkResults.Add(CheckResult.Create("待检测", name, "尚未检测，点击「开始检测」以检测网络状态。"));
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        _ = RunConnectionCheck();
    }

    private async Task RunConnectionCheck()
    {
        if (_checking) return;
        _checking = true;
        RecheckButton.IsEnabled = false;
        _checkResults.Clear();
        SetBanner("正在检测…", "#2B2B2B", "#C8C8C8");
        try
        {
            int ok = 0, total = 0;
            void Add(string icon, string name, string detail)
            {
                _checkResults.Add(CheckResult.Create(icon, name, detail));
                if (icon == "正常") ok++;
                total++;
            }

            // 1. 本地网卡
            {
                bool any = NetworkInterface.GetIsNetworkAvailable();
                int upCount = GetActiveInterfaces().Count();
                Add(any && upCount > 0 ? "正常" : "异常", "本地网卡状态",
                    $"可用网络接口 {upCount} 个，系统网络{(any ? "可用" : "不可用")}");
            }

            // 2. 网关连通性
            {
                var gw = GetGateway();
                if (gw == null)
                    Add("警告", "网关连通性", "未获取到默认网关地址");
                else
                {
                    var (s, detail) = await PingHost(gw.ToString(), 2000);
                    Add(s, $"网关连通性 ({gw})", detail);
                }
            }

            // 3. 公共 DNS
            {
                var (s, detail) = await PingHost("223.5.5.5", 2000);
                Add(s, "公共 DNS (223.5.5.5)", detail);
            }

            // 4. 互联网连通
            {
                var (s, detail) = await PingHost("www.baidu.com", 3000);
                Add(s, "互联网连通性 (Ping)", detail);
            }

            // 5. DNS 解析
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync("www.baidu.com");
                    Add("正常", "DNS 解析",
                        $"www.baidu.com 解析到 {string.Join(", ", addrs.Select(a => a.ToString()).Take(3))}");
                }
                catch (Exception ex)
                {
                    Add("异常", "DNS 解析", $"解析失败：{ex.Message}");
                }
            }

            // 6. HTTP 访问
            {
                try
                {
                    using var resp = await _http.GetAsync("https://www.baidu.com", HttpCompletionOption.ResponseHeadersRead);
                    Add(resp.IsSuccessStatusCode ? "正常" : "异常", "网页访问 (HTTP)",
                        $"https://www.baidu.com 返回状态码 {(int)resp.StatusCode}");
                }
                catch (Exception ex)
                {
                    Add("异常", "网页访问 (HTTP)", $"访问失败：{ex.Message}");
                }
            }

            // 汇总
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                SetBanner($"检测结果：当前没有可用的网络连接（{ok}/{total} 项通过）", "#3B1E22", "#E57373");
                HintText.Visibility = Visibility.Visible;
            }
            else if (ok == total)
            {
                SetBanner($"检测结果：网络连接正常（{ok}/{total} 项通过）", "#1E3B2A", "#6FCF97");
            }
            else if (ok >= 2)
            {
                SetBanner($"检测结果：网络受限或不稳定（{ok}/{total} 项通过）", "#3A2F16", "#F0B429");
            }
            else
            {
                SetBanner($"检测结果：网络连接异常（{ok}/{total} 项通过）", "#3B1E22", "#E57373");
            }
        }
        finally
        {
            RecheckButton.IsEnabled = true;
            _checking = false;
        }
    }

    private void SetBanner(string text, string bgHex, string fgHex)
    {
        OverallBanner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgHex));
        OverallText.Text = text;
        OverallText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fgHex));
    }

    private async Task<(string, string)> PingHost(string? host, int timeoutMs)
    {
        if (string.IsNullOrEmpty(host)) return ("警告", "地址为空，跳过");
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success
                ? ("正常", $"响应 {reply.RoundtripTime} ms")
                : ("异常", $"失败：{reply.Status}");
        }
        catch (Exception ex)
        {
            return ("异常", $"失败：{ex.Message}");
        }
    }

    // ==================== 工具方法 ====================

    private static IEnumerable<NetworkInterface> GetActiveInterfaces()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    private static IPAddress? GetGateway()
        => GetActiveInterfaces()
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(g => g.Address)
            .FirstOrDefault();

    private static string FormatBits(double bitsPerSec)
    {
        string[] units = { "bps", "Kbps", "Mbps", "Gbps" };
        double v = Math.Max(0, bitsPerSec);
        int i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        string fmt = v < 10 && i >= 2 ? "F1" : "F0";
        return $"{v.ToString(fmt)} {units[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double v = Math.Max(0, bytesPerSecond);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F2} {units[i]}";
    }

    // ==================== 数据模型 ====================

    private class CheckResult
    {
        public string Icon { get; set; } = "";
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";

        /// <summary>状态徽标的文字色</summary>
        public Brush Foreground { get; set; } = NormalText;
        /// <summary>详情文字色</summary>
        public Brush DetailBrush { get; set; } = NormalDetail;
        /// <summary>检测项名称色</summary>
        public Brush NameBrush { get; set; } = NameNormal;
        /// <summary>左侧状态色条</summary>
        public Brush Accent { get; set; } = NormalAccent;
        /// <summary>状态徽标底色</summary>
        public Brush BadgeBg { get; set; } = NormalBadge;
        /// <summary>整行底色</summary>
        public Brush RowBg { get; set; } = Brushes.Transparent;

        private static readonly Brush NormalText = Freeze("#6FCF97");
        private static readonly Brush NormalDetail = Freeze("#AFCBBB");
        private static readonly Brush NameNormal = Freeze("#E6E6E6");
        private static readonly Brush NamePending = Freeze("#8A8A8A");
        private static readonly Brush NormalAccent = Freeze("#3E8E63");
        private static readonly Brush NormalBadge = Freeze("#1B3327");

        private static readonly Brush PendingText = Freeze("#808080");
        private static readonly Brush PendingDetail = Freeze("#6E6E6E");
        private static readonly Brush PendingAccent = Freeze("#4A4A4A");
        private static readonly Brush PendingBadge = Freeze("#2A2A2A");

        private static readonly Brush WarnText = Freeze("#F0B429");
        private static readonly Brush WarnDetail = Freeze("#E0CB9A");
        private static readonly Brush WarnAccent = Freeze("#C8922A");
        private static readonly Brush WarnBadge = Freeze("#3A2F16");
        private static readonly Brush WarnRow = Freeze("#2A2418");

        private static readonly Brush ErrorText = Freeze("#FF6B6B");
        private static readonly Brush ErrorDetail = Freeze("#FFB4B4");
        private static readonly Brush ErrorAccent = Freeze("#E04B4B");
        private static readonly Brush ErrorBadge = Freeze("#43222A");
        private static readonly Brush ErrorRow = Freeze("#2B1D20");

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>按检测结论生成带配色的结果行。</summary>
        public static CheckResult Create(string icon, string name, string detail)
        {
            var r = new CheckResult { Icon = icon, Name = name, Detail = detail };
            if (icon == "待检测")
            {
                r.Foreground = PendingText;
                r.DetailBrush = PendingDetail;
                r.NameBrush = NamePending;
                r.Accent = PendingAccent;
                r.BadgeBg = PendingBadge;
            }
            else if (icon == "异常")
            {
                r.Foreground = ErrorText;
                r.DetailBrush = ErrorDetail;
                r.Accent = ErrorAccent;
                r.BadgeBg = ErrorBadge;
                r.RowBg = ErrorRow;
            }
            else if (icon == "警告")
            {
                r.Foreground = WarnText;
                r.DetailBrush = WarnDetail;
                r.Accent = WarnAccent;
                r.BadgeBg = WarnBadge;
                r.RowBg = WarnRow;
            }
            return r;
        }
    }
}

// ==================== Windows WLAN API 封装 ====================

internal class WifiAttributes
{
    public string Ssid { get; set; } = "";
    public string PhyType { get; set; } = "";
    public int Quality { get; set; }
}

internal static class WlanHelper
{
    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);
    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid interfaceGuid,
        uint opCode, IntPtr pReserved, out uint pdwDataSize, ref IntPtr ppData, IntPtr pWlanOpcodeValueType);

    private const int ERROR_SUCCESS = 0;
    private const uint WLAN_MAX_NAME_LENGTH = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
        public uint dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public uint dot11PhyType;
        public uint ulPhyId;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        public int bSecurityEnabled;
        public int bDot1xEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public uint isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    /// <summary>查询无线网卡当前连接信息（SSID、协议、信号强度）。</summary>
    public static WifiAttributes? GetCurrentConnection(Guid interfaceGuid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != ERROR_SUCCESS) return null;
            Guid g = interfaceGuid;
            // 不同系统上 wlan_intf_opcode_current_connection 的取值有差异，逐个探测并用字段合法性校验
            WifiAttributes? best = null;
            foreach (uint opcode in new uint[] { 4, 7, 3 })
            {
                IntPtr dataPtr = IntPtr.Zero;
                int err = WlanQueryInterface(handle, ref g, opcode, IntPtr.Zero, out uint size, ref dataPtr, IntPtr.Zero);
                if (err != ERROR_SUCCESS || dataPtr == IntPtr.Zero) continue;
                try
                {
                    if (size < Marshal.SizeOf<WLAN_CONNECTION_ATTRIBUTES>()) continue;
                    var attr = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                    var assoc = attr.wlanAssociationAttributes;
                    // 布局合法性校验：必须解析出非空 SSID，且信号质量、PHY 类型在合理范围内
                    if (assoc.uSSIDLength is < 1 or > 32) continue;
                    if (assoc.wlanSignalQuality > 100) continue;
                    if (assoc.dot11PhyType > 11 && assoc.dot11PhyType != 0x80000000u) continue;
                    int len = (int)assoc.uSSIDLength;
                    string ssid = assoc.ucSSID != null
                        ? Encoding.UTF8.GetString(assoc.ucSSID, 0, len)
                        : "";
                    if (string.IsNullOrWhiteSpace(ssid)) continue;
                    best = new WifiAttributes
                    {
                        Ssid = ssid,
                        PhyType = PhyTypeName(assoc.dot11PhyType),
                        Quality = (int)Math.Min(100, assoc.wlanSignalQuality),
                    };
                    break;
                }
                finally { WlanFreeMemory(dataPtr); }
            }
            return best;
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero); }
    }

    private static string PhyTypeName(uint t) => t switch
    {
        1 => "802.11 (FHSS)",
        2 => "802.11 (DSSS)",
        3 => "802.11 (IR)",
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n",
        8 => "802.11ac",
        9 => "802.11ad",
        10 => "802.11ax",
        11 => "802.11be",
        0x80000000u => "厂商专用",
        _ => "802.11",
    };
}
