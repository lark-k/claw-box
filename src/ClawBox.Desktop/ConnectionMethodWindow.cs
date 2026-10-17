using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawBox.Desktop;

public sealed class ConnectionMethodWindow : Window
{
    public bool Wireless { get; private set; }
    public ConnectionMethodWindow()
    {
        Title = "Quantum Harness · 选择连接方式"; Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14; Background = Brushes.White;
        StackPanel panel = new(); Content = new Border { Background = Background, Padding = new Thickness(28), Child = panel };
        panel.Children.Add(new TextBlock { Text = "连接你的盒子", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "选择日常连接方式。切换网络会保留原配对。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 0, 0, 20) });
        Add("有线连接", "用网线连接电脑与盒子，检查以太网和网络共享。", false);
        Add("无线连接", "无需网线，通过蓝牙设置 Wi-Fi，或连接已联网的盒子。", true);
        Button close = new() { Content = "取消", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(close);
        void Add(string title, string help, bool wireless)
        {
            StackPanel text = new(); text.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 6, 0, 0) });
            Button button = new() { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(18), Margin = new(0, 0, 0, 14) };
            button.Click += (_, _) => { Wireless = wireless; DialogResult = true; }; panel.Children.Add(button);
        }
    }
}
