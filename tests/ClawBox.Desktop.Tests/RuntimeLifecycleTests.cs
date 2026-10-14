using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class RuntimeLifecycleTests
{
    [Fact]
    public void Closing_app_job_terminates_only_its_owned_background_process()
    {
        using Process owned = Ping(); using Process unrelated = Ping();
        try
        {
            using (OwnedProcessJob job = new())
            {
                job.Add(owned);
                Assert.False(owned.HasExited);
            }
            Assert.True(owned.WaitForExit(5000));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!owned.HasExited) owned.Kill();
            if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(5000); }
        }
    }
    [Fact]
    public void Port_conflict_is_detected_for_non_http_service_without_stopping_its_owner()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        MethodInfo probe = typeof(RuntimeSession).GetMethod("EnsurePortAvailable", BindingFlags.Static | BindingFlags.NonPublic)!;
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => probe.Invoke(null, [port]));
        InvalidOperationException guidance = Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains(port.ToString(), guidance.Message); Assert.Contains("无需重新绑定", guidance.Message);
        using TcpClient client = new(); client.Connect(IPAddress.Loopback, port);
        Assert.True(client.Connected);
        listener.Stop(); probe.Invoke(null, [port]);
    }
    [Theory]
    [InlineData("reused", "reused", false)]
    [InlineData("reused", "paired", false)]
    [InlineData("paired", "reused", true)]
    [InlineData("installed", "configured", true)]
    [InlineData("reused", "configured", true)]
    public void Gateway_is_restarted_only_when_loaded_settings_or_code_change(string plugin, string desktop, bool expected)
    {
        MethodInfo policy = typeof(SshBoardSetupTransport).GetMethod("NeedsRestart", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)policy.Invoke(null, [plugin, desktop])!);
    }
    private static Process Ping()
    {
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-t"); start.ArgumentList.Add("127.0.0.1");
        return Process.Start(start)!;
    }
    [Fact]
    public async Task Next_launch_uses_saved_connection_without_password_and_failure_returns_to_guide()
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Startup.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            LocalState state = new(root);
            DesktopSettings settings = new() { ProfilePath = Path.Combine(root, "connection.json"), GatewayTokenFile = Path.Combine(root, "gateway.secrets.json") };
            File.WriteAllText(settings.ProfilePath, "paired fixture"); File.WriteAllText(settings.GatewayTokenFile, "secret fixture");
            state.Save("settings.json", settings);
            DesktopSettings loaded = new LocalState(root).Load("settings.json", () => new DesktopSettings());
            MethodInfo start = typeof(MainWindow).GetMethod("TryConnectOnStartup", BindingFlags.Static | BindingFlags.NonPublic)!;
            int attempts = 0;
            Func<Task<bool>> connected = () => { attempts++; return Task.FromResult(true); };
            Assert.True(await (Task<bool>)start.Invoke(null, [loaded, connected])!);
            Assert.Equal(1, attempts);
            Func<Task<bool>> disconnected = () => { attempts++; return Task.FromResult(false); };
            Assert.False(await (Task<bool>)start.Invoke(null, [loaded, disconnected])!);
            Assert.Equal(2, attempts);
            File.Delete(loaded.GatewayTokenFile);
            Assert.False(await (Task<bool>)start.Invoke(null, [loaded, connected])!);
            Assert.Equal(2, attempts);
            Assert.DoesNotContain("password", JsonSerializer.Serialize(loaded), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("paired fixture", File.ReadAllText(loaded.ProfilePath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
