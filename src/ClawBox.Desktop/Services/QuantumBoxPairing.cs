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
        ulong address, string identityFile, string? expectedIdentity, byte[]? joinSecret, CancellationToken cancellation, QuantumWifiRequest? wifi = null, bool selectedByUser = false)
    {
        BluetoothNetwork link = await BluetoothNetwork.Connect(address, cancellation);
        try
        {
            JsonElement info = await link.Info(cancellation);
            string? observed = info.GetProperty("identity").GetString();
            (string selectedIdentity, bool open) = SelectIdentity(observed, expectedIdentity, joinSecret is not null, selectedByUser);
            using WirelessSession session = new(link, new WirelessProtocol(identityFile, selectedIdentity, false, joinSecret, open));
            await session.Authenticate(cancellation);
            WirelessStatus status = await EnsureWifi(wifi, session.Status, session.Scan, async (network, request, token) =>
            {
                await session.Send(new { op = "configure", ssid = network.Ssid, ssid_bytes = network.Bytes, password = request.Password, security = request.Security, hidden = false }, token);
            }, cancellation);
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

    internal static (string Identity, bool Open) SelectIdentity(string? observed, string? expected, bool hasJoinSecret, bool selectedByUser)
    {
        if (string.IsNullOrWhiteSpace(observed)) throw new WirelessException("identity_mismatch");
        bool changed = expected is not null && observed != expected;
        if (changed && !selectedByUser) throw new WirelessException("identity_mismatch");
        return (observed, !hasJoinSecret && (expected is null || changed));
    }

    // A matching live network must not be disconnected merely to add another computer.
    internal static async Task<WirelessStatus> EnsureWifi(QuantumWifiRequest? wifi,
        Func<CancellationToken, Task<WirelessStatus>> readStatus,
        Func<CancellationToken, Task<WirelessNetwork[]>> scan,
        Func<WirelessNetwork, QuantumWifiRequest, CancellationToken, Task> configure,
        CancellationToken cancellation)
    {
        WirelessStatus current = await readStatus(cancellation);
        if (wifi is null || current.Connected && wifi.SsidBytes is not null && current.SsidBytes == wifi.SsidBytes)
            return current;
        WirelessNetwork[] networks = await scan(cancellation);
        WirelessNetwork? match = networks.FirstOrDefault(network => network.Bytes == wifi.SsidBytes);
        if (match is null) throw new CurrentWifiException("盒子未发现电脑当前的 Wi-Fi，请检查信号和频段。");
        await configure(match, wifi, cancellation);
        return await readStatus(cancellation);
    }
}
