using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawBox.Contracts;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

public static class VisualSmoke
{
    public static void Run(string destination)
    {
        Directory.CreateDirectory(destination);
        string temporary = Path.Combine(Path.GetTempPath(), "QuantumHarness.Visual", Guid.NewGuid().ToString("N"));
        using ShellViewModel model = new(new LocalState(temporary));
        model.AddProject(@"C:\办公工作区");
        model.NewConversation(); model.Selected.Title = "修复项目登录功能";
        model.NewRecent(); model.Selected.Title = "整理会议记录";
        model.Selected = model.Conversations[1];
        model.Messages.Add(new() { Role = "user", Text = "修复项目登录功能，并说明哪些文件发生了修改。" });
        Message reply = new() { Role = "assistant", Duration = "用时 02:16", Text = "已修复登录表单校验，并更新使用说明。\n\n- 空白用户名现在会显示提示。\n- 相关构建和测试已通过。\n\n本轮修改见下方文件记录。" };
        reply.Activities.Add(new(Guid.NewGuid(), "已完成 · file.read", @"C:\办公工作区\src\Login.cs"));
        reply.Activities.Add(new(Guid.NewGuid(), "已完成 · file.write", @"C:\办公工作区\src\Login.cs"));
        reply.Activities.Add(new(Guid.NewGuid(), "已完成 · project.check", "隔离构建 / 测试\nexitCode: 0\nchecksPassed: true"));
        FileChange change = FileChanges.Compare(@"C:\办公工作区\src\Login.cs", @"C:\办公工作区", new(true, "return user != null;\n"), new(true, "if (string.IsNullOrWhiteSpace(user))\n    return false;\nreturn Validate(user);\n"));
        reply.Changes.Add(change); model.Changes.Add(change);
        model.Messages.Add(reply);
        model.Status = "界面验收预览 · 示例数据";
        using JsonDocument args = JsonDocument.Parse("""{"path":"C:\\办公工作区\\会议纪要.docx","content":"会议纪要\n一、待办事项\n二、负责人及截止时间"}""");
        MainWindow window = new(model, preview: true) { Width = 1907, Height = 1146, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        FrameworkElement content = (FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        Capture("chat.png");
        // Exercise notifications after the UI has already bound to these collections.
        reply.Activities.Add(new(Guid.NewGuid(), "已完成 · file.replace", @"C:\办公工作区\README.md"));
        FileChange readme = FileChanges.Compare(@"C:\办公工作区\README.md", @"C:\办公工作区", new(true, "旧使用说明\n"), new(true, "已更新使用说明\n"));
        reply.Changes.Add(readme); model.Changes.Add(readme);
        content.UpdateLayout();
        if (!Descendants<System.Windows.Controls.ItemsControl>(window).Any(control => ReferenceEquals(control.ItemsSource, reply.Changes) && control.Items.Count == 2))
            throw new InvalidOperationException("Live file changes did not update.");
        window.PreviewPane();
        Capture("changes.png");
        foreach (System.Windows.Controls.Expander expander in Descendants<System.Windows.Controls.Expander>(window)) expander.IsExpanded = true;
        Capture("activity.png");
        model.SetAccess("fullAccess");
        Capture("full-access.png");
        model.SetAccess("approval");
        model.Approvals.Add(new(new(new(Guid.NewGuid(), "word.create", args.RootElement.Clone()), new string('A', 64), "pending", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), null)));
        // Approvals replace the composer and never open the changes pane.
        Capture("approval.png");
        window.Width = 1200; window.Height = 800;
        Capture("compact.png");
        model.Approvals.Clear();
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
        Capture("connected.png");
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, false);
        window.Width = 1907; window.Height = 1146;
        foreach (System.Windows.Controls.Button button in Descendants<System.Windows.Controls.Button>(window).Where(b => Equals(b.ToolTip, "查看本轮文件变更")).ToArray())
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        model.Messages.Clear();
        Message user = new() { Role = "user", Text = "归纳一下该工作区里面有什么内容？" };
        model.Messages.Add(user);
        Message working = new(); using TurnProgress progress = new(working); TurnTranscript transcript = new(working);
        model.Messages.Add(working);
        Capture("thinking-start.png");
        System.Windows.Controls.Border bubble = Descendants<System.Windows.Controls.Border>(window).Single(b => b.Name == "MessageSurface" && ReferenceEquals(b.DataContext, user));
        if (bubble.ActualHeight > 46) throw new InvalidOperationException("Single-line user bubble has a blank extra line.");
        if (!Descendants<ThinkingIndicator>(window).Any(t => t.IsVisible && t.Active)) throw new InvalidOperationException("Immediate thinking feedback is missing.");
        ThinkingIndicator thinking = Descendants<ThinkingIndicator>(window).Single(t => t.IsVisible && t.Active);
        if (SystemParameters.ClientAreaAnimation && (thinking.Foreground is not LinearGradientBrush glow || !glow.RelativeTransform.HasAnimatedProperties))
            throw new InvalidOperationException("Thinking light scan is not animated.");
        System.Windows.Controls.Expander process = Descendants<System.Windows.Controls.Expander>(window).Single(e => e.Name == "Activity" && ReferenceEquals(e.DataContext, working));
        if (!process.IsExpanded) throw new InvalidOperationException("Running process did not expand.");
        transcript.Append(TurnTranscript.ProgressMarker + "我会先浏览工作区，再读取相关说明文件。");
        working.ProgressEntries.Add(new() { Kind = "tool", Text = "已完成浏览工作区：当前工作区。", Detail = "已完成 · directory.list" });
        working.ProgressEntries.Add(new() { Kind = "command", Text = "已执行项目构建检查。", Detail = "已完成 · project.check\nexitCode: 0\nchecksPassed: true" });
        Pump(TimeSpan.FromMilliseconds(450)); Capture("thinking-scan.png");
        transcript.Append(TurnTranscript.ProgressMarker + "已找到项目说明和源码，正在核对目录用途。");
        System.Diagnostics.Stopwatch feedbackDeadline = System.Diagnostics.Stopwatch.StartNew();
        while (working.Duration == "已处理 0秒" && feedbackDeadline.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(250));
        Capture("thinking-progress.png");
        System.Windows.Controls.Expander inline = Descendants<System.Windows.Controls.Expander>(window).First(e => e.Name == "ProgressRow" && e.IsVisible && e.DataContext is ProgressEntry entry && entry.Kind == "tool" && entry.Detail.Length > 0);
        System.Windows.Controls.Primitives.ToggleButton inlineToggle = (System.Windows.Controls.Primitives.ToggleButton)inline.Template.FindName("ProgressToggle", inline);
        System.Windows.Controls.TextBlock arrow = (System.Windows.Controls.TextBlock)inlineToggle.Template.FindName("InlineChevron", inlineToggle);
        if (!arrow.IsVisible || Descendants<System.Windows.Controls.TextBlock>(window).Any(t => t.IsVisible && t.Text == "详情"))
            throw new InvalidOperationException("Progress details must use an inline arrow without a details label.");
        System.Windows.Controls.TextBlock pencil = (System.Windows.Controls.TextBlock)inlineToggle.Template.FindName("ToolIcon", inlineToggle);
        if (!pencil.IsVisible || pencil.Text != "\uE70F") throw new InvalidOperationException("Tool progress is missing its pencil icon.");
        inlineToggle.IsChecked = true; Capture("inline-progress-expanded.png");
        if (!inline.IsExpanded) throw new InvalidOperationException("Inline arrow failed to expand details.");
        inlineToggle.IsChecked = false; Capture("inline-progress-collapsed.png");
        if (inline.IsExpanded) throw new InvalidOperationException("Inline arrow failed to collapse details.");
        if (Descendants<System.Windows.Controls.Expander>(window).Any(e => e.Name == "ProgressRow" && e.IsVisible && e.DataContext is ProgressEntry entry && entry.Kind is "agent" or "status"))
            throw new InvalidOperationException("Assistant narration must not have a details expander.");
        if (!Descendants<System.Windows.Controls.TextBlock>(window).Any(t => t.Name == "NarrationRow" && t.IsVisible && t.DataContext is ProgressEntry entry && entry.Kind == "agent" && entry.Detail.Length > 0))
            throw new InvalidOperationException("Assistant narration must remain visible as plain text.");
        System.Windows.Controls.Expander command = Descendants<System.Windows.Controls.Expander>(window).First(e => e.Name == "ProgressRow" && e.IsVisible && e.DataContext is ProgressEntry entry && entry.Kind == "command");
        System.Windows.Controls.Primitives.ToggleButton commandToggle = (System.Windows.Controls.Primitives.ToggleButton)command.Template.FindName("ProgressToggle", command);
        System.Windows.Controls.TextBlock terminal = (System.Windows.Controls.TextBlock)commandToggle.Template.FindName("ToolIcon", commandToggle);
        if (!terminal.IsVisible || terminal.Text != "\uE756") throw new InvalidOperationException("Command progress is missing its terminal icon.");
        if (working.Duration == "已处理 0秒") throw new InvalidOperationException("Live elapsed time did not advance.");
        // The gateway separates assistant/tool messages with whitespace-only chunks.
        transcript.SealBeforeTool();
        ThinkingIndicator liveIndicator = Descendants<ThinkingIndicator>(window).Single(t => t.IsVisible && t.Active);
        double baseline = liveIndicator.TranslatePoint(new Point(), content).Y;
        for (int i = 0; i < 4; i++) transcript.Append("\n\n");
        Capture("stream-whitespace.png");
        System.Windows.Controls.Border replySurface = Descendants<System.Windows.Controls.Border>(window).Single(b => b.Name == "MessageSurface" && ReferenceEquals(b.DataContext, working));
        if (replySurface.IsVisible || Math.Abs(liveIndicator.TranslatePoint(new Point(), content).Y - baseline) > 1)
            throw new InvalidOperationException("Whitespace-only streaming chunks introduced a visible gap.");
        transcript.Append("创建 `src/App.vue`：\n\n");
        Capture("stream-next-line.png");
        if (replySurface.ActualHeight > 30) throw new InvalidOperationException("Streaming boundary whitespace added blank lines around narration.");
        transcript.SealBeforeTool();
        transcript.Append(TurnTranscript.ProgressMarker + "\n\n");
        Capture("stream-empty-progress.png");
        if (Descendants<System.Windows.Controls.Grid>(window).Any(g => g.Name == "ProgressSurface" && g.IsVisible && g.DataContext is ProgressEntry entry && entry.Text.Length == 0))
            throw new InvalidOperationException("An empty progress marker introduced a visible row.");
        transcript.Append(TurnTranscript.FinalMarker + "该工作区包含项目源码、使用说明和测试目录。\n\n本次只浏览和读取文件，没有修改用户文件。");
        transcript.Complete(); progress.Dispose();
        Capture("thinking-complete.png");
        if (process.IsExpanded || Descendants<ThinkingIndicator>(window).Any(t => t.IsVisible)) throw new InvalidOperationException("Completed process did not collapse.");
        process.IsExpanded = true; Capture("thinking-review.png");
        user.Text = "请归纳工作区中的项目结构、各目录用途、入口文件和测试方式，并区分源码、说明文档与生成文件。还要说明哪些文件是运行时入口、哪些文件用于测试，以及当前项目的构建方式；请保留原文件，不要修改工作区内容。";
        window.Width = 1200; window.Height = 800; Capture("bubble-wrap.png");
        SidebarSmoke.Run(window, model, temporary, destination, Capture);
        window.Close();
        static void Pump(TimeSpan duration)
        {
            DispatcherFrame frame = new();
            DispatcherTimer wait = new(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            wait.Start(); Dispatcher.PushFrame(frame); wait.Stop();
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
        void Capture(string name)
        {
            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            content.UpdateLayout();
            int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(Path.Combine(destination, name)); encoder.Save(output);
        }
    }
}
