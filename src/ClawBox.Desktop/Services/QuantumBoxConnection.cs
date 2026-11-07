using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace ClawBox.Desktop.Services;

/// <summary>Private pairing state and a single live Quantum model tunnel.</summary>
public sealed class QuantumBoxConnection : IDisposable
{
    private sealed class Profile
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Fingerprint { get; set; } = "";
        public string Model { get; set; } = "";
        public string Token { get; set; } = "";
        public int? ContextWindow { get; set; }
    }
    public sealed class Status(bool connected, string? host, string? model, uint? localPort, string? token, int? contextWindow = null, string? connectionMode = null)
    {
        public bool Connected { get; } = connected;
        public string? Host { get; } = host;
        public string? Model { get; } = model;
        public uint? LocalPort { get; } = localPort;
        public int? ContextWindow { get; } = contextWindow;
        public string? ConnectionMode { get; } = connectionMode;
        [JsonIgnore] public string? Token { get; } = token;
    }
    private readonly string root, identity, profilePath;
    private QuantumModelTunnel? tunnel;
    private Profile? profile;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public QuantumBoxConnection(string? directory = null)
    {
        root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuantumHarnessNext", "box");
        BoardSetup.MakePrivate(root);
        identity = Path.Combine(root, "tunnel_ed25519");
        profilePath = Path.Combine(root, "profile.json");
        foreach (string path in new[] { identity, identity + ".pub", profilePath }) BoardSetup.RejectLinkedFile(path);
        if (File.Exists(profilePath))
        {
            try { profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(profilePath), Json) ?? throw new JsonException(); Validate(profile); }
            catch (Exception error) when (error is JsonException or SetupException or ArgumentException)
            { throw new InvalidOperationException("盒子配对记录损坏，已保留原文件。"); }
        }
    }

    public Status GetStatus()
    {
        bool ssh = tunnel?.Alive == true;
        return new(ssh, profile?.Host, profile?.Model, ssh ? tunnel!.LocalPort : null,
            ssh ? profile?.Token : null, profile?.ContextWindow, ssh ? "wifi" : null);
    }

    // Discovery is performed through the authenticated SSH tunnel; no server credential reaches Windows.
    public async Task<string[]> ListModels(CancellationToken cancellation)
    {
        QuantumModelTunnel current = tunnel ?? throw new InvalidOperationException("盒子未连接。");
        Profile active = profile ?? throw new InvalidOperationException("盒子未连接。");
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", active.Token);
        using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{current.LocalPort}/v1/models", cancellation);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
        if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("盒子模型列表过大。");
        using JsonDocument data = JsonDocument.Parse(bytes);
        return data.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 290 && !id.Any(char.IsControl))
            .Select(id => id!).Distinct(StringComparer.Ordinal).Take(64).ToArray();
    }

    public async Task<Status> PairWireless(ulong address, string? host, int port, byte[]? joinSecret,
        QuantumWifiRequest? wifi, CancellationToken cancellation, bool selectedByUser = true)
    {
        await BoardSetup.GenerateKey(identity, cancellation);
        QuantumBoxPairingResult paired = await QuantumBoxPairing.PairWireless(address, identity, profile?.Fingerprint, joinSecret, cancellation, wifi, selectedByUser);
        string target = string.IsNullOrWhiteSpace(host) ? paired.Addresses.FirstOrDefault(a => System.Net.IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? "" : host.Trim();
        Profile next = new() { Host = target, Port = port, Fingerprint = paired.Fingerprint, Model = paired.Model, Token = paired.GatewayToken, ContextWindow = paired.ContextWindow };
        return await RetryWirelessConnection(token => Activate(next, token), cancellation);
    }

    // Retry only connection establishment, not BLE configuration or model requests.
    internal static async Task<T> RetryWirelessConnection<T>(Func<CancellationToken, Task<T>> connect,
        CancellationToken cancellation, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return await connect(cancellation); }
            catch (RuntimeStartupException error) when (error.Kind == StartupFailureKind.Network && attempt < 3)
            {
                await delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellation);
            }
        }
    }

    public async Task<Status> PairWired(string host, int port, string password, CancellationToken cancellation)
    {
        SetupRequest.ValidateAddress(host, port);
        if (string.IsNullOrEmpty(password) || password.Length > 512) throw new ArgumentException("请输入盒子管理员密码。");
        await BoardSetup.GenerateKey(identity, cancellation);
        string observed = await SshBoardSetupTransport.ObserveHostKey(host, port, cancellation);
        string[] parts = observed.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new InvalidOperationException("无法确认盒子身份。");
        string fingerprint = WirelessBinding.Fingerprint(parts[1] + " " + parts[2]);
        string[] publicParts = File.ReadAllText(identity + ".pub").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (publicParts.Length < 2 || publicParts[0] != "ssh-ed25519" || !Regex.IsMatch(publicParts[1], @"^[A-Za-z0-9+/=]+$"))
            throw new InvalidOperationException("本机配对公钥无效。");
        string publicKey = publicParts[0] + " " + publicParts[1];
        const string source = "import base64,json,sys;sys.path.insert(0,'/opt/clawbox-network');from clawbox_network.protocol import Owners;from clawbox_network.model import ModelBootstrap;k=base64.b64decode(sys.argv[1]).decode('ascii');Owners().join(k);print(json.dumps(ModelBootstrap().setup(k)))";
        string code = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        string encodedKey = Convert.ToBase64String(Encoding.ASCII.GetBytes(publicKey));
        string command = "python3 -c \"import base64;exec(base64.b64decode('" + code + "'))\" " + encodedKey;
        ConnectionInfo info = new(host, port, "root", new PasswordAuthenticationMethod("root", password)) { Timeout = TimeSpan.FromSeconds(10) };
        foreach (string algorithm in info.HostKeyAlgorithms.Keys.Where(a => a != "ssh-ed25519").ToArray()) info.HostKeyAlgorithms.Remove(algorithm);
        using SshClient client = new(info);
        client.HostKeyReceived += (_, args) => args.CanTrust = args.HostKeyName == "ssh-ed25519" &&
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=') == fingerprint;
        using CancellationTokenRegistration cancel = cancellation.Register(client.Dispose);
        await client.ConnectAsync(cancellation);
        using SshCommand shell = client.CreateCommand(command);
        string result = await Task.Run(shell.Execute, cancellation);
        if (shell.ExitStatus != 0) throw new InvalidOperationException("盒子未能完成模型授权，请检查盒子服务。");
        using JsonDocument response = JsonDocument.Parse(result);
        JsonElement value = response.RootElement;
        if (value.GetProperty("port").GetInt32() != 18792) throw new InvalidOperationException("盒子模型端口无效。");
        Profile next = new()
        {
            Host = host,
            Port = port,
            Fingerprint = fingerprint,
            Model = value.GetProperty("model").GetString() ?? "",
            Token = value.GetProperty("token").GetString() ?? "",
            ContextWindow = value.TryGetProperty("contextWindow", out JsonElement size) ? size.GetInt32() : null
        };
        return await Activate(next, cancellation);
    }

    public async Task<Status> Reconnect(CancellationToken cancellation)
    {
        if (profile is null) return GetStatus();
        try { return await Activate(profile, cancellation); }
        catch (RuntimeStartupException error) when (error.Kind == StartupFailureKind.Network)
        {
            NearbyBoard[] boards;
            try { boards = await BluetoothNetwork.Discover(cancellation); }
            catch (WirelessException) { throw new RuntimeStartupException(error.Kind, error.Message); }
            foreach (NearbyBoard board in boards)
            {
                try { return await PairWireless(board.Address, null, profile.Port, null, null, cancellation, selectedByUser: false); }
                catch (WirelessException mismatch) when (mismatch.Code == "identity_mismatch") { }
            }
            throw;
        }
    }

    private async Task<Status> Activate(Profile next, CancellationToken cancellation)
    {
        Validate(next);
        QuantumModelTunnel candidate = new(next.Host, next.Port, next.Fingerprint, identity);
        try
        {
            await candidate.Connect(cancellation);
            using HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.Token);
            using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{candidate.LocalPort}/v1/models", cancellation);
            response.EnsureSuccessStatusCode();
            BoardSetup.RejectLinkedFile(profilePath);
            BoardSetup.AtomicWrite(profilePath, JsonSerializer.Serialize(next, Json));
            QuantumModelTunnel? previous = tunnel;
            tunnel = candidate;
            profile = next;
            previous?.Dispose();
            return GetStatus();
        }
        catch { candidate.Dispose(); throw; }
    }

    private static void Validate(Profile value)
    {
        new SetupRequest(value.Host, value.Port, value.Fingerprint).Validate();
        if (string.IsNullOrWhiteSpace(value.Model) || value.Model.Length > 256 || string.IsNullOrWhiteSpace(value.Token) ||
            value.Token.Length < 32 || value.Token.Length > 128 || value.Token.Any(char.IsWhiteSpace) ||
            value.ContextWindow is < 1 or > 10485760) throw new ArgumentException("盒子模型配置无效。");
    }

    public void Disconnect() { tunnel?.Dispose(); tunnel = null; }
    public void Dispose() => Disconnect();
}
