using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ClawBox.Desktop.Services;

// Owns the forwarding protocol so normal shutdown can cancel remote listeners.
public sealed class SshTunnel : IDisposable
{
    private readonly PrivateKeyFile key;
    private readonly SshClient client;
    private readonly ForwardedPortRemote remote;
    private readonly ForwardedPortLocal local;
    private bool identityMismatch;
    private int disposed;
    public bool Alive => disposed == 0 && client.IsConnected && remote.IsStarted && local.IsStarted;
    public SshTunnel(BoardConnectionProfile profile)
    {
        key = new(profile.IdentityFile);
        ConnectionInfo info = new(profile.BoxHost, profile.SshPort, profile.SshUser, new PrivateKeyAuthenticationMethod(profile.SshUser, key))
        { Timeout = TimeSpan.FromSeconds(10), ChannelCloseTimeout = TimeSpan.FromSeconds(2) };
        foreach (string name in info.HostKeyAlgorithms.Keys.Where(n => n != "ssh-ed25519").ToArray()) info.HostKeyAlgorithms.Remove(name);
        client = new(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = args.HostKeyName == "ssh-ed25519" && "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=') == profile.HostKeyFingerprint;
            identityMismatch = !args.CanTrust;
        };
        remote = new(IPAddress.Loopback, (uint)profile.RemotePort, IPAddress.Loopback, (uint)profile.ExecutorPort);
        local = new("127.0.0.1", (uint)profile.GatewayLocalPort, "127.0.0.1", 18789);
    }
    public async Task Connect(CancellationToken cancellation)
    {
        using CancellationTokenRegistration cancel = cancellation.Register(Dispose);
        try
        {
            await client.ConnectAsync(cancellation);
            client.AddForwardedPort(remote); client.AddForwardedPort(local);
            try { await Task.Run(remote.Start, cancellation); }
            catch (SshException) { throw new RuntimeStartupException(StartupFailureKind.RemoteForward, "板子端反向转发暂不可用。请检查旧连接，或在引导中显式绑定修复。"); }
            try { local.Start(); }
            catch (SocketException) { throw new RuntimeStartupException(StartupFailureKind.LocalPort, "本机聊天转发端口已被占用，请关闭其他连接后重试。"); }
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch (SshAuthenticationException) { throw new RuntimeStartupException(StartupFailureKind.Authentication, "专用隧道密钥认证失败，请检查原配对。"); }
        catch (SshConnectionException) when (identityMismatch) { throw new RuntimeStartupException(StartupFailureKind.HostKey, "设备身份与原配对不一致，连接已停止。"); }
        catch (Exception error) when (error is SshException or SocketException) { throw new RuntimeStartupException(StartupFailureKind.Network, "SSH 网络连接未建立，请检查板子电源和网线。"); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (local.IsStarted) local.Stop(); } catch (Exception error) when (error is SshException or SocketException or ObjectDisposedException) { }
        try { if (remote.IsStarted) remote.Stop(); } catch (Exception error) when (error is SshException or SocketException or ObjectDisposedException) { }
        try { if (client.IsConnected) client.Disconnect(); }
        catch (Exception error) when (error is SshException or SocketException or ObjectDisposedException) { }
        finally { client.Dispose(); local.Dispose(); remote.Dispose(); key.Dispose(); }
    }
}
