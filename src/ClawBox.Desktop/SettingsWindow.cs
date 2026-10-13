using System.Windows;
using System.Windows.Controls;
using ClawBox.Desktop.Models;
using Microsoft.Win32;

namespace ClawBox.Desktop;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(DesktopSettings settings)
    {
        Title = "Quantum Harness · 连接与设置";
        Width = 650; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14; Background = System.Windows.Media.Brushes.White;
        StackPanel panel = new() { Margin = new Thickness(28) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "连接与工作目录", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        TextBox workspace = Field("工作目录", settings.Workspace, true);
        TextBox profile = Field("盒子配对文件", settings.ProfilePath, false);
        TextBox secret = Field("网关凭据文件（不会显示密钥）", settings.GatewayTokenFile, false);
        panel.Children.Add(new TextBlock { Text = "新设备请先按运行指南完成配对。凭据与聊天记录保存在当前 Windows 用户目录。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18), Foreground = System.Windows.Media.Brushes.Gray });
        Button save = new() { Content = "保存设置", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(24, 10, 24, 10) };
        save.Click += (_, _) =>
        {
            if (!Path.IsPathFullyQualified(workspace.Text) || Path.TrimEndingDirectorySeparator(workspace.Text) == Path.GetPathRoot(workspace.Text) || !File.Exists(profile.Text) || !File.Exists(secret.Text))
            { MessageBox.Show(this, "请选择有效的工作目录、配对文件与网关凭据文件。"); return; }
            settings.Workspace = workspace.Text; settings.ProfilePath = profile.Text; settings.GatewayTokenFile = secret.Text;
            DialogResult = true;
        };
        panel.Children.Add(save);
        TextBox Field(string label, string value, bool folder)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            DockPanel row = new() { Margin = new Thickness(0, 0, 0, 14) };
            TextBox input = new() { Text = value, Padding = new Thickness(8), IsReadOnly = true };
            Button choose = new() { Content = "选择", Margin = new Thickness(8, 0, 0, 0) };
            choose.Click += (_, _) =>
            {
                if (folder) { OpenFolderDialog dialog = new(); if (dialog.ShowDialog(this) == true) input.Text = dialog.FolderName; }
                else { OpenFileDialog dialog = new() { Filter = "JSON 配置|*.json" }; if (dialog.ShowDialog(this) == true) input.Text = dialog.FileName; }
            };
            DockPanel.SetDock(choose, Dock.Right); row.Children.Add(choose); row.Children.Add(input); panel.Children.Add(row); return input;
        }
    }
}
