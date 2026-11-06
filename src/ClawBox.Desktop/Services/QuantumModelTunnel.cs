using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ClawBox.Desktop.Services;

/// <summary>One computer's local-only forwarding connection to the box model gateway.</summary>
public sealed class QuantumModelTunnel : IDisposable
{
    private readonly PrivateKeyFile key;
    private readonly SshClient client;
    private readonly ForwardedPortLocal forward;
    private readonly string endpoint;
    private bool identityMismatch;
    private int disposed;

    public bool Alive => disposed == 0 && client.IsConnected && forward.IsStarted;
    public uint LocalPort => Alive ? forward.BoundPort : throw new InvalidOperationException("Tunnel is not connected.");

    public QuantumModelTunnel(string host, int port, string fingerprint, string identityFile)
    {
        new SetupRequest(host, port, fingerprint).Validate();
        endpoint = $"{host}:{port}";
        if (!Path.IsPathFullyQualified(identityFile) || !File.Exists(identityFile)) throw new ArgumentException("Identity file is unavailable.", nameof(identityFile));
        key = new(identityFile);
        ConnectionInfo info = new(host, port, "root", new PrivateKeyAuthenticationMethod("root", key))
        { Timeout = TimeSpan.FromSeconds(10), ChannelCloseTimeout = TimeSpan.FromSeconds(2) };
        foreach (string algorithm in info.HostKeyAlgorithms.Keys.Where(name => name != "ssh-ed25519").ToArray()) info.HostKeyAlgorithms.Remove(algorithm);
        client = new(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = args.HostKeyName == "ssh-ed25519" && "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=') == fingerprint;
            identityMismatch = !args.CanTrust;
        };
        // Each process receives an independent ephemeral loopback port; no fixed port contention.
        forward = new("127.0.0.1", 0, "127.0.0.1", 18792);
    }

    public async Task Connect(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        using CancellationTokenRegistration registration = cancellation.Register(Dispose);
        try
        {
            await client.ConnectAsync(cancellation);
            client.AddForwardedPort(forward);
            forward.Start();
        }
        catch (Exception error)
        {
            Dispose();
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            if (error is SshAuthenticationException) throw new RuntimeStartupException(StartupFailureKind.Authentication, "盒子未授权当前电脑，请重新配对。");
            if (error is SshConnectionException && identityMismatch) throw new RuntimeStartupException(StartupFailureKind.HostKey, "盒子身份与原配对不一致，连接已停止。");
            if (error is SshException or SocketException)
            {
                string reason = error is SocketException socket ? socket.SocketErrorCode.ToString() : error.GetType().Name;
                throw new RuntimeStartupException(StartupFailureKind.Network, $"无法建立盒子 SSH 连接（{endpoint}，{reason}）。请确认电脑和盒子连接同一 Wi-Fi，且网络允许设备互访；如果使用代理，请检查局域网直连设置。");
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (forward.IsStarted) forward.Stop(); } catch (Exception error) when (error is SshException or SocketException or ObjectDisposedException) { }
        try { if (client.IsConnected) client.Disconnect(); }
        catch (Exception error) when (error is SshException or SocketException or ObjectDisposedException) { }
        finally { client.Dispose(); forward.Dispose(); key.Dispose(); }
    }
}
