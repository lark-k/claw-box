using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;
using Renci.SshNet.Common;

namespace ClawBox.Desktop;

public sealed class ConnectionSetupWindow : Window
{
    private readonly DesktopSettings settings;
    private readonly Func<CancellationToken, Task<string?>> connect;
    private BoardSetup setup;
    private readonly BoardIdentity identity = new();
    private readonly Dictionary<string, SetupRequest> observed = [];
    private readonly Action? saveConfiguration;
    private readonly bool isPreview;
    private readonly ShellViewModel? session;
    private bool syncingSession;
    private bool HasSessionState => session is not null && (session.HasConnectionAttempted || session.Connected || session.IsConnecting);
    private bool ShowingSession => HasSessionState && (session!.Connected || session.IsConnecting || !networkChecksEnabled);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer autoTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly ConnectionCheckGate autoGate = new();
    private CancellationTokenSource? autoCancellation;
    private bool autoChecking;
    private bool connectionReady;
    private bool retryOnActivation;
    private bool networkRetryAllowed;
    private bool boardReady;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly TextBox host = new() { Text = "192.168.137.2" };
    private readonly TextBox port = new() { Text = "22" };
    private readonly PasswordBox password = new() { Padding = new Thickness(12, 8, 12, 8) };
    private readonly CheckBox consent = new() { Content = "我确认连接的是自己的板子，同意首次配对", Margin = new Thickness(0, 12, 0, 10) };
    private readonly ComboBox internet = new() { DisplayMemberPath = nameof(NetworkAdapter.Display), Margin = new Thickness(0, 5, 0, 5) };
    private readonly Button inspect = new() { Content = "检查板子配置" };
    private readonly Button provision = new() { Content = "绑定并连接" };
    private readonly Button reconnect = new() { Content = "使用已有配对连接" };
    private readonly Button cancel = new() { Content = "关闭" };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock introduction = new() { Text = "板子接电并用网线连电脑，电脑先连 Wi-Fi 或热点。填写地址、端口和密码并确认设备后，按顺序检查连接。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 20) };
    private readonly StackPanel form = new();
    private readonly Dictionary<string, (TextBlock Status, TextBlock Detail)> rows = [];
    private readonly Dictionary<string, Button> repairButtons = [];
    private bool busy, diagnosing, closed, closePending;
    private bool localReady, networkReady;
    private bool networkChecksEnabled;
    private bool loginVerified, diagnoseAgain;
    private string failedStep = "";
    public ConnectionSetupWindow(DesktopSettings settings, Func<CancellationToken, Task<string?>> connect, bool preview = false, Action? saveConfiguration = null, ShellViewModel? session = null)
    {
        this.settings = settings; this.connect = connect; this.saveConfiguration = saveConfiguration; isPreview = preview; this.session = session;
        setup = new(pairingId: settings.PairingId);
        Title = "Quantum Harness · 连接引导"; Width = 890; Height = 790; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14; Background = Brushes.White;
        if (!preview) { Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24); }
        LoadExisting();
        DockPanel root = new(); Content = new Border { Background = Brushes.White, Padding = new Thickness(28), Child = root };
        StackPanel heading = new(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = preview ? "连接你的板子 · 界面预览" : "连接你的板子", FontSize = 27, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(introduction);
        StackPanel footer = new(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(summary);
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        footer.Children.Add(actions);
        foreach (Button button in new[] { reconnect, inspect, provision, cancel }) { button.Margin = new Thickness(4, 3, 0, 0); actions.Children.Add(button); }
        if (TryFindResource("Primary") is Style primary) provision.Style = primary;
        Grid columns = new(); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        columns.Children.Add(form);
        AddField("设备地址", host, "查看设备交付卡或路由器设备列表。当前直连示例为 192.168.137.2；请以你的设备为准。");
        AddField("SSH 端口", port, "通常为 22。本向导支持 root 管理的标准板子镜像。");
        AddField("设备登录密码", password, "使用交付时提供的 root 密码，输入完成后自动检查，不会保存。已有配对可留空直接连接。");
        form.Children.Add(consent);
        form.Children.Add(new TextBlock { Text = "软件会记录首次确认的设备身份。换电脑前先关闭旧 APP；同一电脑换网络继续使用原配对。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        WrapPanel fixes = new(); form.Children.Add(fixes);
        AddAction(fixes, "设置以太网 IP", OpenEthernetSettings);
        AddAction(fixes, "设置网络共享", OpenSharingSettings);
        NetworkAdapter[] adapters = preview ? [] : NetworkSetup.ReadAdapters();
        internet.ItemsSource = adapters; internet.SelectedItem = adapters.OrderByDescending(a => a.Up && a.HasGateway && !a.Wired).ThenByDescending(a => a.Up && a.HasGateway).FirstOrDefault();
        Expander source = new() { Header = "选择共享网络的上网网卡", Content = internet, Margin = new Thickness(0, 0, 0, 8) };
        form.Children.Add(source);
        Expander help = new() { Header = "网线直连电脑，怎样让板子联网？", Margin = new Thickness(0, 0, 0, 16) };
        help.Content = new TextBlock { Text = "1. 板子接电，用网线连电脑；电脑先连 Wi-Fi 或热点。\n2. 若板子固定 192.168.137.2 / 24，点“设置以太网 IP”，电脑用 192.168.137.1，掩码 255.255.255.0，网关留空。\n3. 点“设置网络共享”，在上网网卡的“共享”页允许其他网络用户连接，目标选插板子的以太网网卡。\n回到本窗口会自动复查，填写密码后配对即可。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray };
        form.Children.Add(help);
        form.Children.Add(new TextBlock { Text = "绑定先检查已有配对、插件和聊天配置，仅补齐缺项；必要时重启网关。设备身份变化会停止连接；换板请关闭本窗口，从设置中的“更换板子／重新绑定”开始。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 });
        Expander advanced = new() { Header = "高级：导入已有配对", Margin = new Thickness(0, 16, 0, 0) };
        StackPanel advancedPanel = new(); advanced.Content = advancedPanel; form.Children.Add(advanced);
        AddAction(advancedPanel, "Windows 以太网设置", () => OpenSettings("ms-settings:network-ethernet"));
        AddAction(advancedPanel, "选择已有配对文件", () =>
        {
            SettingsWindow dialog = new(settings) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            setup = new(pairingId: settings.PairingId);
            saveConfiguration?.Invoke(); observed.Clear(); LoadExisting(); InputsChanged();
            summary.Text = "已有配对已导入，点击“使用已有配对连接”应用。";
        });
        StackPanel checklist = new(); Grid.SetColumn(checklist, 2); columns.Children.Add(checklist);
        checklist.Children.Add(new TextBlock { Text = "连接进度", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        StackPanel deviceCard = new();
        deviceCard.Children.Add(new TextBlock { Text = "板子固定 IP", FontWeight = FontWeights.SemiBold });
        TextBlock deviceState = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 5, 0, 4) };
        TextBlock deviceDetail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray };
        deviceCard.Children.Add(deviceState); deviceCard.Children.Add(deviceDetail); rows["boardIp"] = (deviceState, deviceDetail);
        checklist.Children.Add(new Border { Child = deviceCard, Background = new SolidColorBrush(Color.FromRgb(245, 248, 253)), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 15) });
        Apply(new("boardIp", "waiting", "输入密码并确认设备后，自动读取持久网络配置。"));
        foreach ((string key, string title) in new[] { ("local", "1  电脑连接组件"), ("subnet", "2  以太网地址"), ("network", "3  设备网络"), ("identity", "4  设备登录"), ("internet", "5  板子访问模型网络"), ("board", "6  板子运行环境"), ("deploy", "7  配对与插件配置"), ("verify", "8  聊天入口验收") })
        {
            StackPanel row = new() { Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            TextBlock state = new() { Margin = new Thickness(0, 5, 0, 3), Foreground = Brushes.Gray };
            TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
            row.Children.Add(state); row.Children.Add(detail); rows[key] = (state, detail);
            checklist.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromRgb(237, 237, 240)), Margin = new Thickness(0, 0, 0, 12) });
            Apply(new(key, "waiting", key is "board" or "boardIp" ? "密码填写完成并确认设备后，自动检查。" : "等待前面的步骤完成。"));
            if (key == "subnet") repairButtons[key] = AddAction(row, "设置以太网 IP", OpenEthernetSettings);
            if (key == "local") repairButtons[key] = AddAction(row, "安装连接组件", () => OpenSettings("ms-settings:optionalfeatures"));
            if (key == "internet") repairButtons[key] = AddAction(row, "设置网络共享", OpenSharingSettings);
            if (repairButtons.TryGetValue(key, out Button? repair)) repair.Visibility = Visibility.Collapsed;
        }
        foreach (TextBox input in new[] { host, port }) input.TextChanged += (_, _) => { consent.IsChecked = false; InputsChanged(); };
        password.PasswordChanged += (_, _) => InputsChanged();
        consent.Checked += (_, _) => BeginChecks();
        consent.Unchecked += (_, _) => InputsChanged();
        foreach (Control input in new Control[] { host, port, password }) input.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(BeginChecks);
        autoTimer.Tick += async (_, _) => { autoTimer.Stop(); await AutoCheck(); };
        inspect.Click += async (_, _) => await Run(false);
        provision.Click += async (_, _) => await Run(true);
        reconnect.Click += async (_, _) => await Reconnect();
        cancel.Click += (_, _) => { if (busy) { operation?.Cancel(); summary.Text = "正在停止。已经完成的配对步骤会保留，不会自动继续配置。"; } else if (autoChecking) { autoCancellation?.Cancel(); summary.Text = "正在停止自动检查。"; } else Close(); };
        timer.Tick += async (_, _) => { if (networkRetryAllowed && loginVerified && !autoChecking) RetryNetworkCheck(); await Diagnose(); };
        Loaded += async (_, _) => { if (!preview) { NetworkChange.NetworkAddressChanged += NetworkChanged; timer.Start(); await Diagnose(); } };
        Activated += (_, _) => { if (retryOnActivation) { retryOnActivation = false; RetryNetworkCheck(); _ = Diagnose(); } };
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; if (session is not null) session.PropertyChanged -= SessionChanged; NetworkChange.NetworkAddressChanged -= NetworkChanged; timer.Stop(); autoTimer.Stop(); autoCancellation?.Cancel(); lifetime.Cancel(); password.Clear(); lifetime.Dispose(); };
        InputsChanged();
        if (session is not null) { session.PropertyChanged += SessionChanged; SyncSession(); }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ShellViewModel.Connected) or nameof(ShellViewModel.IsConnecting) or nameof(ShellViewModel.Status))) return;
        if (Dispatcher.CheckAccess()) SyncSession(); else Dispatcher.BeginInvoke(SyncSession);
    }
    private void SyncSession()
    {
        if (closed || busy || syncingSession || !HasSessionState) return;
        syncingSession = true;
        try
        {
            autoGate.Invalidate(); autoCancellation?.Cancel(); autoTimer.Stop();
            networkChecksEnabled = false; networkRetryAllowed = false; loginVerified = false; boardReady = false;
            connectionReady = session!.Connected;
            bool active = session.Connected || session.IsConnecting;
            host.IsEnabled = port.IsEnabled = password.IsEnabled = consent.IsEnabled = !active;
            if (active && session.ConnectionDevice is SetupRequest device) { host.Text = device.Host; port.Text = device.Port.ToString(); }
            if (session.Connected)
            {
                password.Clear(); consent.IsChecked = false;
                introduction.Text = "板子已连接。当前地址、登录和聊天入口与首页同步，无需重复输入密码或绑定。";
                Apply(new("local", "ready", "当前连接组件已建立加密隧道。"));
                Apply(new("network", "ready", $"已连接板子 {host.Text}:{port.Text}，SSH 隧道正常。"));
                Apply(new("identity", "ready", "已使用已有配对密钥登录，并核验原设备身份。无需再次输入密码。"));
                Apply(session.RememberedCheck("boardIp") ?? new("boardIp", "unverified", "当前地址为 " + host.Text + "；已有配对未读取持久 IP 配置。"));
                Apply(session.RememberedCheck("internet") ?? new("internet", "unverified", "聊天网关已连接；模型服务网络未单独检测，实际回答由聊天验证。"));
                Apply(session.RememberedCheck("board") ?? new("board", "inUse", "板子网关已响应；未重新读取管理配置。"));
                Apply(new("deploy", "inUse", "已使用原配对，本机执行器和工具转发已建立。"));
                Apply(new("verify", "ready", "聊天入口已连接，与首页状态同步。"));
                summary.Text = $"已连接 {host.Text}:{port.Text}。可返回对话，或点击“断开连接”；已有配对会保留。";
            }
            else
            {
                introduction.Text = session.IsConnecting ? "正在连接板子，进度与首页同步；可点击“停止连接”取消。" : "当前已断开。已有配对可直接重新连接；更换地址或修复配置时，填写密码并确认设备后检查。";
                bool tools = BoardSetup.OpenSshTool("ssh.exe") is not null && BoardSetup.OpenSshTool("ssh-keygen.exe") is not null;
                Apply(new("local", tools ? "ready" : "missing", tools ? "Windows 连接组件已安装。" : "请安装 Windows OpenSSH 客户端。"));
                foreach (string step in new[] { "network", "identity", "internet", "board", "verify" })
                    Apply(new(step, session.IsConnecting ? "checking" : "disconnected", session.IsConnecting ? "正在建立连接，结果与首页同步。" : "当前连接已断开；点击“使用已有配对连接”可重新连接。"));
                Apply(new("boardIp", "unverified", "当前未连接；已有配对不包含持久 IP 配置，不能据此判断固定 IP。"));
                bool saved = File.Exists(settings.ProfilePath) && File.Exists(settings.GatewayTokenFile);
                Apply(new("deploy", saved ? "saved" : "unverified", saved ? "已有配对保留，无需重新绑定。" : "尚未保存可用配对。"));
                summary.Text = session.Status;
            }
            if (!isPreview)
            {
                try { Apply(NetworkSetup.Diagnose(host.Text.Trim(), NetworkSetup.ReadAdapters())); }
                catch (NetworkInformationException) { Apply(new("subnet", "unverified", "暂时无法读取网卡状态，请检查 Windows 以太网设置。")); }
            }
            else Apply(new("subnet", "unverified", "以太网配置由本机独立检查。"));
            cancel.Content = session.Connected ? "返回对话" : "关闭";
            UpdateActions();
        }
        finally { syncingSession = false; }
    }
    private void LoadExisting()
    {
        if (!File.Exists(settings.ProfilePath)) return;
        try
        {
            BoardConnectionProfile? profile = JsonSerializer.Deserialize<BoardConnectionProfile>(File.ReadAllText(settings.ProfilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (profile is not null) { host.Text = profile.BoxHost; port.Text = profile.SshPort.ToString(); }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void AddField(string title, Control input, string help)
    {
        form.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        form.Children.Add(input);
        form.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 14) });
    }
    private void InputsChanged()
    {
        if (busy || closed || syncingSession) return;
        if (HasSessionState) { SyncSession(); return; }
        autoGate.Invalidate(); autoCancellation?.Cancel(); autoTimer.Stop();
        networkRetryAllowed = false;
        loginVerified = false;
        networkChecksEnabled = false;
        connectionReady = false;
        if (!autoChecking) cancel.Content = "关闭";
        boardReady = false;
        networkReady = false;
        foreach (string step in new[] { "subnet", "network", "identity", "internet" })
            Apply(new(step, "waiting", "填写密码并确认设备后开始检查；已有配对可直接连接。"));
        Apply(new("board", "waiting", "填写完成后自动检查，缺少运行条件时显示处理办法。"));
        Apply(new("boardIp", "waiting", "登录后读取持久网络配置，确认板子是否固定 IP。"));
        Apply(new("deploy", "waiting", "勾选绑定说明并点击“绑定并连接”才会修改板子配置。"));
        Apply(new("verify", "waiting", "实际连接通过后才会显示验收成功，不会发送模型任务。"));
        failedStep = ""; summary.Text = "先填写密码并确认设备，再检查网络和板子；开始后每 3 秒复查网络。已有配对可留空密码直接连接。";
        UpdateActions();
    }
    private void BeginChecks()
    {
        if (closed || busy || session?.Connected == true || session?.IsConnecting == true || host.IsKeyboardFocusWithin || port.IsKeyboardFocusWithin || password.IsKeyboardFocusWithin ||
            password.Password.Length == 0 || consent.IsChecked != true) return;
        networkChecksEnabled = true;
        _ = Diagnose();
        ScheduleAutoCheck();
    }
    private async Task Diagnose()
    {
        if (isPreview || busy || closed) return;
        if (ShowingSession) { SyncSession(); return; }
        if (diagnosing) { diagnoseAgain = true; return; }
        diagnosing = true;
        string address = host.Text.Trim(); string portText = port.Text;
        int version = autoGate.Version;
        bool includeNetwork = networkChecksEnabled;
        try
        {
            SetupUpdate[] checks = await BoardSetup.Diagnose(address, int.TryParse(portText, out int number) ? number : 0, lifetime.Token, includeNetwork);
            if (closed || busy || version != autoGate.Version || host.Text.Trim() != address || port.Text != portText) return;
            foreach (SetupUpdate check in checks) Apply(check);
            if (includeNetwork) Apply(NetworkSetup.Diagnose(address, NetworkSetup.ReadAdapters()));
            localReady = checks.Any(c => c.Step == "local" && c.Status == "ready");
            networkReady = checks.Any(c => c.Step == "network" && c.Status == "ready");
            UpdateActions();
            if (networkReady && !host.IsKeyboardFocusWithin && !port.IsKeyboardFocusWithin && !password.IsKeyboardFocusWithin) ScheduleAutoCheck();
        }
        catch (OperationCanceledException) { }
        catch (NetworkInformationException) { if (includeNetwork && version == autoGate.Version) Apply(new("subnet", "missing", "无法读取网卡状态。点击“设置以太网 IP”或 Windows 以太网设置检查。")); }
        finally { diagnosing = false; if (diagnoseAgain && !closed) { diagnoseAgain = false; _ = Diagnose(); } }
    }
    private async Task<SetupRequest> Request(CancellationToken cancellation)
    {
        string target = host.Text.Trim(); int number = int.TryParse(port.Text, out int parsed) ? parsed : 0;
        SetupRequest.ValidateAddress(target, number);
        string key = target + ":" + number;
        SetupRequest? saved = identity.Saved(target, number, settings.ProfilePath);
        if (saved is not null) return saved;
        if (observed.TryGetValue(key, out SetupRequest? cached)) return cached;
        Apply(new("identity", "checking", "正在自动取得首次设备身份；这一步不发送登录密码…"));
        SetupRequest request = await identity.Resolve(target, number, settings.ProfilePath, consent.IsChecked == true, cancellation);
        observed[key] = request; return request;
    }
    private async Task Run(bool bind)
    {
        if (busy || session?.Connected == true || session?.IsConnecting == true) return;
        networkChecksEnabled = true;
        string login = password.Password;
        await Execute(async cancellation =>
        {
            if (consent.IsChecked != true) throw new SetupException("identity", "请先阅读并勾选配对确认。检查不会更新插件；只有点击绑定才会进行配置。");
            SetupRequest request = await Request(cancellation);
            IProgress<SetupUpdate> progress = new WindowProgress(this, device: request);
            if (!bind)
            {
                await setup.Inspect(request, login, progress, cancellation);
                boardReady = !networkRetryAllowed;
                summary.Text = boardReady ? "板子检查通过，可点击“绑定并连接”。" : "设备登录成功，模型网络尚未连通。请设置网络共享，返回后自动复查。";
                return;
            }
            if (consent.IsChecked != true) throw new SetupException("deploy", "请先阅读并勾选绑定说明。");
            SetupResult result = await setup.Provision(request, login, progress, cancellation);
            settings.ProfilePath = result.ProfilePath; settings.GatewayTokenFile = result.GatewayTokenFile;
            password.Clear();
            await Verify(cancellation);
        });
        login = "";
    }
    private void ScheduleAutoCheck()
    {
        if (isPreview || closed || busy || session?.Connected == true || session?.IsConnecting == true) return;
        autoTimer.Stop(); autoTimer.Start();
    }
    private async Task AutoCheck()
    {
        if (closed || busy || autoChecking || session?.Connected == true || session?.IsConnecting == true) return;
        string target = host.Text.Trim(); int number = int.TryParse(port.Text, out int parsed) ? parsed : 0;
        string login = password.Password;
        if (!autoGate.TryStart(target, number, login, networkReady, consent.IsChecked == true)) return;
        int version = autoGate.Version;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(45)); autoCancellation = cancellation;
        autoChecking = true; UpdateActions(); cancel.Content = "停止检查";
        try
        {
            SetupRequest request = await Request(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            IProgress<SetupUpdate> progress = new WindowProgress(this, version, request);
            await setup.Inspect(request, login, progress, cancellation.Token);
            if (version == autoGate.Version && !closed)
            {
                boardReady = !networkRetryAllowed;
                summary.Text = boardReady ? "自动检查通过。首次使用点击“绑定并连接”；已有配对可直接连接。" : "设备登录成功，模型网络尚未连通。请设置网络共享，返回后自动复查。";
            }
        }
        catch (OperationCanceledException)
        {
            if (version == autoGate.Version && !closed)
            {
                Apply(new("identity", "missing", "自动检查已停止或超时。可修正输入，或点击检查按钮重试。"));
                summary.Text = "检查已停止，未自动绑定或修改板子。";
            }
        }
        catch (SetupException error) { if (version == autoGate.Version && !closed) { networkRetryAllowed |= error.Step == "network"; Apply(new(error.Step, "missing", error.Message)); summary.Text = "按提示处理后会检查新输入；也可点击按钮重试。"; } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or Win32Exception or SshException or SocketException or ObjectDisposedException or ArgumentException)
        { if (version == autoGate.Version && !closed && !cancellation.IsCancellationRequested) Apply(new("identity", "missing", "自动检查未完成。请核对密码、网络或设备服务，修正后自动检查；相同输入不会反复登录。")); }
        finally
        {
            login = ""; autoChecking = false; autoCancellation = null;
            if (!closed) { cancel.Content = connectionReady ? "开始对话" : "关闭"; UpdateActions(); if (version != autoGate.Version) ScheduleAutoCheck(); }
        }
    }
    private async Task Reconnect()
    {
        if (session?.IsConnecting == true) { session.CancelConnection(); return; }
        if (session?.Connected == true) { session.Disconnect(); SyncSession(); return; }
        await Execute(async cancellation =>
        {
            Apply(new("identity", "checking", "正在核验新地址是否仍是原来的板子…"));
            await identity.UpdateAddress(settings.ProfilePath, host.Text.Trim(), int.TryParse(port.Text, out int number) ? number : 0, cancellation);
            Apply(new("identity", "ready", "已沿用原设备身份和密钥，不需要重新部署。"));
            await Verify(cancellation);
        });
    }
    private async Task Verify(CancellationToken cancellation)
    {
        connectionReady = false;
        failedStep = "verify";
        Apply(new("verify", "checking", "正在启动本机执行器、受限加密隧道并检查聊天入口；不会发送模型任务…"));
        string? error = await connect(cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (error is not null) throw new SetupException("verify", error + "\n按此提示处理后，再点击“使用已有配对连接”。");
        Apply(new("verify", "ready", "聊天入口已连接。可返回主窗口选择文件并发送任务。"));
        connectionReady = true;
        summary.Text = "连接成功。文件读取和修改仍需逐请求审批；远端模型由首次聊天验证。";
        reconnect.Content = "重新检查连接";
    }
    private async Task Execute(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        autoCancellation?.Cancel(); autoTimer.Stop(); autoGate.Invalidate();
        busy = true; failedStep = ""; timer.Stop(); form.IsEnabled = false;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(8)); cancel.Content = "停止"; UpdateActions();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { summary.Text = "配置或连接已停止。已完成步骤保留；不会自动重试或重发任何文件任务。"; Apply(new(failedStep.Length > 0 ? failedStep : "deploy", "missing", summary.Text)); }
        catch (SetupException error) { Apply(new(error.Step, "missing", error.Message)); summary.Text = "按右侧提示处理后，再点击检查或连接。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or Win32Exception or SshException or SocketException or ObjectDisposedException or ArgumentException)
        {
            if (operation.IsCancellationRequested) summary.Text = "操作已停止，已完成步骤保留。";
            else if (error is ArgumentException) summary.Text = "连接配置未完成：软件处理连接参数时发生错误。已有配对保留，请使用新版 APP 重试；仍失败请联系交付人员。";
            else summary.Text = "连接配置未完成。请检查网络、Windows 用户目录权限和板子服务；软件保留已有配置，修复后可显式重试。";
            Apply(new(failedStep.Length > 0 ? failedStep : "deploy", "missing", summary.Text));
        }
        finally
        {
            operation.Dispose(); operation = null; busy = false; form.IsEnabled = true; cancel.Content = connectionReady ? "开始对话" : "关闭";
            UpdateActions();
            if (closePending) Close(); else { timer.Start(); await Diagnose(); }
        }
    }
    private void Apply(SetupUpdate update)
    {
        if (closed || !rows.TryGetValue(update.Step, out var row)) return;
        row.Status.Text = update.Status switch { "ready" => "✓ 已通过", "checking" => "正在检查…", "missing" => "需要处理", "unverified" => "未核验", "inUse" => "使用中", "disconnected" => "已断开", "saved" => "已保存", _ => "等待" };
        row.Status.Foreground = update.Status switch { "ready" => Brushes.ForestGreen, "missing" => Brushes.DarkOrange, "checking" => Brushes.RoyalBlue, _ => Brushes.Gray };
        row.Detail.Text = update.Detail;
        if (update.Step == "internet") networkRetryAllowed = update.Status == "missing";
        if (update.Step == "identity") loginVerified = update.Status == "ready";
        if (repairButtons.TryGetValue(update.Step, out Button? repair)) repair.Visibility = update.Status == "missing" ? Visibility.Visible : Visibility.Collapsed;
        if (update.Status == "checking") failedStep = update.Step;
        if (busy && update.Step is "board" or "boardIp" or "deploy" or "verify")
            Dispatcher.BeginInvoke(() => { if (!closed) row.Detail.BringIntoView(); });
    }
    private void UpdateActions()
    {
        bool active = session?.Connected == true || session?.IsConnecting == true;
        inspect.IsEnabled = !active && !busy && !autoChecking && networkReady && password.Password.Length > 0 && consent.IsChecked == true;
        provision.IsEnabled = inspect.IsEnabled && localReady && boardReady && !networkRetryAllowed && consent.IsChecked == true;
        reconnect.Content = session?.Connected == true ? "断开连接" : session?.IsConnecting == true ? "停止连接" : "使用已有配对连接";
        reconnect.IsEnabled = !busy && !autoChecking && (active || File.Exists(settings.ProfilePath) && File.Exists(settings.GatewayTokenFile));
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!busy) return;
        args.Cancel = true; closePending = true; operation?.Cancel(); summary.Text = "正在停止并关闭连接引导…";
    }
    private void OpenNetworkSettings()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe")) { UseShellExecute = true, ArgumentList = { "ncpa.cpl" } }); }
        catch (Win32Exception) { summary.Text = "请从 Windows 设置打开网络连接，检查以太网与网络共享。"; }
    }
    internal void PreviewUpdate(SetupUpdate update) => Apply(update);
    private void OpenEthernetSettings()
    {
        EthernetIpWindow dialog = new(host.Text.Trim()) { Owner = this }; dialog.ShowDialog();
        RetryNetworkCheck(); _ = Diagnose();
    }
    private void OpenSharingSettings()
    {
        retryOnActivation = true;
        if (internet.SelectedItem is NetworkAdapter selected && NetworkSetup.OpenProperties(selected.Name))
            summary.Text = $"已请求打开“{selected.Name}”属性。选择“共享”，允许其他网络用户连接，目标选插板子的以太网网卡。";
        else { OpenNetworkSettings(); summary.Text = "请选择正在上网的网卡 → 属性 → 共享，目标选连接板子的以太网网卡。"; }
    }
    private void RetryNetworkCheck()
    {
        if (ShowingSession) { SyncSession(); return; }
        if (closed || busy || !networkChecksEnabled) return;
        autoGate.RetryAfterNetworkSettings(); autoCancellation?.Cancel();
        Apply(new("subnet", "checking", "正在刷新 IPv4 配置…"));
        if (networkRetryAllowed && loginVerified) Apply(new("internet", "checking", "正在重新检查板子的模型网络…"));
        _ = Diagnose(); ScheduleAutoCheck();
    }
    private void NetworkChanged(object? sender, EventArgs args)
    {
        if (!closed) Dispatcher.BeginInvoke(() => { if (!closed) RetryNetworkCheck(); });
    }
    private void OpenSettings(string uri)
    {
        try { NetworkSetup.OpenSettings(uri); }
        catch (Win32Exception) { summary.Text = "Windows 设置未打开，请从系统设置中的“网络和 Internet”或“可选功能”进入。"; }
    }
    private Button AddAction(Panel panel, string text, Action action)
    {
        Button button = new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 5, 6) };
        button.Click += (_, _) => { if (!isPreview) action(); }; panel.Children.Add(button);
        return button;
    }
    private sealed class WindowProgress(ConnectionSetupWindow window, int? version = null, SetupRequest? device = null) : IProgress<SetupUpdate>
    {
        public void Report(SetupUpdate update)
        {
            void Apply()
            {
                if (window.closed || version is not null && version != window.autoGate.Version) return;
                if (device is not null) window.session?.RememberCheck(device, update);
                window.Apply(update);
            }
            if (window.Dispatcher.CheckAccess()) Apply();
            else window.Dispatcher.Invoke(Apply);
        }
    }
}
