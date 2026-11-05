using System.Text.Json;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class QuantumBoxConnectionTests
{
    [Fact]
    public async Task Empty_profile_is_disconnected_and_rejects_missing_password()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Quantum.Box.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using QuantumBoxConnection box = new(directory);
            Assert.False(box.GetStatus().Connected);
            Assert.False((await box.Reconnect(CancellationToken.None)).Connected);
            await Assert.ThrowsAsync<ArgumentException>(() => box.PairWired("192.0.2.1", 22, "", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "profile.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Status_does_not_serialize_computer_credential()
    {
        QuantumBoxConnection.Status status = new(true, "192.0.2.1", "model", 1234, "secret-value");
        Assert.DoesNotContain("secret-value", JsonSerializer.Serialize(status));
    }
}

public sealed class LiveQuantumWiredFactAttribute : FactAttribute
{
    public LiveQuantumWiredFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QUANTUM_WIRED_TEST_ROOT"))) Skip = "Opt-in wired pairing test requires a temporary private directory."; }
}

public sealed class LiveQuantumWiredTests
{
    [LiveQuantumWiredFact]
    public async Task Wired_admin_pair_connect_disconnect_and_reconnect()
    {
        string directory = Environment.GetEnvironmentVariable("QUANTUM_WIRED_TEST_ROOT")!;
        string host = Environment.GetEnvironmentVariable("QUANTUM_WIRED_TEST_HOST")!;
        string password = Environment.GetEnvironmentVariable("QUANTUM_WIRED_TEST_PASSWORD")!;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
        using QuantumBoxConnection box = new(directory);
        QuantumBoxConnection.Status paired = await box.PairWired(host, 22, password, deadline.Token);
        Assert.True(paired.Connected);
        Assert.NotNull(paired.LocalPort);
        Assert.NotNull(paired.Model);
        box.Disconnect();
        Assert.False(box.GetStatus().Connected);
        Assert.True((await box.Reconnect(deadline.Token)).Connected);
    }
}

public sealed class LiveQuantumOpenFactAttribute : FactAttribute
{
    public LiveQuantumOpenFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QUANTUM_OPEN_TEST_ROOT"))) Skip = "Opt-in BLE enrollment test requires a disposable private directory."; }
}

[Collection("Bluetooth")]
public sealed class LiveQuantumOpenTests
{
    [LiveQuantumOpenFact]
    public async Task New_identity_enrolls_over_ble_and_reaches_model_tunnel()
    {
        string directory = Environment.GetEnvironmentVariable("QUANTUM_OPEN_TEST_ROOT")!;
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
        NearbyBoard[] devices = await BluetoothNetwork.Discover(deadline.Token);
        Assert.NotEmpty(devices);
        using QuantumBoxConnection box = new(directory);
        QuantumBoxConnection.Status status = await box.PairWireless(devices[0].Address, "192.168.137.2", 22, null, null, deadline.Token);
        Assert.True(status.Connected);
        Assert.NotNull(status.Model);
        Assert.NotNull(status.LocalPort);
    }
}
