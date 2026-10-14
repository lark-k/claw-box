using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using Renci.SshNet.Sftp;

namespace ClawBox.Desktop.Tests;

public sealed class BindingFailureTests
{
    [Fact]
    public void Uploaded_file_mode_is_accepted_by_real_sshnet_and_grants_only_owner_read_write()
    {
        SftpFileAttributes attributes = (SftpFileAttributes)Activator.CreateInstance(typeof(SftpFileAttributes),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [DateTime.UtcNow, DateTime.UtcNow, 0L, 0, 0, 0x81FFu, null], null)!;
        // Reproduce the actual crash: the bitmask value contains a non-octal digit.
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => attributes.SetPermissions(384)).ParamName);
        short mode = (short)typeof(SshBoardSetupTransport).GetField("PrivateFileMode", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        attributes.SetPermissions(mode);
        Assert.True(attributes.OwnerCanRead); Assert.True(attributes.OwnerCanWrite);
        Assert.False(attributes.OwnerCanExecute);
        Assert.False(attributes.GroupCanRead); Assert.False(attributes.GroupCanWrite); Assert.False(attributes.GroupCanExecute);
        Assert.False(attributes.OthersCanRead); Assert.False(attributes.OthersCanWrite); Assert.False(attributes.OthersCanExecute);
        Assert.False(attributes.IsUIDBitSet); Assert.False(attributes.IsGroupIDBitSet); Assert.False(attributes.IsStickyBitSet);
        Assert.True(attributes.IsRegularFile);
    }

    [Fact]
    public void Background_binding_argument_failure_is_shown_without_escaping_and_window_can_retry() => RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Binding.Tests", Guid.NewGuid().ToString("N"));
        DesktopSettings settings = new() { ProfilePath = Path.Combine(root, "connection.json"), GatewayTokenFile = Path.Combine(root, "gateway.secrets.json") };
        ConnectionSetupWindow window = new(settings, _ => throw new InvalidOperationException("Must not connect to a real board."), preview: true);
        try
        {
            MethodInfo execute = typeof(ConnectionSetupWindow).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Func<CancellationToken, Task> fail = cancellation => Task.Run(() => throw new ArgumentOutOfRangeException("mode", "private password must not appear"), cancellation);
            Wait((Task)execute.Invoke(window, [fail])!);
            TextBlock summary = Field<TextBlock>(window, "summary");
            Assert.Contains("未完成", summary.Text);
            Assert.DoesNotContain("private password", summary.Text);
            Assert.True(Field<StackPanel>(window, "form").IsEnabled);
            Assert.Equal("关闭", Field<Button>(window, "cancel").Content);
            Assert.False(Field<bool>(window, "busy"));
            int attempts = 0;
            Func<CancellationToken, Task> retry = _ => { attempts++; return Task.CompletedTask; };
            Wait((Task)execute.Invoke(window, [retry])!);
            Assert.Equal(1, attempts);
            Assert.False(Directory.Exists(root));
        }
        finally { window.Close(); }
    });

    private static T Field<T>(ConnectionSetupWindow window, string name) => (T)typeof(ConnectionSetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Wait(Task task)
    {
        DispatcherFrame frame = new(); Stopwatch deadline = Stopwatch.StartNew();
        DispatcherTimer timer = new(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (task.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.True(task.IsCompleted, "Binding operation did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception caught) { error = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
