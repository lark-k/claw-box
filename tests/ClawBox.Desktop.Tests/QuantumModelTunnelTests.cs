using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class LiveQuantumTunnelFactAttribute : FactAttribute
{
    public LiveQuantumTunnelFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QUANTUM_TEST_IDENTITY"))) Skip = "Opt-in box model tunnel test requires a temporary paired identity."; }
}

public sealed class QuantumModelTunnelTests
{
    [LiveQuantumTunnelFact]
    public async Task Paired_identity_reaches_model_through_its_own_loopback_tunnel()
    {
        string host = Environment.GetEnvironmentVariable("QUANTUM_TEST_HOST")!;
        string fingerprint = Environment.GetEnvironmentVariable("QUANTUM_TEST_FINGERPRINT")!;
        string identity = Environment.GetEnvironmentVariable("QUANTUM_TEST_IDENTITY")!;
        string token = Environment.GetEnvironmentVariable("QUANTUM_TEST_GATEWAY_TOKEN")!;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        string? joinCode = Environment.GetEnvironmentVariable("QUANTUM_TEST_JOIN_SECRET");
        if (joinCode is not null)
        {
            ulong address = Convert.ToUInt64(Environment.GetEnvironmentVariable("QUANTUM_TEST_BLE_ADDRESS")!.Replace(":", "", StringComparison.Ordinal), 16);
            QuantumBoxPairingResult paired = await QuantumBoxPairing.PairWireless(address, identity, fingerprint, Convert.FromHexString(joinCode), deadline.Token);
            Assert.Equal(token, paired.GatewayToken);
            Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(paired));
        }
        using QuantumModelTunnel tunnel = new(host, 22, fingerprint, identity);
        await tunnel.Connect(deadline.Token);
        Assert.True(tunnel.Alive);
        using HttpClient http = new() { BaseAddress = new Uri($"http://127.0.0.1:{tunnel.LocalPort}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.GetAsync("v1/models", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
