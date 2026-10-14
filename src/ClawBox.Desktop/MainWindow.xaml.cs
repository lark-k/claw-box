using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClawBox.Desktop.ViewModels;
using Microsoft.Win32;

namespace ClawBox.Desktop;

public partial class MainWindow : Window
{
    public ShellViewModel Model { get; }
    private readonly bool preview;
    private bool paneOpen;
    private bool syncingSelection;
    public MainWindow(ShellViewModel model, bool preview = false)
    {
        this.preview = preview;
        Model = model;
        InitializeComponent();
        if (!preview)
        {
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        }
        DataContext = model;
        model.Messages.CollectionChanged += (_, _) => { Welcome.Visibility = model.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ChatScroll.ScrollToEnd(); };
        model.Approvals.CollectionChanged += (_, _) => { if (model.Approvals.Count > 0) Dispatcher.BeginInvoke(() => ComposerSurface.BringIntoView()); };
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.Busy)) StopButton.Visibility = model.Busy ? Visibility.Visible : Visibility.Collapsed;
            if (args.PropertyName == nameof(model.Selected)) SyncSelection();
        };
        Welcome.Visibility = model.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) { if (!preview) await Model.Connect(); }
    private void Window_Closing(object? sender, CancelEventArgs e) => Model.Dispose();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColumn is not null) SidebarColumn.Width = new GridLength(ActualWidth >= 1700 ? 290 : 250);
        if (paneOpen && TaskColumn is not null) TaskColumn.Width = new GridLength(ActualWidth >= 1400 ? 380 : 310);
    }
    private void ShowPane(bool visible) { paneOpen = visible; TaskColumn.Width = new GridLength(visible ? (ActualWidth >= 1400 ? 380 : 310) : 0); }
    private void ToggleTasks_Click(object sender, RoutedEventArgs e) => ShowPane(!paneOpen);
    private void Home_Click(object sender, RoutedEventArgs e) => ShowPane(false);
    private void New_Click(object sender, RoutedEventArgs e) => Model.NewConversation();
    private void RecentNew_Click(object sender, RoutedEventArgs e) => Model.NewRecent();
    private void ProjectNew_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string path) return;
        Models.Conversation? conversation = Model.Conversations.Where(c => c.WorkspacePath.Equals(path, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (conversation is null) Model.AddProject(path); else Model.Selected = conversation;
    }
    private void Access_Click(object sender, RoutedEventArgs e)
    {
        Button button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
    }
    private void AccessMode_Click(object sender, RoutedEventArgs e) { if (((MenuItem)sender).Tag is string mode) Model.SetAccess(mode); }
    private void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Parent is ContextMenu menu && menu.PlacementTarget is ListBoxItem row && row.DataContext is Models.Conversation conversation)
        {
            if (MessageBox.Show(this, "删除该会话和本地对话记录？工作区、附件和用户文件会保留。", "删除会话", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { Model.DeleteConversation(conversation); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageBox.Show(this, error.Message, "会话未删除"); }
        }
    }
    private void Conversation_RightClick(object sender, MouseButtonEventArgs e)
    {
        ListBoxItem row = (ListBoxItem)sender;
        MenuItem delete = new() { Header = "删除会话" };
        delete.Click += DeleteConversation_Click;
        ContextMenu menu = new() { PlacementTarget = row };
        menu.Items.Add(delete); row.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Parent is ContextMenu menu && menu.PlacementTarget is Button button && button.Tag is string path) Model.RemoveProject(path);
    }
    private void Conversation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection) return;
        if (((ListBox)sender).SelectedItem is Models.Conversation conversation) Model.Selected = conversation;
        SyncSelection();
    }
    private void ConversationList_Loaded(object sender, RoutedEventArgs e) => SyncSelection();
    private void SyncSelection()
    {
        if (syncingSelection) return;
        syncingSelection = true;
        try { foreach (ListBox list in Descendants<ListBox>(this)) list.SelectedItem = list.Items.Contains(Model.Selected) ? Model.Selected : null; }
        finally { syncingSelection = false; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await Model.Connect();
    private async void Send_Click(object sender, RoutedEventArgs e) => await Model.Send();
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Model.Stop();
    private async void Approve_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is ApprovalCard card) await Model.Decide(card, true); }
    private async void Reject_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is ApprovalCard card) await Model.Decide(card, false); }
    private async void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await Model.Send(); }
    }
    private void Suggestion_Click(object sender, RoutedEventArgs e) { Model.Input = ((Button)sender).Tag?.ToString() ?? ""; Composer.Focus(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(Model.Settings.Workspace); Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = '"' + Model.Settings.Workspace + '"' }); }
        catch (Exception error) when (error is IOException or Win32Exception) { Model.Status = "工作目录无法打开。"; }
    }
    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        string? path = ((Button)sender).Tag as string;
        if (path is null) return;
        string full = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Model.Settings.Workspace)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
        for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { Model.Status = "结果路径含有链接，无法打开。"; return; }
        string extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension is ".cs" or ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" or ".vue" or ".java" or ".json" or ".xml" or ".css" or ".html" or ".gradle" or ".kts" or ".py")
        {
            try
            {
                if (new FileInfo(full).Length > 4 * 1024 * 1024) { Model.Status = "文件超过预览大小限制。"; return; }
                Window code = new()
                {
                    Owner = this,
                    Title = Path.GetFileName(full),
                    Width = 900,
                    Height = 650,
                    Content = new TextBox
                    {
                        Text = File.ReadAllText(full),
                        IsReadOnly = true,
                        FontFamily = new("Consolas"),
                        FontSize = 14,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Margin = new(16)
                    }
                };
                code.ShowDialog();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Model.Status = "文件预览无法打开。"; }
            return;
        }
        if (extension is not (".txt" or ".docx" or ".md")) return;
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Win32Exception) { Model.Status = "没有找到可打开该文件的程序。"; }
    }
    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy) return;
        OpenFileDialog dialog = new() { Filter = "办公及代码文件|*.txt;*.md;*.docx;*.cs;*.py;*.json;*.js;*.ts;*.tsx;*.jsx;*.vue;*.java;*.xml;*.html;*.css;*.gradle;*.kts", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        try { foreach (string path in dialog.FileNames) Model.Attach(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Model.Status = "文件无法添加，请检查权限和路径。"; }
    }
    private async void Project_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy) return;
        OpenFolderDialog dialog = new() { Title = "选择代码项目目录" };
        if (dialog.ShowDialog(this) != true) return;
        if (Path.TrimEndingDirectorySeparator(dialog.FolderName) == Path.GetPathRoot(dialog.FolderName)) { Model.Status = "请选择具体项目，不要选择整个磁盘。"; return; }
        Model.AddProject(dialog.FolderName);
        if (!Model.Connected && !Model.Busy) await Model.Connect();
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. 保持盒子电源、网线与网络共享开启。\n2. 点击连接盒子。\n3. 选择文件并输入任务，Ctrl+Enter 发送。\n4. 查看审批卡片的完整参数，再批准或拒绝。\n5. 核对执行记录与结果文件。\n\n选择工作区外的文件时会复制到工作区，原文件保持不变。",
        "Quantum Harness 使用帮助", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanManageChats) { Model.Status = "请先结束当前任务，再修改连接设置。"; return; }
        SettingsWindow dialog = new(Model.Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            string workspace = Model.Settings.Workspace;
            Model.SaveSettings();
            if (!workspace.Equals(Model.Selected.WorkspacePath, StringComparison.OrdinalIgnoreCase)) Model.AddProject(workspace);
            Model.Status = "设置已保存，点击连接盒子应用。";
        }
    }
    public void PreviewPane() => ShowPane(true);
}
