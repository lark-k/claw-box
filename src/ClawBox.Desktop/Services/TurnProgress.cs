using System.Diagnostics;
using System.Windows.Threading;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class TurnProgress : IDisposable
{
    private readonly Message message;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer;
    private bool disposed;
    private TimeSpan lastFeedback;
    public TurnProgress(Message message)
    {
        this.message = message;
        message.IsProcessing = true;
        message.ProgressEntries.Add(new() { Kind = "status", Text = "正在发送任务，等待盒子返回进展。" });
        message.PropertyChanged += OnFeedback;
        message.ProgressEntries.CollectionChanged += OnProgress;
        Update();
        timer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Update(), Dispatcher.CurrentDispatcher);
        timer.Start();
    }
    private void OnProgress(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => Feedback();
    private void OnFeedback(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Message.Text) or nameof(Message.Activities) or nameof(Message.ProgressEntries)) Feedback();
    }
    private void Feedback() { lastFeedback = clock.Elapsed; message.WaitingHint = ""; }
    private void Update()
    {
        message.Duration = "已处理 " + FormatElapsed(clock.Elapsed);
        if (clock.Elapsed - lastFeedback >= TimeSpan.FromSeconds(20))
            message.WaitingHint = message.AwaitingApproval ? "等待你批准当前操作。" : "暂未收到新进展，继续等待盒子回复。";
    }
    public static string FormatElapsed(TimeSpan elapsed)
    {
        long seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds < 60 ? $"{seconds}秒" : $"{seconds / 60}分 {seconds % 60:00}秒";
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop(); clock.Stop();
        message.PropertyChanged -= OnFeedback;
        message.ProgressEntries.CollectionChanged -= OnProgress;
        message.WaitingHint = "";
        message.IsProcessing = false;
        message.Duration = "用时 " + FormatElapsed(clock.Elapsed);
    }
}
