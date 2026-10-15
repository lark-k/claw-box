using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

// Real WPF layout, scrolling, tooltips and menu actions with isolated conversation fixtures.
public static class ChatNavigationSmoke
{
    public static void Run(string destination)
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Navigation.Smoke", Guid.NewGuid().ToString("N"));
        LocalState state = new(root);
        Conversation history = new() { Title = "历史消息定位", WorkspacePath = Path.Combine(root, "workspace") };
        for (int i = 0; i < 36; i++) history.Messages.Add(new()
        {
            Role = i % 2 == 0 ? "user" : "assistant",
            Text = $"第 {i + 1} 条消息：核对项目文件与处理结果。\n" + (i % 2 == 0 ? "请保留原有文件，说明这一步的检查结果。" : "已完成本轮检查，保留原文件。\n这条消息用于验证历史定位、预览和滚动位置。")
        });
        new ConversationStore(state).Migrate([history]);
        state.Save("settings.json", new DesktopSettings { Workspace = history.WorkspacePath, ProfilePath = Path.Combine(root, "missing-profile.json") });
        using ShellViewModel model = new(state);
        MainWindow window = new(model, preview: true) { Width = 1200, Height = 800 };
        ((Grid)window.Content).Background = window.Background;
        window.Show(); Flush();
        ScrollViewer scroll = (ScrollViewer)window.FindName("ChatScroll");
        Button latest = (Button)window.FindName("JumpLatest");
        ItemsControl rail = (ItemsControl)window.FindName("MessageRail");
        if (scroll.ScrollableHeight < 400 || !AtBottom()) throw new InvalidOperationException("Saved conversation did not open at the latest message.");
        Capture(window, "chat-latest.png");
        ScrollBar bar = Descendants<ScrollBar>(scroll).Single(b => b.Orientation == Orientation.Vertical);
        RepeatButton[] pages = Descendants<RepeatButton>(bar).ToArray();
        if (bar.ActualWidth > 12.1 || pages.Length != 2 || pages.Any(p => p.Content is not null)) throw new InvalidOperationException($"Scrollbar width={bar.ActualWidth}, buttons={pages.Length}, content={pages.Count(p => p.Content is not null)}.");
        scroll.ScrollToTop(); Flush();
        if (!latest.IsVisible) throw new InvalidOperationException("Reading older messages must reveal the jump button.");
        double readingOffset = scroll.VerticalOffset;
        Message streamed = new() { Text = "最新流式回复" }; model.Messages.Add(streamed); model.Selected.Messages.Add(streamed);
        Flush(); streamed.Text += string.Concat(Enumerable.Repeat("\n新的内容应保留向上阅读的位置。", 10)); Flush();
        if (Math.Abs(scroll.VerticalOffset - readingOffset) > 2) throw new InvalidOperationException("New or streaming content stole the history reading position.");
        Capture(window, "chat-reading-history.png");
        Message target = model.Messages[8];
        Button marker = Descendants<Button>(rail).Single(b => ReferenceEquals(b.Tag, target));
        ToolTip preview = (ToolTip)marker.ToolTip;
        preview.PlacementTarget = marker; preview.IsOpen = true; Flush();
        if (!Descendants<TextBlock>(preview).Any(t => t.Text == target.PreviewText)) throw new InvalidOperationException("Message preview does not show the targeted message.");
        Capture(preview, "chat-message-preview.png");
        preview.IsOpen = false;
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
        ItemsControl list = (ItemsControl)window.FindName("MessageList");
        FrameworkElement targetRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(target);
        if (Math.Abs(targetRow.TranslatePoint(new Point(), scroll).Y - 12) > 2 || !latest.IsVisible)
            throw new InvalidOperationException("Preview marker did not navigate to its message.");
        Capture(window, "chat-message-location.png");
        latest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
        if (!AtBottom() || latest.IsVisible) throw new InvalidOperationException("Jump to latest did not reach the bottom or hide its button.");
        streamed.Text += string.Concat(Enumerable.Repeat("\n在底部时继续跟随新的回复。", 8)); Flush();
        if (!AtBottom()) throw new InvalidOperationException("Streaming at the bottom did not follow the latest message.");
        Conversation first = model.Selected;
        scroll.ScrollToTop(); Flush();
        model.NewRecent(); Flush(); model.Selected = first; Flush();
        if (!AtBottom()) throw new InvalidOperationException("Switching back to a conversation did not show its latest message.");
        Capture(window, "chat-reentered-latest.png");
        Button access = (Button)window.FindName("AccessButton");
        foreach (string mode in new[] { "approval", "readOnly", "fullAccess" })
        {
            access.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            access.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, mode)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            access.ContextMenu.IsOpen = false; Flush();
            TextBlock glyph = (TextBlock)access.Template.FindName("AccessGlyph", access);
            string color = ((SolidColorBrush)access.Foreground).Color.ToString();
            if (glyph.Text != model.AccessIcon || !color.EndsWith(model.AccessForeground[1..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Access chip icon or color does not match its selected mode.");
            Capture(window, "access-" + mode + ".png");
        }
        using (ShellViewModel reopened = new(state))
            if (reopened.Settings.AccessMode != "fullAccess") throw new InvalidOperationException("Last selected access mode was not restored.");
        window.Close();
        Directory.Delete(root, true);
        bool AtBottom() => scroll.ScrollableHeight - scroll.VerticalOffset < 2;
        static void Flush()
        {
            for (int i = 0; i < 2; i++)
            {
                DispatcherFrame frame = new();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
        }
        void Capture(FrameworkElement targetElement, string name)
        {
            Flush();
            FrameworkElement surface = targetElement is Window targetWindow ? (FrameworkElement)targetWindow.Content : targetElement;
            surface.UpdateLayout();
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(destination, name)); encoder.Save(file);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
