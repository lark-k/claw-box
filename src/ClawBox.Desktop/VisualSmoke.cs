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
        model.Selected.Title = "整理办公文件";
        model.Messages.Add(new() { Role = "user", Text = "请整理这份工作记录，生成 Word 文档，并保留原文件。" });
        model.Messages.Add(new() { Role = "assistant", Text = "我会先读取你选择的工作记录，提取事项、负责人和时间，再生成一份清晰的 Word 文档。\n\n每一步文件操作都会请求你确认。你可以查看完整参数，也可以拒绝本次操作。\n\n完成后，新文档会保存到工作目录，原文件保持不变。" });
        model.Status = "界面验收预览 · 示例数据";
        using JsonDocument args = JsonDocument.Parse("""{"path":"C:\\办公工作区\\会议纪要.docx","content":"会议纪要\n一、待办事项\n二、负责人及截止时间"}""");
        MainWindow window = new(model, preview: true) { Width = 1907, Height = 1146, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        window.Show();
        FrameworkElement content = (FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        Capture("chat.png");
        model.Approvals.Add(new(new(new(Guid.NewGuid(), "word.create", args.RootElement.Clone()), new string('A', 64), "pending", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), null)));
        window.PreviewPane();
        Capture("approval.png");
        window.Width = 1200; window.Height = 800;
        Capture("compact.png");
        window.Close();
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
