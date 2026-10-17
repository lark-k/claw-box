using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClawBox.Desktop;

public sealed class BoardReplacementWindow : Window
{
    public BoardReplacementWindow()
    {
        Title = "Quantum Harness · 更换板子／重新绑定";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14; Background = Brushes.White;
        StackPanel panel = new();
        Content = new Border { Background = Brushes.White, Padding = new Thickness(28), Child = panel };
        panel.Children.Add(new TextBlock { Text = "更换板子／重新绑定", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock
        {
            Text = "用于换一块板子，或为当前板子重新建立配对。\n如果只是网络变化或暂时断线，请取消并使用设置中的“有线连接”或“无线连接”。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Margin = new Thickness(0, 0, 0, 16)
        });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 253)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new TextBlock
            {
                Text = "确认后将断开当前连接，停止使用旧设备配对。\n聊天记录、工作区和电脑上的文件都会保留。\n\n接下来选择有线或无线连接方式，确认自己的盒子后绑定。无需手工删除文件或生成密钥。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24
            }
        });
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        Button cancel = new() { Content = "取消", IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(18, 10, 18, 10) };
        Button confirm = new() { Content = "确认更换并继续", Padding = new Thickness(18, 10, 18, 10) };
        if (TryFindResource("Primary") is Style primary) confirm.Style = primary;
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions);
    }
}
