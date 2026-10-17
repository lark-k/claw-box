using System.Security.Cryptography;
using System.Text.Json;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

[Collection("Bluetooth")]
public sealed class WirelessReconnectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClawBox.WirelessReconnect", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Key = [0, 0, 0, 11, .. "ssh-ed25519"u8.ToArray(), 0, 0, 0, 32, .. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static string Known(string host) => host + " ssh-ed25519 " + Convert.ToBase64String(Key);
    private DesktopSettings Pairing()
    {
        Directory.CreateDirectory(root);
        BoardConnectionProfile profile = new()
        {
            Version = 1,
            BoxHost = "192.0.2.2",
            SshPort = 22,
            SshUser = "root",
            ExecutorPort = 18790,
            RemotePort = 18791,
            KnownHostsFile = Path.Combine(root, "known_hosts"),
            IdentityFile = Path.Combine(root, "key"),
            HostKeyFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Key)).TrimEnd('=')
        };
        File.WriteAllText(profile.KnownHostsFile, Known(profile.BoxHost)); File.WriteAllText(profile.IdentityFile, "original-key");
        string path = Path.Combine(root, "connection.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new() { ProfilePath = path, ConnectionMode = "wireless", WirelessHost = profile.BoxHost };
    }
    [Fact]
    public async Task Startup_reuses_reachable_pinned_address_without_requiring_bluetooth()
    {
        DesktopSettings settings = Pairing();
        WirelessReconnect reconnect = new((host, _, _) => Task.FromResult(Known(host)), (_, _) => throw new Exception("Bluetooth must not be required"));
        Assert.False(await reconnect.Resolve(settings, CancellationToken.None));
    }
    [Fact]
    public async Task Changed_address_is_verified_then_saved_without_changing_keys_or_credentials()
    {
        DesktopSettings settings = Pairing(); BoardConnectionProfile before = BoardConnectionProfile.Load(settings.ProfilePath);
        WirelessReconnect reconnect = new((host, _, _) => host == "192.0.2.2" ? throw new IOException("old address") : Task.FromResult(Known(host)),
            (profile, _) => { Assert.Equal(before.HostKeyFingerprint, profile.HostKeyFingerprint); return Task.FromResult<string?>("192.0.2.5"); });
        Assert.True(await reconnect.Resolve(settings, CancellationToken.None));
        BoardConnectionProfile after = BoardConnectionProfile.Load(settings.ProfilePath);
        Assert.Equal("192.0.2.5", after.BoxHost); Assert.Equal(after.BoxHost, settings.WirelessHost);
        Assert.Equal(before.HostKeyFingerprint, after.HostKeyFingerprint); Assert.Equal(before.IdentityFile, after.IdentityFile);
        Assert.Equal("original-key", File.ReadAllText(after.IdentityFile));
        BoardSetup.ValidateKnownHost(new(after.BoxHost, 22, after.HostKeyFingerprint), File.ReadAllText(after.KnownHostsFile));
    }
    [Fact]
    public async Task Different_host_at_discovered_address_cannot_replace_pairing()
    {
        DesktopSettings settings = Pairing(); string before = File.ReadAllText(settings.ProfilePath);
        WirelessReconnect reconnect = new((host, _, _) => host == "192.0.2.2" ? throw new IOException() : Task.FromResult(host + " ssh-ed25519 AAAA"),
            (_, _) => Task.FromResult<string?>("192.0.2.5"));
        await Assert.ThrowsAsync<SetupException>(() => reconnect.Resolve(settings, CancellationToken.None));
        Assert.Equal(before, File.ReadAllText(settings.ProfilePath)); Assert.Equal("192.0.2.2", settings.WirelessHost);
    }
    [Fact]
    public async Task Stop_during_probe_never_discovers_or_updates_an_address()
    {
        DesktopSettings settings = Pairing(); using CancellationTokenSource stop = new();
        WirelessReconnect reconnect = new((_, _, _) => { stop.Cancel(); throw new OperationCanceledException(); }, (_, _) => throw new Exception("No discovery"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect.Resolve(settings, stop.Token));
        Assert.Equal("192.0.2.2", BoardConnectionProfile.Load(settings.ProfilePath).BoxHost);
    }
    [Fact]
    public async Task Wired_connection_does_not_run_wireless_recovery()
    {
        DesktopSettings settings = Pairing(); settings.ConnectionMode = "wired";
        WirelessReconnect reconnect = new((_, _, _) => throw new Exception("No probe"), (_, _) => throw new Exception("No discovery"));
        Assert.False(await reconnect.Resolve(settings, CancellationToken.None));
    }
    [Fact]
    public void Current_network_uses_raw_ssid_and_saved_network_is_clearly_labeled()
    {
        WirelessNetwork network = new("测试网络", "bytes-a", 80, true, true);
        Assert.Contains("已保存", network.ToString());
        Assert.False(network.IsCurrent(new(true, ["192.0.2.2"], network.Ssid, "bytes-b")));
        Assert.True(network.IsCurrent(new(true, ["192.0.2.2"], network.Ssid, "bytes-a")));
    }
    [LiveWirelessFact]
    public async Task Native_startup_recovery_finds_real_paired_board_from_stale_address_without_wifi_write()
    {
        BoardConnectionProfile original = BoardConnectionProfile.Load(Environment.GetEnvironmentVariable("CLAWBOX_WIRELESS_PROFILE")!);
        Directory.CreateDirectory(root);
        string[] known = File.ReadAllText(original.KnownHostsFile).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        original.BoxHost = "192.0.2.2"; original.KnownHostsFile = Path.Combine(root, "known_hosts");
        File.WriteAllText(original.KnownHostsFile, original.BoxHost + " " + known[1] + " " + known[2]);
        string path = Path.Combine(root, "connection.json");
        File.WriteAllText(path, JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        DesktopSettings settings = new() { ProfilePath = path, ConnectionMode = "wireless" };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(75));
        Assert.True(await new WirelessReconnect().Resolve(settings, deadline.Token));
        BoardConnectionProfile current = BoardConnectionProfile.Load(path);
        Assert.NotEqual("192.0.2.2", current.BoxHost);
        Assert.Equal(original.HostKeyFingerprint, current.HostKeyFingerprint);
        Assert.Equal(original.IdentityFile, current.IdentityFile);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreachable_address_after_bluetooth_is_a_reported_failure_instead_of_an_app_crash(bool startup)
    {
        DesktopSettings settings = Pairing();
        settings.GatewayTokenFile = Path.Combine(root, "gateway.secrets.json");
        File.WriteAllText(settings.GatewayTokenFile, "unchanged-gateway");
        LocalState state = new(Path.Combine(root, "desktop")); state.Save("settings.json", settings);
        int probes = 0;
        WirelessReconnect reconnect = new((_, _, _) => { probes++; throw new SetupException("network", "设备网络不可达"); },
            (_, _) => Task.FromResult<string?>("192.0.2.5"));
        using ShellViewModel model = new(state, reconnect);
        string profile = File.ReadAllText(settings.ProfilePath);
        async Task Connect()
        {
            if (!startup) { await model.Connect(); return; }
            Func<Task<bool>> callback = async () => { await model.Connect(); return model.Connected; };
            System.Reflection.MethodInfo method = typeof(MainWindow).GetMethod("TryConnectOnStartup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert.False(await (Task<bool>)method.Invoke(null, [model.Settings, callback])!);
        }
        Assert.Null(await Record.ExceptionAsync(Connect));
        Assert.Equal(2, probes); Assert.False(model.Connected); Assert.False(model.Busy); Assert.False(model.IsConnecting);
        Assert.Equal("连接盒子", model.ConnectionButtonLabel);
        Assert.Equal(profile, File.ReadAllText(settings.ProfilePath));
        Assert.Equal("unchanged-gateway", File.ReadAllText(settings.GatewayTokenFile));
        Assert.Contains("Wi-Fi", model.Status);
    }
    [Fact]
    public async Task Unresponsive_discovery_has_a_deadline_and_keeps_original_pairing()
    {
        DesktopSettings settings = Pairing(); string before = File.ReadAllText(settings.ProfilePath);
        TaskCompletionSource<string?> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WirelessReconnect reconnect = new((_, _, _) => throw new IOException(), (_, _) => blocked.Task, TimeSpan.FromMilliseconds(30));
        WirelessException error = await Assert.ThrowsAsync<WirelessException>(() => reconnect.Resolve(settings, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("wifi_setup_required", error.Code); Assert.Equal(before, File.ReadAllText(settings.ProfilePath));
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("bluetooth")]
    [InlineData("offline")]
    [InlineData("cancel")]
    [InlineData("cancel_system")]
    public async Task Recovery_failures_leave_home_usable_and_user_stop_is_distinct(string failure)
    {
        DesktopSettings settings = Pairing(); LocalState state = new(Path.Combine(root, "desktop")); state.Save("settings.json", settings);
        using CancellationTokenSource stop = new();
        WirelessReconnect reconnect = new((host, _, _) => host == "192.0.2.2" ? throw new IOException() : Task.FromResult("192.0.2.5 ssh-ed25519 AAAA"),
            (_, _) =>
            {
                if (failure == "bluetooth") throw new System.Runtime.InteropServices.COMException("private-driver-details");
                if (failure == "cancel_system") { stop.Cancel(); throw new System.Runtime.InteropServices.COMException("private-driver-details"); }
                if (failure == "cancel") { stop.Cancel(); throw new OperationCanceledException(stop.Token); }
                return Task.FromResult<string?>(failure == "offline" ? null : "192.0.2.5");
            });
        using ShellViewModel model = new(state, reconnect);
        Assert.Null(await Record.ExceptionAsync(() => model.Connect(stop.Token)));
        Assert.False(model.Busy); Assert.False(model.IsConnecting); Assert.False(model.Connected);
        Assert.Equal(failure.StartsWith("cancel", StringComparison.Ordinal), model.ConnectionCanceled);
        Assert.DoesNotContain("private-driver-details", model.Status);
        Assert.Equal("192.0.2.2", BoardConnectionProfile.Load(settings.ProfilePath).BoxHost);
    }
    [LiveOfflineWirelessFact]
    public async Task Real_box_on_unreachable_old_wifi_returns_setup_required_without_changing_pairing()
    {
        BoardConnectionProfile original = BoardConnectionProfile.Load(Environment.GetEnvironmentVariable("CLAWBOX_OFFLINE_PROFILE")!);
        Directory.CreateDirectory(root);
        string known = File.ReadAllText(original.KnownHostsFile);
        original.KnownHostsFile = Path.Combine(root, "known_hosts"); File.WriteAllText(original.KnownHostsFile, known);
        string path = Path.Combine(root, "connection.json");
        File.WriteAllText(path, JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        string before = File.ReadAllText(path);
        DesktopSettings settings = new() { ProfilePath = path, ConnectionMode = "wireless" };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        WirelessException error = await Assert.ThrowsAsync<WirelessException>(() => new WirelessReconnect().Resolve(settings, deadline.Token));
        Assert.Equal("wifi_setup_required", error.Code); Assert.Equal(before, File.ReadAllText(path));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

public sealed class LiveOfflineWirelessFactAttribute : FactAttribute
{
    public LiveOfflineWirelessFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAWBOX_OFFLINE_PROFILE"))) Skip = "Opt-in test requires a paired board on an unreachable old Wi-Fi.";
    }
}
