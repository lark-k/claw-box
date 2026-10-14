using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop;

public sealed class EthernetIpWindow : Window
{
    private readonly ComboBox adapter = new() { DisplayMemberPath = nameof(NetworkAdapter.Display), Margin = new Thickness(0, 6, 0, 15) };
    private readonly TextBox address = new();
    private readonly TextBox mask = new() { Text = "255.255.255.0" };
    private readonly TextBlock current = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 8) };
    private readonly string boardHost;
    private readonly bool preview;
    private bool busy;
    public EthernetIpWindow(string boardHost, NetworkAdapter[]? previewAdapters = null)
    {
        this.boardHost = boardHost; preview = previewAdapters is not null;
        Title = "Quantum Harness · 以太网 IPv4 设置"; Width = 660; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14; Background = Brushes.White;
        StackPanel panel = new() { Margin = new Thickness(26) };
        Content = new Border { Background = Brushes.White, Child = panel };
        panel.Children.Add(new TextBlock { Text = "设置连接板子的以太网 IP", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "选择插板子的网口（包括 USB 转网口），根据下方名称和描述确认。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 15) });
        panel.Children.Add(adapter); panel.Children.Add(current);
        Add("电脑以太网 IPv4 地址", address); Add("子网掩码", mask);
        panel.Children.Add(new TextBlock { Text = $"板子地址：{boardHost}\n建议电脑地址：{NetworkSetup.SuggestedAddress(boardHost)}，掩码按板子的实际配置填写（常见为 255.255.255.0）。电脑地址不能与板子相同。\n静态设置会替换所选网卡的 IPv4 地址并关闭其 DHCP，不设置默认网关；电脑继续通过 Wi-Fi 上网。网络共享需要单独开启。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(status);
        WrapPanel actions = new(); panel.Children.Add(actions);
        Button save = Button("保存 IP 设置", async () => await Apply(false));
        if (TryFindResource("Primary") is Style primary) save.Style = primary;
        actions.Children.Add(save); actions.Children.Add(Button("恢复自动获取 IP", async () => await Apply(true)));
        actions.Children.Add(Button("系统网卡属性", () => { if (adapter.SelectedItem is NetworkAdapter selected && !preview && !NetworkSetup.OpenProperties(selected.Name)) status.Text = "无法打开此网卡的属性，请从 Windows 以太网设置进入。"; }));
        actions.Children.Add(Button("关闭", Close));
        adapter.SelectionChanged += (_, _) => ShowCurrent();
        NetworkAdapter[] adapters = previewAdapters ?? NetworkSetup.ReadAdapters();
        adapter.ItemsSource = adapters.Where(a => a.Wired).ToArray();
        adapter.SelectedItem = adapters.Where(a => a.Wired).OrderByDescending(a => a.Up && a.Addresses.Any(ip => NetworkSetup.SameSubnet(ip.Address, boardHost, ip.Mask))).ThenByDescending(a => a.Up && !a.HasGateway).FirstOrDefault();
        save.IsEnabled = adapter.SelectedItem is not null;
        if (adapter.SelectedItem is null) status.Text = "没有检测到有线网卡，请插入网线或 USB 网卡后重新打开此窗口。";
        Closing += (_, args) => { if (busy) args.Cancel = true; };
        void Add(string label, TextBox input) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 5) }); panel.Children.Add(input); }
        static Button Button(string title, Action click) { Button button = new() { Content = title, Margin = new Thickness(0, 0, 6, 6) }; button.Click += (_, _) => click(); return button; }
    }
    private void ShowCurrent()
    {
        if (adapter.SelectedItem is not NetworkAdapter selected) return;
        current.Text = selected.Description + "\n当前 IPv4：" + (selected.Addresses.Length == 0 ? "暂无地址" : string.Join("；", selected.Addresses.Select(a => a.Address + " / " + a.Mask)));
        address.Text = NetworkSetup.SuggestedAddress(boardHost); mask.Text = "255.255.255.0";
    }
    private async Task Apply(bool dhcp)
    {
        if (busy || preview || adapter.SelectedItem is not NetworkAdapter selected) return;
        try
        {
            if (!dhcp && address.Text.Trim() == boardHost) throw new InvalidOperationException("电脑 IP 不能与板子相同，请使用建议地址或其他未占用的同网段地址。");
            ProcessStartInfo command = NetworkSetup.AddressCommand(selected, address.Text.Trim(), mask.Text.Trim(), dhcp, NetworkSetup.ReadAdapters());
            busy = true; ((FrameworkElement)Content).IsEnabled = false;
            status.Text = "请在 Windows 权限提示中确认；正在修改所选网卡…";
            using Process process = Process.Start(command)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) { status.Text = "Windows 未完成设置，请检查管理员权限、网卡状态或打开系统网卡属性处理。"; return; }
            NetworkAdapter? updated = NetworkSetup.ReadAdapters().FirstOrDefault(a => a.Id == selected.Id);
            bool verified = dhcp ? updated is not null && NetworkSetup.IsDhcp(selected.Id) : updated?.Addresses.Any(a => a.Address == address.Text.Trim() && a.Mask == mask.Text.Trim()) == true;
            status.Text = verified ? "设置已保存并复查。返回连接引导后会自动检查网络。" : "设置命令已结束，系统状态尚未确认。请稍后重新检查或打开系统网卡属性。";
            if (updated is not null) current.Text = updated.Description + "\n当前 IPv4：" + string.Join("；", updated.Addresses.Select(a => a.Address + " / " + a.Mask));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { status.Text = "已取消 Windows 权限申请，软件没有继续修改网卡。"; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or System.Net.NetworkInformation.NetworkInformationException) { status.Text = error.Message; }
        finally { busy = false; ((FrameworkElement)Content).IsEnabled = true; }
    }
}
