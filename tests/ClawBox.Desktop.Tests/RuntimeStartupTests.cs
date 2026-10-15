using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class RuntimeStartupTests
{
    [Theory]
    [InlineData(true, "Error: remote port forwarding failed for listen port 18791", StartupFailureKind.RemoteForward, "板子端转发端口 18791")]
    [InlineData(true, "bind [127.0.0.1]:18889: Address already in use", StartupFailureKind.LocalPort, "SSH 本地端口 18889")]
    [InlineData(true, "bind [127.0.0.1]:18889: Permission denied", StartupFailureKind.LocalPort, "SSH 本地端口 18889")]
    [InlineData(true, "Permission denied (publickey).", StartupFailureKind.Authentication, "密钥认证失败")]
    [InlineData(true, "WARNING: UNPROTECTED PRIVATE KEY FILE!", StartupFailureKind.Authentication, "密钥认证失败")]
    [InlineData(true, "Host key verification failed.", StartupFailureKind.HostKey, "主机密钥")]
    [InlineData(true, "REMOTE HOST IDENTIFICATION HAS CHANGED!", StartupFailureKind.HostKey, "主机密钥")]
    [InlineData(true, "connect to host 192.0.2.1 port 22: Connection timed out", StartupFailureKind.Network, "网络连接失败")]
    [InlineData(true, "Connection refused", StartupFailureKind.Network, "网络连接失败")]
    [InlineData(false, "Another executor is using this data directory.", StartupFailureKind.ExecutorState, "数据目录")]
    [InlineData(false, "arbitrary executor exception", StartupFailureKind.Unknown, "本机执行器启动失败")]
    public async Task Startup_errors_identify_the_failed_component_without_exposing_output(bool tunnel, string output, StartupFailureKind kind, string guidance)
    {
        RuntimeStartupDiagnostics diagnostics = new(tunnel);
        const string secret = "sensitive-fixture-token-and-document-text";
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(secret + "\n" + output)));
        await diagnostics.Drain(reader);
        RuntimeStartupException error = diagnostics.Error(18790, 18889, 18791);
        Assert.Equal(kind, error.Kind); Assert.Contains(guidance, error.Message);
        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain(output, error.Message);
    }
    [Fact]
    public async Task Split_output_and_later_noise_do_not_lose_the_original_failure()
    {
        RuntimeStartupDiagnostics diagnostics = new(true);
        string text = new('x', 2035);
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(text + "remote port forwarding failed for listen port 18791\n" + new string('y', 5000))));
        await diagnostics.Drain(reader);
        Assert.Equal(StartupFailureKind.RemoteForward, diagnostics.Kind);
    }
    [Fact]
    public async Task Transient_remote_forward_failure_retries_connection_until_it_recovers()
    {
        int connections = 0, waits = 0, progress = 0;
        await SshStartupRecovery.Run(() =>
        {
            if (++connections < 3) throw new RuntimeStartupException(StartupFailureKind.RemoteForward, "fixture");
            return Task.CompletedTask;
        }, CancellationToken.None, (duration, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), duration); waits++; return Task.CompletedTask;
        }, () => progress++);
        Assert.Equal(3, connections); Assert.Equal(2, waits); Assert.Equal(2, progress);
    }
    [Fact]
    public async Task Persistent_remote_conflict_stops_after_sixty_seconds_of_waiting()
    {
        int attempts = 0; TimeSpan waited = TimeSpan.Zero;
        RuntimeStartupException original = new(StartupFailureKind.RemoteForward, "fixture");
        RuntimeStartupException error = await Assert.ThrowsAsync<RuntimeStartupException>(() => SshStartupRecovery.Run(() =>
        {
            attempts++; throw original;
        }, CancellationToken.None, (duration, _) => { waited += duration; return Task.CompletedTask; }));
        Assert.Same(original, error); Assert.Equal(13, attempts); Assert.Equal(TimeSpan.FromSeconds(60), waited);
    }
    [Theory]
    [InlineData(StartupFailureKind.Unknown)]
    [InlineData(StartupFailureKind.Authentication)]
    [InlineData(StartupFailureKind.HostKey)]
    [InlineData(StartupFailureKind.LocalPort)]
    [InlineData(StartupFailureKind.ExecutorState)]
    [InlineData(StartupFailureKind.Network)]
    public async Task Other_failures_are_returned_immediately(StartupFailureKind kind)
    {
        int attempts = 0;
        await Assert.ThrowsAsync<RuntimeStartupException>(() => SshStartupRecovery.Run(() =>
        {
            attempts++; throw new RuntimeStartupException(kind, "fixture");
        }, CancellationToken.None, (_, _) => throw new InvalidOperationException("must not wait")));
        Assert.Equal(1, attempts);
    }
    [Fact]
    public async Task Cancellation_during_wait_prevents_another_connection_attempt()
    {
        using CancellationTokenSource cancellation = new(); int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshStartupRecovery.Run(() =>
        {
            attempts++; throw new RuntimeStartupException(StartupFailureKind.RemoteForward, "fixture");
        }, cancellation.Token, async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); }));
        Assert.Equal(1, attempts);
    }
    [Fact]
    public async Task Healthy_chat_endpoint_requires_confirmation_of_the_reverse_tool_forward()
    {
        using RuntimeSession runtime = new();
        MethodInfo launch = typeof(RuntimeSession).GetMethod("Launch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = launch.Invoke(runtime, [Hidden(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-t", "127.0.0.1")]);
        Process tunnel = (Process)launch.Invoke(runtime, [Hidden(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-t", "127.0.0.1")])!;
        MethodInfo ready = typeof(RuntimeSession).GetMethod("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using HttpClient health = new(new Healthy());
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            (Task)ready.Invoke(runtime, [health, "http://127.0.0.1:1/fixture", timeout.Token, true])!);
        Assert.False(runtime.Alive);
    }
    private sealed class Healthy : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
    [Fact]
    public async Task Real_process_exit_is_drained_before_reporting_the_remote_forward_error()
    {
        using RuntimeSession runtime = new();
        typeof(RuntimeSession).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime,
            new BoardConnectionProfile { ExecutorPort = 18790, GatewayLocalPort = 18889, RemotePort = 18791 });
        MethodInfo launch = typeof(RuntimeSession).GetMethod("Launch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ProcessStartInfo ping = Hidden(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-t", "127.0.0.1");
        Process executor = (Process)launch.Invoke(runtime, [ping])!;
        ProcessStartInfo fail = Hidden(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('Error: remote port forwarding failed for listen port 18791'); exit 1");
        Process tunnel = (Process)launch.Invoke(runtime, [fail])!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await tunnel.WaitForExitAsync(timeout.Token);
        MethodInfo ready = typeof(RuntimeSession).GetMethod("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using HttpClient health = RuntimeSession.CreateHealthClient();
        RuntimeStartupException error = await Assert.ThrowsAsync<RuntimeStartupException>(() =>
            (Task)ready.Invoke(runtime, [health, "http://127.0.0.1:1/unused", timeout.Token, false])!);
        Assert.Equal(StartupFailureKind.RemoteForward, error.Kind);
        typeof(RuntimeSession).GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [tunnel]);
        Assert.False(executor.HasExited);
    }
    private static ProcessStartInfo Hidden(string executable, params string[] arguments)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
