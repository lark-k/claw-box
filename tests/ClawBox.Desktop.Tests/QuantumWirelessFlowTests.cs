using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class QuantumWirelessFlowTests
{
    [Fact]
    public void Selecting_a_different_box_uses_its_identity_and_open_enrollment()
    {
        var result = QuantumBoxPairing.SelectIdentity("new-device", "old-device", false, true);
        Assert.Equal("new-device", result.Identity);
        Assert.True(result.Open);
        Assert.False(QuantumBoxPairing.SelectIdentity("old-device", "old-device", false, true).Open);
        Assert.True(QuantumBoxPairing.SelectIdentity("new-device", null, false, true).Open);
    }

    [Fact]
    public void Automatic_reconnect_does_not_switch_to_another_box()
    {
        var error = Assert.Throws<WirelessException>(() => QuantumBoxPairing.SelectIdentity("different", "original", false, false));
        Assert.Equal("identity_mismatch", error.Code);
        Assert.Throws<WirelessException>(() => QuantumBoxPairing.SelectIdentity(null, "original", false, true));
    }

    [Fact]
    public async Task Adding_a_computer_to_the_same_wifi_does_not_disconnect_or_reconfigure_the_box()
    {
        WirelessStatus current = new(true, ["192.0.2.2"], "Network A", "YQ==");
        int scans = 0, writes = 0;
        WirelessStatus result = await QuantumBoxPairing.EnsureWifi(new("Network A", "password", "wpa-psk", "YQ=="),
            _ => Task.FromResult(current),
            _ => { scans++; return Task.FromResult(Array.Empty<WirelessNetwork>()); },
            (_, _, _) => { writes++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Same(current, result);
        Assert.Equal(0, scans);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task A_new_wifi_is_configured_once_and_returns_the_new_address()
    {
        WirelessStatus first = new(true, ["192.0.2.2"], "Network A", "YQ==");
        WirelessStatus second = new(true, ["198.51.100.7"], "Network B", "Yg==");
        WirelessNetwork b = new("Network B", "Yg==", 80, true);
        int reads = 0, writes = 0;
        WirelessStatus result = await QuantumBoxPairing.EnsureWifi(new("Network B", "password", "wpa-psk", "Yg=="),
            _ => Task.FromResult(++reads == 1 ? first : second),
            _ => Task.FromResult(new[] { b }),
            (network, _, _) => { Assert.Same(b, network); writes++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Equal(1, writes);
        Assert.Equal("198.51.100.7", Assert.Single(result.Addresses));
    }

    [Fact]
    public async Task A_missing_network_does_not_write_configuration()
    {
        int writes = 0;
        await Assert.ThrowsAsync<CurrentWifiException>(() => QuantumBoxPairing.EnsureWifi(new("B", "", "open", "Yg=="),
            _ => Task.FromResult(new WirelessStatus(true, ["192.0.2.2"], "A", "YQ==")),
            _ => Task.FromResult(Array.Empty<WirelessNetwork>()),
            (_, _, _) => { writes++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Failed_wifi_configuration_is_not_replayed()
    {
        int writes = 0;
        await Assert.ThrowsAsync<WirelessException>(() => QuantumBoxPairing.EnsureWifi(new("B", "password", "wpa-psk", "Yg=="),
            _ => Task.FromResult(new WirelessStatus(true, ["192.0.2.2"], "A", "YQ==")),
            _ => Task.FromResult(new[] { new WirelessNetwork("B", "Yg==", 70, true) }),
            (_, _, _) => { writes++; throw new WirelessException("wifi_connection_timeout"); }, CancellationToken.None));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task Wireless_network_failures_retry_connection_without_repeating_configuration()
    {
        int attempts = 0, waits = 0;
        string result = await QuantumBoxConnection.RetryWirelessConnection<string>(_ =>
        {
            if (++attempts < 3) throw new RuntimeStartupException(StartupFailureKind.Network, "fixture");
            return Task.FromResult("connected");
        }, CancellationToken.None, (_, _) => { waits++; return Task.CompletedTask; });
        Assert.Equal("connected", result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
    }

    [Theory]
    [InlineData(StartupFailureKind.Authentication)]
    [InlineData(StartupFailureKind.HostKey)]
    public async Task Identity_and_authentication_errors_are_not_retried(StartupFailureKind kind)
    {
        int attempts = 0;
        await Assert.ThrowsAsync<RuntimeStartupException>(() => QuantumBoxConnection.RetryWirelessConnection<string>(_ =>
        {
            attempts++;
            throw new RuntimeStartupException(kind, "fixture");
        }, CancellationToken.None, (_, _) => throw new InvalidOperationException("must not wait")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Persistent_network_failure_stops_after_a_bounded_number_of_attempts()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<RuntimeStartupException>(() => QuantumBoxConnection.RetryWirelessConnection<string>(_ =>
        {
            attempts++;
            throw new RuntimeStartupException(StartupFailureKind.Network, "fixture");
        }, CancellationToken.None, (_, _) => Task.CompletedTask));
        Assert.Equal(4, attempts);
    }
}
