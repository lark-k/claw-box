using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

[CollectionDefinition("Local connection probes", DisableParallelization = true)]
public sealed class LocalConnectionCollection { }

[Collection("Local connection probes")]
public sealed class ConnectionTests
{
    [Fact]
    public async Task Startup_health_probe_bypasses_a_proxy_that_intercepts_loopback()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        Uri url = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/health");
        Task server = Serve(listener, timeout.Token);
        IWebProxy original = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy("http://192.0.2.1:9") { BypassProxyOnLocal = false };
            Assert.False(HttpClient.DefaultProxy.IsBypassed(url));
            using HttpClient client = RuntimeSession.CreateHealthClient();
            Assert.Equal("ok", await client.GetStringAsync(url, timeout.Token));
        }
        finally
        {
            HttpClient.DefaultProxy = original;
            timeout.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
    }
    private static async Task Serve(TcpListener listener, CancellationToken cancellation)
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync(cancellation);
        await using NetworkStream stream = peer.GetStream();
        using StreamReader reader = new(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellation) is string line && line.Length > 0) { }
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response, cancellation);
    }
}
