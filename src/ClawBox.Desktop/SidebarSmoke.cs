using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Contracts;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

// Uses the actual window, menu handlers and dialogs with disposable mock data only.
public static class SidebarSmoke
{
    public static void Run(MainWindow window, ShellViewModel model, string root, string destination, Action<string> capture)
    {
        window.Width = 1480; window.Height = 960;
        string project = Path.Combine(root, "sidebar-project"); Directory.CreateDirectory(project);
        string userFile = Path.Combine(project, "keep.txt"); File.WriteAllText(userFile, "用户文件保留");
        model.AddProject(project); Conversation first = model.Selected;
        model.RenameConversation(first, "侧栏交互验证"); model.NewRecent(); Conversation recent = model.Selected;
        model.RenameConversation(recent, "最近会话验证"); model.Selected = first;
        capture("sidebar-idle.png");
        Button add = (Button)window.FindName("AddProjectButton"), recentNew = (Button)window.FindName("RecentNewButton");
        if (add.Visibility != Visibility.Hidden || recentNew.Visibility != Visibility.Hidden) throw new InvalidOperationException("Section actions must be hidden without hover.");
        Expander group = Group();
        ToggleButton toggle = (ToggleButton)group.Template.FindName("WorkspaceToggle", group);
        if (Descendants<TextBlock>(toggle).Any(t => t.Text is "\uE76C" or "\uE70D")) throw new InvalidOperationException("Workspace header must not show a chevron.");
        toggle.IsChecked = false; capture("workspace-collapsed.png");
        model.RenameConversation(first, "折叠仍保留"); capture("workspace-collapsed-refresh.png");
        if (Group().IsExpanded) throw new InvalidOperationException("Sidebar refresh lost workspace collapse state.");
        group = Group(); toggle = (ToggleButton)group.Template.FindName("WorkspaceToggle", group); toggle.IsChecked = true;
        Button pencil = (Button)group.Template.FindName("WorkspaceNew", group);
        if (pencil.Visibility != Visibility.Hidden) throw new InvalidOperationException("Workspace pencil must be hidden without hover.");
        // Capture the hover presentation without moving the user's physical mouse.
        pencil.Visibility = Visibility.Visible; add.Visibility = Visibility.Visible; recentNew.Visibility = Visibility.Visible;
        capture("sidebar-hover.png");
        pencil.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Conversation created = model.Selected;
        if (created.Id == first.Id || !created.IsProject || created.WorkspacePath != project) throw new InvalidOperationException("Workspace pencil did not create a new chat in its workspace.");
        add.ClearValue(UIElement.VisibilityProperty); recentNew.ClearValue(UIElement.VisibilityProperty);
        model.RenameConversation(created, "新会话"); model.Selected = recent; capture("sidebar-selection.png");
        for (int i = 0; i < 3; i++)
        {
            ListBoxItem row = Row(created);
            MouseButtonEventArgs right = new(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            row.RaiseEvent(right);
            if (!right.Handled || window.SidebarMenu is not { IsOpen: true } menu || menu.Items.OfType<MenuItem>().Any(item => !ReferenceEquals(item.Tag, created)))
                throw new InvalidOperationException("Repeated right click lost the clicked conversation target.");
            if (i == 0) CapturePopup(menu, "conversation-menu.png");
            menu.IsOpen = false;
        }
        // Select another conversation before invoking the clicked row's rename/delete actions.
        window.OpenConversationMenu(Row(created), created, keyboard: true);
        SubmitDialog("已重命名的项目会话");
        window.SidebarMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        capture("sidebar-renamed.png");
        if (created.Title != "已重命名的项目会话" || model.Selected != recent) throw new InvalidOperationException("Rename modified the selection rather than the clicked chat.");
        if (new ConversationStore(new LocalState(root)).Load().Single(c => c.Id == created.Id).Title != created.Title) throw new InvalidOperationException("Rename was not persisted.");
        window.OpenConversationMenu(Row(created), created, keyboard: true); SubmitDialog(null);
        window.SidebarMenu!.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        capture("sidebar-deleted.png");
        if (model.Conversations.Contains(created) || !model.Conversations.Contains(recent) || File.ReadAllText(userFile) != "用户文件保留") throw new InvalidOperationException("Delete did not preserve the other chat and user file.");
        window.OpenConversationMenu(Row(recent), recent, keyboard: true); SubmitDialog("最近也能重命名");
        window.SidebarMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (recent.Title != "最近也能重命名") throw new InvalidOperationException("Recent chat rename failed.");
        model.Selected = first;
        Button access = (Button)window.FindName("AccessButton"); access.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ContextMenu accessMenu = access.ContextMenu; CapturePopup(accessMenu, "access-menu.png");
        MenuItem full = accessMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "fullAccess"));
        full.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); accessMenu.IsOpen = false;
        access.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); CapturePopup(access.ContextMenu, "access-menu-selected.png");
        if (!access.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "fullAccess")).IsChecked) throw new InvalidOperationException("Access selection checkmark failed.");
        access.ContextMenu.IsOpen = false; capture("access-chip.png");
        Button send = (Button)window.FindName("SendButton"), stop = (Button)window.FindName("StopButton");
        Point sendPoint = send.TranslatePoint(new Point(), (UIElement)window.Content);
        window.OpenProjectMenu(Group(), project, keyboard: true);
        ContextMenu runningMenu = window.SidebarMenu!;
        using CancellationTokenSource cancellation = new();
        FieldInfo turn = typeof(ShellViewModel).GetField("turn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        turn.SetValue(model, cancellation); typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(model, true);
        capture("send-stop.png");
        if (!stop.IsVisible || send.IsVisible || (stop.TranslatePoint(new Point(), (UIElement)window.Content) - sendPoint).Length > 1) throw new InvalidOperationException("Stop did not replace send in the same position.");
        if (Descendants<Button>(window).Any(b => b.IsVisible && Equals(b.Content, "停止"))) throw new InvalidOperationException("A standalone stop button remains.");
        if (runningMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled)) throw new InvalidOperationException("An already open workspace menu must disable removal when a turn starts.");
        runningMenu.IsOpen = false; stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!cancellation.IsCancellationRequested) throw new InvalidOperationException("Stop failed to cancel the current turn.");
        using JsonDocument arguments = JsonDocument.Parse("{\"path\":\"sample.txt\"}");
        model.Approvals.Add(new(new ToolRequest(new(Guid.NewGuid(), "file.read", arguments.RootElement.Clone()), new string('A', 64), "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), null)));
        capture("approval-stop.png");
        Button approvalStop = (Button)window.FindName("ApprovalStopButton");
        if (!approvalStop.IsVisible || stop.IsVisible || send.IsVisible) throw new InvalidOperationException("Approval must expose a single compact stop button.");
        model.Approvals.Clear();
        turn.SetValue(model, null); typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(model, false);
        capture("send-restored.png");
        if (stop.IsVisible || !send.IsVisible) throw new InvalidOperationException("Send was not restored after the turn.");
        toggle = (ToggleButton)Group().Template.FindName("WorkspaceToggle", Group()); toggle.Focus();
        capture("sidebar-focus.png");
        if (toggle.FocusVisualStyle is not null || send.FocusVisualStyle is not null || Row(first).FocusVisualStyle is not null) throw new InvalidOperationException("Default focus outline remains.");
        window.OpenProjectMenu(Group(), project, keyboard: true); CapturePopup(window.SidebarMenu!, "workspace-menu.png");
        window.SidebarMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        capture("workspace-removed.png");
        if (model.Projects.Any(p => p.Path == project) || !model.Recent.Contains(first) || !File.Exists(userFile)) throw new InvalidOperationException("Workspace removal must only hide the project.");

        string oldProfile = Path.Combine(root, "old-connection.json"), oldGateway = Path.Combine(root, "old-gateway.secrets.json");
        File.WriteAllText(oldProfile, "fixture-old-pairing"); File.WriteAllText(oldGateway, "fixture-old-gateway");
        model.Settings.ProfilePath = oldProfile; model.Settings.GatewayTokenFile = oldGateway; model.SaveSettings();
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
        Button settings = (Button)window.FindName("SettingsButton");
        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ContextMenu settingsMenu = window.SettingsMenu!;
        CapturePopup(settingsMenu, "settings-menu.png");
        if (!settingsMenu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(new[] { "有线连接", "无线连接", "更换板子／重新绑定" }))
            throw new InvalidOperationException("Settings must separate connection guidance and board replacement.");
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(model, true);
        if (settingsMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled)) throw new InvalidOperationException("Settings actions must disable while work is running.");
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(model, false);
        settingsMenu.IsOpen = false;
        string previousMode = model.Settings.ConnectionMode;
        model.Settings.ConnectionMode = "wireless";
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, false);
        model.Status = "未能通过当前网络连接原盒子。如果更换了 Wi-Fi，请为盒子设置新网络。";
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            WirelessRecoveryWindow prompt = window.OwnedWindows.OfType<WirelessRecoveryWindow>().Single();
            prompt.DialogResult = false;
        });
        window.ShowConnectionRecovery();
        if (model.Settings.ProfilePath != oldProfile || File.ReadAllText(oldProfile) != "fixture-old-pairing")
            throw new InvalidOperationException("Dismissing wireless recovery changed the pairing.");
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            WirelessRecoveryWindow prompt = window.OwnedWindows.OfType<WirelessRecoveryWindow>().Single();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                WirelessConnectionWindow wireless = window.OwnedWindows.OfType<WirelessConnectionWindow>().Single();
                wireless.Close();
            });
            Descendants<Button>(prompt).Single(b => Equals(b.Content, "设置 Wi-Fi")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        window.ShowConnectionRecovery();
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.ConnectionCanceled))!.SetValue(model, true);
        window.ShowConnectionRecovery(); // Stopping a connection must not open another modal.
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.ConnectionCanceled))!.SetValue(model, false);
        model.Settings.ConnectionMode = previousMode;
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
        SubmitReplacement(false);
        settingsMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (!model.Connected || model.Settings.ProfilePath != oldProfile || model.Settings.PairingId != "") throw new InvalidOperationException("Canceling replacement changed the active pairing.");
        string workspace = model.Settings.Workspace; int conversations = model.Conversations.Count;
        SubmitReplacement(true);
        settingsMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (model.Connected || model.Settings.ProfilePath == oldProfile || model.HasConnectionAttempted || model.Settings.Workspace != workspace || model.Conversations.Count != conversations)
            throw new InvalidOperationException("Confirmed replacement did not reset only the connection.");
        if (File.ReadAllText(oldProfile) != "fixture-old-pairing" || File.ReadAllText(oldGateway) != "fixture-old-gateway" || !File.Exists(userFile))
            throw new InvalidOperationException("Replacement changed the previous pairing or user files.");

        void SubmitReplacement(bool confirm)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                BoardReplacementWindow dialog = window.OwnedWindows.OfType<BoardReplacementWindow>().Single();
                CaptureElement((FrameworkElement)dialog.Content, "board-replacement.png");
                if (confirm)
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        ConnectionMethodWindow chooser = window.OwnedWindows.OfType<ConnectionMethodWindow>().Single();
                        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                        {
                            ConnectionSetupWindow guide = window.OwnedWindows.OfType<ConnectionSetupWindow>().Single();
                            CaptureElement((FrameworkElement)guide.Content, "board-replacement-guide.png");
                            if (Descendants<Button>(guide).Single(b => Equals(b.Content, "使用已有配对连接")).IsEnabled || Descendants<PasswordBox>(guide).Single().Password.Length != 0)
                                throw new InvalidOperationException("Replacement guide must not reuse the previous pairing or password.");
                            guide.Close();
                        });
                        Descendants<Button>(chooser).Single(b => Descendants<TextBlock>(b).Any(t => t.Text == "有线连接")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    Descendants<Button>(dialog).Single(b => Equals(b.Content, "确认更换并继续")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else dialog.DialogResult = false;
            });
        }

        Expander Group() => Descendants<Expander>(window).Single(e => e.Name == "WorkspaceGroup" && e.DataContext is ProjectGroup p && p.Path == project);
        ListBoxItem Row(Conversation conversation) => Descendants<ListBoxItem>(window).Single(row => ReferenceEquals(row.DataContext, conversation));
        void SubmitDialog(string? name)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                ConversationDialog dialog = window.OwnedWindows.OfType<ConversationDialog>().Single();
                if (name is not null) Descendants<TextBox>(dialog).Single().Text = name;
                Descendants<Button>(dialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
        void CapturePopup(ContextMenu menu, string name)
        {
            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); menu.UpdateLayout();
            CaptureElement(menu, name);
        }
        void CaptureElement(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(Path.Combine(destination, name)); encoder.Save(output);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
