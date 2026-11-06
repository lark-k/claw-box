using System.Text.Json;
using ClawBox.Desktop.Services;

// The parent application owns stdin and stdout. This process never listens on a socket.
using QuantumBoxConnection box = new();
while (await Console.In.ReadLineAsync() is string line)
{
    if (line.Length > 32768) break;
    string? id = null;
    try
    {
        using JsonDocument message = JsonDocument.Parse(line);
        JsonElement request = message.RootElement;
        id = request.GetProperty("id").GetString();
        if (id is null || id.Length is < 1 or > 64) throw new ArgumentException();
        string op = request.GetProperty("op").GetString() ?? "";
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        object result = op switch
        {
            "status" => Describe(box.GetStatus()),
            "reconnect" => Describe(await box.Reconnect(timeout.Token)),
            "disconnect" => Disconnect(box),
            "discover" => (await BluetoothNetwork.Discover(timeout.Token)).Select(d => new { address = d.Address.ToString(), name = d.Name, signal = d.Signal }).ToArray(),
            "current-wifi" => Wifi(),
            "pair-wired" => Describe(await box.PairWired(Text(request, "host"), Port(request), Text(request, "password"), timeout.Token)),
            "pair-wireless" => Describe(await PairWireless(box, request, timeout.Token)),
            _ => throw new ArgumentException("不支持该连接操作。"),
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, result }));
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        string message = error switch
        {
            WirelessException wireless => wireless.Message,
            SetupException setup => setup.Message,
            RuntimeStartupException startup => startup.Message,
            ArgumentException => "连接信息无效，请检查后重试。",
            CurrentWifiException wifi => wifi.Message,
            OperationCanceledException => "连接超时，请检查盒子和网络。",
            _ => "连接未完成，请检查盒子、密码和网络后重试。",
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = false, error = message }));
    }
}

static object Describe(QuantumBoxConnection.Status status) => new
{
    connected = status.Connected,
    host = status.Host,
    model = status.Model,
    localPort = status.LocalPort,
    contextWindow = status.ContextWindow,
    connectionMode = status.ConnectionMode,
    // This token is for the current computer only and travels solely over the parent pipe.
    token = status.Token,
};

static object Disconnect(QuantumBoxConnection box) { box.Disconnect(); return Describe(box.GetStatus()); }
static object Wifi() { var wifi = CurrentWifi.Read(); return new { name = wifi.Name }; }
static string Text(JsonElement request, string name) => request.GetProperty(name).GetString() ?? "";
static int Port(JsonElement request) => request.TryGetProperty("port", out JsonElement value) ? value.GetInt32() : 22;
static async Task<QuantumBoxConnection.Status> PairWireless(QuantumBoxConnection box, JsonElement request, CancellationToken cancellation)
{
    if (!ulong.TryParse(Text(request, "address"), out ulong address) || address > 0xffffffffffff) throw new ArgumentException();
    string code = request.TryGetProperty("pairingCode", out JsonElement item) ? item.GetString()?.Trim() ?? "" : "";
    byte[]? secret = code.Length == 0 ? null : Convert.FromHexString(code);
    if (secret is not null && secret.Length != 32) throw new ArgumentException();
    string? host = request.TryGetProperty("host", out JsonElement addressValue) ? addressValue.GetString() : null;
    QuantumWifiRequest? wifi = null;
    if (request.TryGetProperty("wifi", out JsonElement network) && network.ValueKind == JsonValueKind.Object)
    {
        var current = CurrentWifi.Read();
        string password = Text(network, "password");
        wifi = new(current.Name, password, password.Length == 0 ? "open" : "wpa-psk", current.Bytes);
    }
    try { return await box.PairWireless(address, host, Port(request), secret, wifi, cancellation); }
    finally { if (secret is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
}
