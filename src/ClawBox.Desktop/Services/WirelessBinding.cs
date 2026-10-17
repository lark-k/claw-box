using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class WirelessBinding(DesktopSettings settings, string? dataRoot = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string Root => dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox");
    public string? ExpectedIdentity => new BoardIdentity().Saved("pending", 22, settings.ProfilePath)?.Fingerprint;
    public bool Complete => File.Exists(settings.ProfilePath) && File.Exists(settings.GatewayTokenFile);
    public async Task<string> Prepare(CancellationToken cancellation)
    {
        string directory = Path.GetDirectoryName(settings.ProfilePath)!;
        BoardSetup.MakePrivate(directory);
        foreach (string name in new[] { settings.ProfilePath, Path.Combine(directory, "known_hosts"),
            Path.Combine(directory, "tunnel_ed25519"), Path.Combine(directory, "tunnel_ed25519.pub"), Path.Combine(directory, "gateway.secrets.json") }) BoardSetup.RejectLinkedFile(name);
        string identity = File.Exists(settings.ProfilePath) ? BoardConnectionProfile.Load(settings.ProfilePath).IdentityFile : Path.Combine(directory, "tunnel_ed25519");
        BoardSetup.RejectLinkedFile(identity);
        await BoardSetup.GenerateKey(identity, cancellation);
        return identity;
    }
    public void Pin(string hostKey)
    {
        string fingerprint = Fingerprint(hostKey);
        if (ExpectedIdentity is string existing && existing != fingerprint) throw new WirelessException("identity_mismatch");
        string known = Path.Combine(Path.GetDirectoryName(settings.ProfilePath)!, "known_hosts");
        if (!File.Exists(known))
        {
            using FileStream stream = new(known, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(stream); writer.WriteLine("clawbox-pending " + hostKey);
        }
    }
    public string Worker() => new BoardSetup(Root).EnsureCredentials();
    public async Task Save(string host, string hostKey, string? gatewayToken, CancellationToken cancellation)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) || host == "0.0.0.0") throw new WirelessException("invalid_address");
        string fingerprint = Fingerprint(hostKey);
        if (fingerprint != ExpectedIdentity) throw new WirelessException("identity_mismatch");
        if (File.Exists(settings.ProfilePath))
        {
            await new BoardIdentity().UpdateAddress(settings.ProfilePath, host, 22, cancellation);
            if (gatewayToken is not null)
            {
                string exported = JsonSerializer.Serialize(new { token = gatewayToken });
                BoardSetup.ValidateGatewaySecret(exported); BoardSetup.RejectLinkedFile(settings.GatewayTokenFile);
                BoardSetup.MakePrivate(Path.GetDirectoryName(settings.GatewayTokenFile)!);
                BoardSetup.AtomicWrite(settings.GatewayTokenFile, exported);
            }
            return;
        }
        SetupRequest target = new(host, 22, fingerprint);
        string known = await SshBoardSetupTransport.ObserveHostKey(host, 22, cancellation);
        BoardSetup.ValidateKnownHost(target, known);
        string secret = JsonSerializer.Serialize(new { token = gatewayToken });
        BoardSetup.ValidateGatewaySecret(secret);
        string directory = Path.GetDirectoryName(settings.ProfilePath)!;
        string identity = Path.Combine(directory, "tunnel_ed25519"), knownPath = Path.Combine(directory, "known_hosts");
        string gateway = Path.Combine(directory, "gateway.secrets.json");
        foreach (string file in new[] { settings.ProfilePath, identity, knownPath, gateway }) BoardSetup.RejectLinkedFile(file);
        BoardSetup.AtomicWrite(knownPath, known.Trim() + "\n");
        BoardSetup.AtomicWrite(gateway, secret);
        BoardConnectionProfile profile = new()
        {
            Version = 1,
            BoxHost = host,
            SshUser = "root",
            SshPort = 22,
            ExecutorPort = 18790,
            RemotePort = 18791,
            GatewayLocalPort = 18889,
            KnownHostsFile = knownPath,
            IdentityFile = identity,
            HostKeyFingerprint = fingerprint
        };
        profile.Validate();
        BoardSetup.AtomicWrite(settings.ProfilePath, JsonSerializer.Serialize(profile, Json));
        settings.GatewayTokenFile = gateway;
    }
    public static string Fingerprint(string hostKey)
    {
        string[] parts = hostKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "ssh-ed25519") throw new WirelessException("identity_mismatch");
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[1]))).TrimEnd('=');
    }
}

public sealed class ConnectionSnapshot
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    public ConnectionSnapshot(DesktopSettings settings)
    {
        if (!File.Exists(settings.ProfilePath)) return;
        BoardConnectionProfile profile = BoardConnectionProfile.Load(settings.ProfilePath);
        foreach (string file in new[] { settings.ProfilePath, profile.KnownHostsFile })
        {
            BoardSetup.RejectLinkedFile(file);
            files.Add(file, File.ReadAllBytes(file));
        }
    }
    public void Restore()
    {
        foreach ((string path, byte[] value) in files)
        {
            BoardSetup.RejectLinkedFile(path);
            string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(stage, value); File.Move(stage, path, true); }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
    }
}
