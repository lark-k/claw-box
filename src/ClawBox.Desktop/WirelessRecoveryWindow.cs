using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawBox.Desktop;

public sealed class WirelessRecoveryWindow : Window
{
    public WirelessRecoveryWindow(string detail)
    {
        Title = "Quantum Harness · 连接未完成"; Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; FontFamily = new("Microsoft YaHei UI"); FontSize = 14;
        StackPanel panel = new(); Content = new Border { Background = Background, Padding = new Thickness(28), Child = panel };
        panel.Children.Add(new TextBlock { Text = "需要设置新的 Wi-Fi？", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "未能连接到原盒子。如果更换了网络，可以通过蓝牙为盒子选择新的 Wi-Fi，无需重新绑定。", TextWrapping = TextWrapping.Wrap, LineHeight = 25 });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 253)),
            CornerRadius = new(8),
            Padding = new(14),
            Margin = new(0, 18, 0, 18),
            Child = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, LineHeight = 24, Foreground = Brushes.DimGray }
        });
        panel.Children.Add(new TextBlock { Text = "已有配对、保存的 Wi-Fi、聊天记录和工作区都会保留。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 0, 0, 20) });
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(actions);
        Button cancel = new() { Content = "暂不连接", IsCancel = true, Padding = new(16, 9, 16, 9), Margin = new(0, 0, 12, 0) };
        Button setup = new() { Content = "设置 Wi-Fi", IsDefault = true, Padding = new(16, 9, 16, 9) };
        if (TryFindResource("Primary") is Style style) setup.Style = style;
        setup.Click += (_, _) => DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(setup);
    }
}
