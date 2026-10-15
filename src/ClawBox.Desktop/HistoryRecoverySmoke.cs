using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

public static class HistoryRecoverySmoke
{
    public static void Run(string destination)
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.HistorySmoke", Guid.NewGuid().ToString("N"));
        LocalState state = new(root); ConversationStore store = new(state); Conversation chat = new();
        store.Migrate([chat]);
        string path = store.FilePath(chat.Id, chat.CreatedAt); byte[] original = new byte[712792]; File.WriteAllBytes(path, original);
        using ShellViewModel model = new(state);
        MainWindow window = new(model, preview: true) { Width = 1200, Height = 800 };
        try
        {
            window.Show();
            ((System.Windows.Controls.Grid)window.Content).Background = window.Background;
            if (!model.Selected.HistoryUnavailable || !model.Messages.Any(m => m.Text.Contains("内容尚未恢复")))
                throw new InvalidOperationException("Corrupt history must show a read-only recovery notice.");
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
            if (model.CanSend) throw new InvalidOperationException("Corrupt history must not accept new tasks.");
            Capture("history-recovery.png");
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, false);
            model.NewRecent();
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
            if (!model.CanSend || model.Selected.HistoryUnavailable) throw new InvalidOperationException("A new chat must remain usable.");
            Capture("history-new-chat.png");
            if (!File.ReadAllBytes(path).SequenceEqual(original)) throw new InvalidOperationException("Corrupt original was overwritten.");
        }
        finally { window.Close(); }
        // Reopen persisted streaming fragments through the real window templates.
        Conversation partial = new() { Messages = [new() { Text = "**" }, new() { Text = "***" }, new() { Text = "**unfinished" }] };
        store.Save(partial); store.SaveIndex([partial]);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using ShellViewModel reopened = new(state);
            window = new(reopened, preview: true) { Width = 1200, Height = 800 };
            try
            {
                window.Show(); ((System.Windows.Controls.Grid)window.Content).Background = window.Background;
                Capture("history-partial-markdown.png");
                if (reopened.Selected.HistoryUnavailable || reopened.Messages[0].Text != "**")
                    throw new InvalidOperationException("Partial Markdown must reopen without altering history.");
            }
            finally { window.Close(); }
        }
        void Capture(string name)
        {
            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            FrameworkElement content = (FrameworkElement)window.Content; content.UpdateLayout();
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(Path.Combine(destination, name)); encoder.Save(output);
        }
    }
}
