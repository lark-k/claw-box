using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClawBox.Desktop;

public sealed class ConversationDialog : Window
{
    public string ConversationName { get; private set; }
    public ConversationDialog(string name, bool rename)
    {
        ConversationName = name;
        Title = rename ? "重命名会话" : "删除会话";
        Width = 430; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        Border surface = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(230, 228, 232)), BorderThickness = new(1), CornerRadius = new(14), Padding = new(22), Margin = new(12) };
        surface.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 20, Opacity = 0.14, ShadowDepth = 4 };
        StackPanel panel = new(); surface.Child = panel; Content = surface;
        TextBlock title = new() { Text = Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 14) };
        title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        panel.Children.Add(title);
        TextBox input = new() { Text = name, MaxLength = 120, Margin = new(0, 0, 0, 8) };
        if (rename) panel.Children.Add(input);
        else panel.Children.Add(new TextBlock { Text = $"删除“{name}”及其本地对话记录？\n工作区、附件和用户文件会保留。", TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new(0, 0, 0, 8) });
        TextBlock validation = new() { Foreground = Brushes.Firebrick, FontSize = 11, Visibility = Visibility.Collapsed };
        panel.Children.Add(validation);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) });
        Button submit = new() { Content = rename ? "保存" : "删除", IsDefault = true, Background = new SolidColorBrush(rename ? Color.FromRgb(39, 39, 42) : Color.FromRgb(210, 48, 48)), Foreground = Brushes.White, MinWidth = 70 };
        submit.Click += (_, _) =>
        {
            string value = input.Text.Trim();
            if (rename && (value.Length == 0 || value.Any(char.IsControl))) { validation.Text = "请填写有效的会话名称。"; validation.Visibility = Visibility.Visible; input.Focus(); return; }
            ConversationName = value; DialogResult = true;
        };
        actions.Children.Add(submit); panel.Children.Add(actions);
        Loaded += (_, _) => { if (rename) { input.Focus(); input.SelectAll(); } };
    }
}
