using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop;

public static class ConnectionSetupSmoke
{
    public static void Run(string destination)
    {
        DesktopSettings settings = new() { ProfilePath = Path.Combine(destination, "nonexistent", "connection.json"), GatewayTokenFile = Path.Combine(destination, "nonexistent", "gateway.secrets.json") };
        ConnectionSetupWindow window = new(settings, _ => throw new InvalidOperationException("Preview must never connect."), preview: true);
        window.Show();
        string originalWorkspace = settings.Workspace;
        if (Descendants<TextBox>(window).Count() != 2 || Descendants<PasswordBox>(window).Count() != 1 || Descendants<TextBlock>(window).Any(t => t.Text is "工作目录" or "可信设备指纹"))
            throw new InvalidOperationException("Connection guide must contain address, port and password only.");
        foreach (string action in new[] { "设置以太网 IP", "安装连接组件", "设置网络共享" })
            if (!Descendants<Button>(window).Any(b => Equals(b.Content, action))) throw new InvalidOperationException("A direct network action is missing.");
        window.PreviewUpdate(new("local", "ready", "Windows 连接组件已安装。"));
        Button component = Descendants<Button>(window).Single(b => Equals(b.Content, "安装连接组件"));
        if (component.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Installed connection components must not prompt for installation.");
        window.PreviewUpdate(new("local", "missing", "请安装 OpenSSH 客户端连接组件。"));
        if (component.Visibility != Visibility.Visible) throw new InvalidOperationException("Missing connection components must provide an installation action.");
        window.PreviewUpdate(new("local", "ready", "Windows 连接组件已安装。"));
        window.PreviewUpdate(new("subnet", "missing", "当前以太网与板子未匹配。点击设置，将电脑设为 192.168.137.1 / 255.255.255.0。"));
        window.PreviewUpdate(new("network", "missing", "设备端口暂不可达。请检查板子电源、网线与设备地址；直连电脑时请开启网络共享。"));
        Capture("connection-guide-missing.png");
        window.PreviewUpdate(new("network", "ready", "设备 SSH 端口可达，还需核验设备身份。"));
        window.PreviewUpdate(new("identity", "ready", "设备身份已核验，登录成功。"));
        window.PreviewUpdate(new BoardNetwork("192.168.137.2", "eth0", "manual", ["192.168.137.2/24"], true, "192.168.137.1").Describe());
        window.PreviewUpdate(new("internet", "missing", "板子无法连到模型服务。点击设置网络共享，将上网网卡共享给插板子的以太网。"));
        window.PreviewUpdate(new("board", "missing", BoardReadiness.Guidance("model") + "\n" + BoardReadiness.Guidance("gateway")));
        Capture("connection-guide-board.png");
        window.PreviewUpdate(new("board", "ready", "板子配置条件已满足。"));
        window.PreviewUpdate(new("deploy", "checking", "正在配置聊天入口并重启板子网关…"));
        Capture("connection-guide-progress.png");
        window.PreviewUpdate(new("deploy", "ready", "专用配对和 Windows 插件已配置。"));
        window.PreviewUpdate(new("verify", "ready", "聊天入口已连接，返回主窗口即可发送任务。"));
        Capture("connection-guide-ready.png");
        NetworkAdapter[] adapters = [new("wired", "以太网 USB", "USB Ethernet adapter", true, true, false, [new("192.168.1.10", "255.255.255.0")])];
        EthernetIpWindow ip = new("192.168.137.2", adapters) { Owner = window }; ip.Show();
        Capture("ethernet-ip-settings.png", ip);
        if (!Descendants<TextBox>(ip).Any(t => t.Text == "192.168.137.1")) throw new InvalidOperationException("Suggested Ethernet address is missing.");
        foreach (Button button in Descendants<Button>(ip).Where(b => Equals(b.Content, "保存 IP 设置") || Equals(b.Content, "恢复自动获取 IP"))) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ip.Close();
        SettingsWindow import = new(settings) { Owner = window }; import.Show();
        Capture("connection-import.png", import);
        if (Descendants<TextBlock>(import).Any(t => t.Text.Contains("工作目录", StringComparison.Ordinal))) throw new InvalidOperationException("Connection import must not show workspace selection.");
        import.Close();
        window.Width = 780; window.Height = 600; Capture("connection-guide-compact.png");
        window.Close();
        if (settings.Workspace != originalWorkspace) throw new InvalidOperationException("Connection settings changed the selected workspace.");
        void Capture(string name, Window? target = null)
        {
            DispatcherFrame frame = new(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
            target ??= window;
            FrameworkElement content = (FrameworkElement)target.Content;
            content.UpdateLayout();
            foreach (Button button in Descendants<Button>(target).Where(b => b.IsVisible && b.Content is string title && title is "使用已有配对连接" or "检查板子配置" or "绑定并连接" or "关闭" or "保存 IP 设置" or "恢复自动获取 IP"))
            {
                Point position = button.TranslatePoint(new Point(), content);
                if (position.X < -1 || position.Y < -1 || position.X + button.ActualWidth > content.ActualWidth + 1 || position.Y + button.ActualHeight > content.ActualHeight + 1)
                    throw new InvalidOperationException("Connection guide action was clipped.");
            }
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(destination, name)); encoder.Save(file);
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T item) yield return item;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }
    }
}
