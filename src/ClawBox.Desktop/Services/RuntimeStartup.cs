namespace ClawBox.Desktop.Services;

public enum StartupFailureKind { Unknown, RemoteForward, Network, LocalPort, Authentication, HostKey, ExecutorState }

public sealed class RuntimeStartupException(StartupFailureKind kind, string message) : InvalidOperationException(message)
{
    public StartupFailureKind Kind { get; } = kind;
}

// Keep only a fixed error category. Raw subprocess output never reaches UI or disk.
public sealed class RuntimeStartupDiagnostics(bool tunnel)
{
    private int failure;
    private int forwardingReady;
    public StartupFailureKind Kind => (StartupFailureKind)Volatile.Read(ref failure);
    public bool ForwardingReady => Volatile.Read(ref forwardingReady) != 0;
    public async Task Drain(StreamReader stream)
    {
        char[] buffer = new char[2048];
        string tail = "";
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer)) != 0)
            {
                string text = tail + new string(buffer, 0, count);
                if (tunnel && text.Contains("remote forward success for:", StringComparison.Ordinal)) Volatile.Write(ref forwardingReady, 1);
                StartupFailureKind next = Classify(text, tunnel);
                int current;
                do { current = Volatile.Read(ref failure); }
                while ((int)next > current && Interlocked.CompareExchange(ref failure, (int)next, current) != current);
                tail = text.Length > 128 ? text[^128..] : text;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private static StartupFailureKind Classify(string text, bool tunnel)
    {
        bool Has(string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);
        if (!tunnel) return Has("Another executor is using this data directory") ? StartupFailureKind.ExecutorState : StartupFailureKind.Unknown;
        if (Has("Host key verification failed") || Has("REMOTE HOST IDENTIFICATION HAS CHANGED")) return StartupFailureKind.HostKey;
        if (Has("Address already in use") || Has("cannot listen to port") || (Has("bind [") && Has("Permission denied"))) return StartupFailureKind.LocalPort;
        if (Has("Permission denied") || Has("UNPROTECTED PRIVATE KEY FILE")) return StartupFailureKind.Authentication;
        if (Has("Connection timed out") || Has("Connection refused") || Has("No route to host") || Has("Could not resolve hostname")) return StartupFailureKind.Network;
        if (Has("remote port forwarding failed for listen port")) return StartupFailureKind.RemoteForward;
        return StartupFailureKind.Unknown;
    }
    public RuntimeStartupException Error(int executorPort, int gatewayPort, int remotePort) => new(Kind, Kind switch
    {
        StartupFailureKind.RemoteForward => $"板子端转发端口 {remotePort} 无法监听。请在连接引导输入密码并点击“绑定并连接”，检查并回收已确认断开的本机旧隧道；活动连接会保留。也请关闭其他电脑上的 APP。",
        StartupFailureKind.LocalPort => $"SSH 本地端口 {gatewayPort} 无法监听。请关闭占用该端口的程序后使用已有配对连接，无需重新绑定。",
        StartupFailureKind.Network => "SSH 网络连接失败。请检查板子电源、网线、设备地址及重启后的网络共享，再使用已有配对连接。",
        StartupFailureKind.Authentication => "SSH 配对密钥认证失败。请检查原配对密钥及文件权限；软件未修改已有配对。",
        StartupFailureKind.HostKey => "板子 SSH 主机密钥与原配对记录不一致，连接已停止。请核对设备身份。",
        StartupFailureKind.ExecutorState => "已有执行器正在使用本机数据目录。请关闭旧 APP 或手动启动的执行器后使用已有配对连接，无需删除数据或重新绑定。",
        _ => tunnel ? "SSH 隧道在启动时退出。请检查设备网络、原配对密钥及板子 SSH 转发配置；已有配对保留。"
            : $"本机执行器启动失败（端口 {executorPort}）。请检查工作目录权限、执行器数据及完整应用包；已有数据和配对保留。"
    });
}

public static class SshStartupRecovery
{
    // Retry connection establishment only, never a chat, approval or tool request.
    public static async Task Run(Func<Task> start, CancellationToken cancellation, Func<TimeSpan, CancellationToken, Task>? delay = null, Action? retrying = null)
    {
        delay ??= Task.Delay;
        for (int retry = 0; ; retry++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { await start(); return; }
            catch (RuntimeStartupException error) when (error.Kind == StartupFailureKind.RemoteForward && retry < 12)
            {
                retrying?.Invoke();
                await delay(TimeSpan.FromSeconds(5), cancellation);
            }
        }
    }
}
