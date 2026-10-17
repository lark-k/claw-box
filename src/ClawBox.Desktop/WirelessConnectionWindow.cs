using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop;

public sealed class WirelessConnectionWindow : Window
{
    private readonly DesktopSettings settings;
    private readonly WirelessBinding binding;
    private readonly Func<CancellationToken, Task<string?>> connect;
    private readonly Action save;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private WirelessSession? session;
    private readonly StackPanel devicesPanel = new(), networkPanel = new(), resultPanel = new();
    private readonly ListBox devices = new() { MinHeight = 100, MaxHeight = 170 };
    private readonly ComboBox networks = new() { MinWidth = 320, Margin = new(0, 8, 0, 12) };
    private readonly PasswordBox password = new() { Padding = new(12, 8, 12, 8) };
    private readonly TextBlock status = new() { Text = "给盒子通电并靠近电脑，点击搜索。首次配网也不需要网线。", TextWrapping = TextWrapping.Wrap, LineHeight = 24 };
    private readonly TextBlock progress = new() { Text = "1  发现盒子     →     2  选择 Wi-Fi     →     3  完成连接", Foreground = Brushes.Gray, Margin = new(0, 16, 0, 22) };
    private readonly TextBlock connectedNetwork = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    private readonly Button close = new() { Content = "关闭", MinWidth = 88 };
    private readonly TextBox address = new() { MinWidth = 220 };
    private readonly TextBlock passwordHint = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 14) };
    private WirelessStatus? currentNetwork;
    private readonly bool preview;
    private string? readyAddress;
    private bool busy, closing;
    public WirelessConnectionWindow(DesktopSettings settings, Func<CancellationToken, Task<string?>> connect, Action save, bool preview = false)
    {
        this.settings = settings; binding = new(settings); this.connect = connect; this.save = save; this.preview = preview;
        Title = "Quantum Harness · 无线连接"; Width = 740; Height = 690; MinWidth = 620; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14;
        if (!preview) { Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24); }
        DockPanel root = new(); Content = new Border { Background = Background, Padding = new Thickness(28), Child = root };
        StackPanel heading = new(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "无线连接", FontSize = 27, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "通过蓝牙设置盒子的 Wi-Fi，之后使用 Wi-Fi 连接。换网络保留原配对。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 8, 0, 0) });
        heading.Children.Add(progress);
        StackPanel footer = new(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(245, 248, 253)), CornerRadius = new(8), Padding = new(14), Margin = new(0, 16, 0, 10), Child = status });
        close.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(close);
        StackPanel body = new(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(devicesPanel); body.Children.Add(networkPanel); body.Children.Add(resultPanel);
        Label(devicesPanel, "附近的盒子"); devicesPanel.Children.Add(devices);
        WrapPanel findActions = new() { Margin = new(0, 12, 0, 16) }; devicesPanel.Children.Add(findActions);
        Button(findActions, "搜索附近盒子", () => Execute(Discover));
        Button(findActions, "连接选中的盒子", () => Execute(Select), true);
        devicesPanel.Children.Add(new TextBlock { Text = "请选择自己的盒子。已有配对会自动验证身份；选择其他盒子不会替换原配对。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        Label(networkPanel, "盒子的 Wi-Fi"); networkPanel.Children.Add(connectedNetwork); networkPanel.Children.Add(networks);
        Label(networkPanel, "Wi-Fi 密码"); networkPanel.Children.Add(password);
        networkPanel.Children.Add(passwordHint);
        networks.SelectionChanged += (_, _) =>
        {
            password.Clear();
            passwordHint.Text = networks.SelectedItem is WirelessNetwork { Saved: true }
                ? "盒子已记住此 Wi-Fi 的密码，留空即可连接。密码变更时在此输入新密码。"
                : "成功连接后盒子会记住密码，下次无需重复输入。电脑也需连接同一局域网。";
        };
        WrapPanel wifiActions = new(); networkPanel.Children.Add(wifiActions);
        Button(wifiActions, "刷新网络", () => Execute(Refresh));
        Button(wifiActions, "连接此 Wi-Fi", () => Execute(Configure), true);
        Button(wifiActions, "使用盒子当前 Wi-Fi", () => Execute(UseCurrent));
        Label(resultPanel, "无线网络已就绪");
        resultPanel.Children.Add(new TextBlock { Text = "连接后可以拔掉网线。聊天记录、工作区和已有配对都会保留。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) });
        Button(resultPanel, "连接并开始使用", () => Execute(Finish), true);
        Expander advanced = new() { Header = "已联网的盒子／蓝牙不可用", Margin = new(0, 24, 0, 0) }; body.Children.Add(advanced);
        StackPanel manual = new(); advanced.Content = manual;
        manual.Children.Add(new TextBlock { Text = "已有配对可直接使用无线地址重连，无需再次配网。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 8, 0, 8) });
        address.Text = settings.WirelessHost; manual.Children.Add(address);
        Button(manual, "使用已有配对连接此地址", () => Execute(Manual));
        networkPanel.Visibility = resultPanel.Visibility = Visibility.Collapsed;
        close.Click += (_, _) => { if (busy) operation?.Cancel(); else Close(); };
        Closing += (_, e) => { if (busy) { e.Cancel = true; closing = true; operation?.Cancel(); } };
        Closed += (_, _) => { lifetime.Cancel(); session?.Dispose(); lifetime.Dispose(); };
    }
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 17, Margin = new(0, 0, 0, 10) });
    private void Button(Panel panel, string text, Func<Task> action, bool primary = false)
    {
        Button button = new() { Content = text, Padding = new(16, 9, 16, 9), Margin = new(0, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
        if (primary && TryFindResource("Primary") is Style style) button.Style = style;
        button.Click += async (_, _) => { if (!preview) await action(); }; panel.Children.Add(button);
    }
    private async Task Execute(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; close.Content = "停止";
        devicesPanel.IsEnabled = networkPanel.IsEnabled = resultPanel.IsEnabled = false;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation.CancelAfter(TimeSpan.FromMinutes(4));
        try { await action(operation.Token); }
        catch (OperationCanceledException) { session?.Dispose(); session = null; devicesPanel.Visibility = Visibility.Visible; status.Text = "操作已停止。若已发送网络配置，请重新连接盒子查询状态，软件不会自动重发。"; }
        catch (WirelessException error) { devicesPanel.Visibility = Visibility.Visible; status.Text = error.Message; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { session?.Dispose(); session = null; devicesPanel.Visibility = Visibility.Visible; status.Text = "连接未完成。请检查蓝牙、盒子电源和电脑网络后重试。已有配对与聊天记录保留。"; }
        finally
        {
            operation.Dispose(); operation = null; busy = false;
            if (Equals(close.Content, "停止")) close.Content = "关闭";
            devicesPanel.IsEnabled = networkPanel.IsEnabled = resultPanel.IsEnabled = true;
            if (closing) Close();
        }
    }
    private async Task Discover(CancellationToken token)
    {
        status.Text = "正在搜索附近的盒子，请保持盒子通电、电脑蓝牙开启…";
        devices.ItemsSource = await BluetoothNetwork.Discover(token);
        status.Text = devices.Items.Count == 0 ? "没有发现盒子。请检查蓝牙和盒子电源，然后重新搜索。" : $"发现 {devices.Items.Count} 台盒子，请选择自己的设备并点击连接。";
    }
    private async Task Select(CancellationToken token)
    {
        if (devices.SelectedItem is not NearbyBoard board) { status.Text = "请先选择一台盒子。"; return; }
        session?.Dispose(); session = null;
        status.Text = "正在连接并验证盒子身份…";
        BluetoothNetwork link = await BluetoothNetwork.Connect(board.Address, token);
        try
        {
            JsonElement info = await link.Info(token);
            string? expected = binding.ExpectedIdentity;
            if (expected is not null && expected != info.GetProperty("identity").GetString()) throw new WirelessException("identity_mismatch");
            bool claim = expected is null && info.GetProperty("enrollment").GetBoolean();
            string key = await binding.Prepare(token);
            WirelessProtocol protocol = new(key, expected, claim);
            session = new(link, protocol);
            await session.Authenticate(token); binding.Pin(session.HostKey);
            networkPanel.Visibility = Visibility.Visible; devicesPanel.Visibility = Visibility.Collapsed;
            progress.Text = "✓  盒子身份已确认     →     2  选择 Wi-Fi     →     3  完成连接";
            await Refresh(token);
        }
        catch { link.Dispose(); session?.Dispose(); session = null; throw; }
    }
    private async Task Refresh(CancellationToken token)
    {
        if (session is null) { devicesPanel.Visibility = Visibility.Visible; status.Text = "请重新搜索并连接盒子。"; return; }
        status.Text = "正在读取盒子网络并扫描 Wi-Fi…";
        WirelessStatus current = await session.Status(token); currentNetwork = current;
        connectedNetwork.Text = current.Connected ? "当前已连接：" + current.Ssid + "。可以直接使用，也可以更换下面的网络。" : "盒子尚未联网，请选择 Wi-Fi。";
        WirelessNetwork[] scanned = await session.Scan(token); networks.ItemsSource = scanned;
        networks.SelectedItem = scanned.FirstOrDefault(n => n.IsCurrent(current)) ?? scanned.FirstOrDefault(n => n.Saved) ?? scanned.FirstOrDefault();
        status.Text = "已保存的 Wi-Fi 无需再次输入密码。也可选择其他网络，首次连接时输入密码。";
    }
    private async Task Configure(CancellationToken token)
    {
        if (session is null) { await Refresh(token); return; }
        if (networks.SelectedItem is not WirelessNetwork network) { status.Text = "请先选择 Wi-Fi。"; return; }
        if (password.Password.Length == 0 && currentNetwork is { Connected: true } current && network.IsCurrent(current))
        { await UseCurrent(token); return; }
        if (network.Saved && password.Password.Length == 0)
        {
            status.Text = "正在使用盒子已保存的密码连接 Wi-Fi…";
            JsonElement saved = await session.Send(new { op = "connect_saved", ssid = network.Ssid, ssid_bytes = network.Bytes, security = network.Secured ? "wpa-psk" : "open" }, token);
            Ready(saved.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()!).FirstOrDefault());
            return;
        }
        if (network.Secured && password.Password.Length < 8) { status.Text = "请输入至少 8 位 Wi-Fi 密码。"; return; }
        string value = password.Password; password.Clear(); status.Text = "正在让盒子连接 Wi-Fi，请稍候…";
        JsonElement result = await session.Send(new
        {
            op = "configure",
            ssid = network.Ssid,
            ssid_bytes = network.Bytes,
            password = network.Secured ? value : "",
            security = network.Secured ? "wpa-psk" : "open"
        }, token);
        Ready(result.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()!).FirstOrDefault());
    }
    private async Task UseCurrent(CancellationToken token)
    {
        if (session is null) { await Refresh(token); return; }
        WirelessStatus current = await session.Status(token);
        if (!current.Connected) { status.Text = "盒子尚未连接 Wi-Fi，请先选择网络并连接。"; return; }
        Ready(current.Addresses.FirstOrDefault());
    }
    private void Ready(string? host)
    {
        if (string.IsNullOrEmpty(host)) { status.Text = "盒子尚未取得无线地址，请稍候再检查。"; return; }
        readyAddress = host; networkPanel.Visibility = Visibility.Collapsed; resultPanel.Visibility = Visibility.Visible;
        progress.Text = "✓  盒子身份已确认     →     ✓  Wi-Fi 已连接     →     3  完成连接";
        status.Text = "盒子无线地址：" + host + "。请确认电脑也连接同一局域网，再点击“连接并开始使用”。";
    }
    private async Task Finish(CancellationToken token)
    {
        if (session is null || readyAddress is null) { status.Text = "请先连接盒子并确认无线网络。"; return; }
        string? gateway = null;
        if (!binding.Complete)
        {
            status.Text = "正在完成首次连接设置，请保持盒子通电…";
            JsonElement result = await session.Send(new { op = "desktop_setup", worker_token = binding.Worker() }, token);
            gateway = result.GetProperty("gateway_token").GetString();
        }
        await Switch(readyAddress, session.HostKey, gateway, token);
    }
    private async Task Manual(CancellationToken token)
    {
        if (!binding.Complete) { status.Text = "首次连接需要先通过蓝牙确认盒子并配网。"; return; }
        BoardConnectionProfile profile = BoardConnectionProfile.Load(settings.ProfilePath);
        string[] known = File.ReadAllText(profile.KnownHostsFile).Trim().Split(' ');
        await Switch(address.Text.Trim(), known[1] + " " + known[2], null, token);
    }
    private async Task Switch(string host, string hostKey, string? gateway, CancellationToken token)
    {
        ConnectionSnapshot snapshot = new(settings);
        string oldMode = settings.ConnectionMode, oldWireless = settings.WirelessHost, oldWired = settings.WiredHost;
        int oldWiredPort = settings.WiredPort;
        try
        {
            if (oldMode == "wired" && File.Exists(settings.ProfilePath))
            { BoardConnectionProfile old = BoardConnectionProfile.Load(settings.ProfilePath); settings.WiredHost = old.BoxHost; settings.WiredPort = old.SshPort; }
            status.Text = "正在验证无线地址和原设备身份，建立聊天连接…";
            await binding.Save(host, hostKey, gateway, token);
            settings.WirelessHost = host; settings.WirelessPort = 22; settings.ConnectionMode = "wireless";
            string? error = await connect(token);
            if (error is not null) throw new InvalidOperationException(error);
            status.Text = "无线连接成功，可以拔掉网线。已有配对、聊天和工作区均已保留。";
            progress.Text = "✓  盒子身份已确认     →     ✓  Wi-Fi 已连接     →     ✓  已连接";
            close.Content = "返回对话";
        }
        catch
        {
            snapshot.Restore(); settings.ConnectionMode = oldMode; settings.WirelessHost = oldWireless;
            settings.WiredHost = oldWired; settings.WiredPort = oldWiredPort; save(); throw;
        }
    }
    internal void Preview(string stage)
    {
        if (stage == "devices") { devices.ItemsSource = new[] { new NearbyBoard(1, "ClawBox · 我的盒子", -50) }; devices.SelectedIndex = 0; status.Text = "发现 1 台盒子，请选择自己的设备并点击连接。"; }
        if (stage is "network" or "ready")
        {
            progress.Text = "✓  盒子身份已确认     →     2  选择 Wi-Fi     →     3  完成连接";
            devicesPanel.Visibility = Visibility.Collapsed; networkPanel.Visibility = Visibility.Visible;
            connectedNetwork.Text = "当前已连接：家庭 Wi-Fi。可直接使用，也可以更换网络。";
            networks.ItemsSource = new[] { new WirelessNetwork("家庭 Wi-Fi", "", 88, true, true), new WirelessNetwork("手机热点", "", 72, true) }; networks.SelectedIndex = 0;
            status.Text = "选择 Wi-Fi 并输入密码，或使用盒子已经连接的网络。";
        }
        if (stage == "ready") Ready("192.168.1.20");
    }
}
