using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawBox.Desktop.Services;

/// <summary>Credentials returned over an authenticated BLE session; never log or expose the token in UI.</summary>
public sealed class QuantumBoxPairingResult(string fingerprint, string hostKey, string[] addresses, string model, string gatewayToken, int? contextWindow)
{
    public string Fingerprint { get; } = fingerprint;
    public string HostKey { get; } = hostKey;
    public string[] Addresses { get; } = addresses;
    public string Model { get; } = model;
    public int? ContextWindow { get; } = contextWindow;
    [JsonIgnore] public string GatewayToken { get; } = gatewayToken;
}

public sealed class QuantumWifiRequest(string ssid, string password, string security, string? ssidBytes = null)
{
    public string Ssid { get; } = ssid;
    [JsonIgnore] public string Password { get; } = password;
    public string Security { get; } = security;
    public string? SsidBytes { get; } = ssidBytes;
}

/// <summary>Pair one computer without replacing other computers' authorizations.</summary>
public static class QuantumBoxPairing
{
    public static async Task<QuantumBoxPairingResult> PairWireless(
        ulong address, string identityFile, string? expectedIdentity, byte[]? joinSecret, CancellationToken cancellation, QuantumWifiRequest? wifi = null)
    {
        BluetoothNetwork link = await BluetoothNetwork.Connect(address, cancellation);
        try
        {
            JsonElement info = await link.Info(cancellation);
            string? observed = info.GetProperty("identity").GetString();
            if (expectedIdentity is not null && observed != expectedIdentity) throw new WirelessException("identity_mismatch");
            bool open = expectedIdentity is null && joinSecret is null;
            using WirelessSession session = new(link, new WirelessProtocol(identityFile, expectedIdentity, false, joinSecret, open));
            await session.Authenticate(cancellation);
            if (wifi is not null)
            {
                WirelessNetwork[] networks = await session.Scan(cancellation);
                WirelessNetwork? match = networks.FirstOrDefault(network => network.Bytes == wifi.SsidBytes);
                if (match is null) throw new CurrentWifiException("盒子未发现电脑当前的 Wi-Fi，请检查信号和频段。");
                await session.Send(new { op = "configure", ssid = match.Ssid, ssid_bytes = match.Bytes, password = wifi.Password, security = wifi.Security, hidden = false }, cancellation);
            }
            WirelessStatus status = await session.Status(cancellation);
            JsonElement model = await session.Send(new { op = "model_setup" }, cancellation);
            string? name = model.GetProperty("model").GetString();
            string? token = model.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Length > 128 ||
                model.GetProperty("port").GetInt32() != 18792) throw new WirelessException("model_setup_failed");
            int? context = model.TryGetProperty("contextWindow", out JsonElement size) ? size.GetInt32() : null;
            if (context is not null && (context < 1 || context > 10485760)) throw new WirelessException("model_setup_failed");
            return new(session.Identity, session.HostKey, status.Addresses, name, token, context);
        }
        catch { link.Dispose(); throw; }
    }
}
