using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class WirelessTests
{
    [Fact]
    public void Discovery_merges_both_event_orders_but_requires_service_uuid()
    {
        NearbyBoardDiscovery discovery = new();
        discovery.Observe(1, "ClawBox-first", -65, false);
        Assert.Empty(discovery.Results());
        discovery.Observe(1, null, -67, true);
        discovery.Observe(2, null, -70, true);
        discovery.Observe(2, "ClawBox-second", -69, false);
        discovery.Observe(3, "ClawBox-unverified", -55, false);
        NearbyBoard[] result = discovery.Results();
        Assert.Equal(2, result.Length);
        Assert.Contains(result, x => x.Address == 1 && x.Name == "ClawBox-first");
        Assert.Contains(result, x => x.Address == 2 && x.Name == "ClawBox-second");
    }
    [Fact]
    public void Canonical_json_matches_python_ascii_and_base64_rules()
    {
        byte[] value = WirelessProtocol.Canonical(WirelessProtocol.Element(new { z = "+/测试", a = 1 }));
        Assert.Equal("{\"a\":1,\"z\":\"+/\\u6d4b\\u8bd5\"}", Encoding.ASCII.GetString(value));
    }
    [Fact]
    public async Task Native_client_interoperates_with_real_python_protocol_and_rejects_replay()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src/ClawBox.Board.Network"))) root = Directory.GetParent(root)?.FullName ?? throw new IOException("Repository not found");
        string temporary = Path.Combine(Path.GetTempPath(), "ClawBox.Wireless.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        string key = Path.Combine(temporary, "tunnel_ed25519");
        string secondKey = Path.Combine(temporary, "second_ed25519");
        try
        {
            ProcessStartInfo generate = new(BoardSetup.OpenSshTool("ssh-keygen.exe")!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "clawbox-reverse-tunnel", "-f", key }) generate.ArgumentList.Add(argument);
            using (Process created = Process.Start(generate)!) { await created.WaitForExitAsync(); Assert.Equal(0, created.ExitCode); }
            ProcessStartInfo generateSecond = new(BoardSetup.OpenSshTool("ssh-keygen.exe")!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "clawbox-reverse-tunnel", "-f", secondKey }) generateSecond.ArgumentList.Add(argument);
            using (Process created = Process.Start(generateSecond)!) { await created.WaitForExitAsync(); Assert.Equal(0, created.ExitCode); }
            ProcessStartInfo start = new("python") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["PYTHONPATH"] = Path.Combine(root, "src/ClawBox.Board.Network");
            start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
import json,sys
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from clawbox_network.protocol import Service,ProtocolError
class Owners:
 def __init__(self): self.values=set()
 def keys(self): return self.values
 def enrollment_open(self): return not self.values
 def claim(self,key): self.values.add(key)
 def join_secret(self): return bytes(range(32))
 def join(self,key): self.values.add(key)
class Network:
 def status(self): return {'connected':True,'addresses':['192.0.2.2']}
server=Service(Ed25519PrivateKey.generate(),Owners(),Network())
for line in sys.stdin:
 try: print(json.dumps(server.exchange('fixture',json.loads(line))),flush=True)
 except ProtocolError as e: print(json.dumps({'error':e.code}),flush=True)
""");
            using Process process = Process.Start(start)!;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            async Task<JsonElement> Exchange(JsonElement value)
            {
                await process.StandardInput.WriteLineAsync(Encoding.ASCII.GetString(WirelessProtocol.Canonical(value)));
                string line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException(await process.StandardError.ReadToEndAsync());
                using JsonDocument document = JsonDocument.Parse(line); return document.RootElement.Clone();
            }
            using WirelessProtocol client = new(key, null, true);
            JsonElement auth = client.Accept(await Exchange(client.Hello));
            Assert.True(client.Decrypt(await Exchange(auth)).GetProperty("authenticated").GetBoolean());
            JsonElement request = client.Encrypt(WirelessProtocol.Element(new { op = "status" }));
            Assert.True(client.Decrypt(await Exchange(request)).GetProperty("connected").GetBoolean());
            Assert.Equal("invalid_sequence", (await Exchange(request)).GetProperty("error").GetString());
            using (WirelessProtocol wrong = new(secondKey, null, false, new byte[32]))
            {
                JsonElement response = await Exchange(wrong.Hello);
                WirelessException error = Assert.Throws<WirelessException>(() => wrong.Accept(response));
                Assert.Equal("invalid_join_proof", error.Code);
            }
            using WirelessProtocol second = new(secondKey, null, false, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
            JsonElement secondAuth = second.Accept(await Exchange(second.Hello));
            Assert.True(second.Decrypt(await Exchange(secondAuth)).GetProperty("authenticated").GetBoolean());
            Assert.True(second.Decrypt(await Exchange(second.Encrypt(WirelessProtocol.Element(new { op = "status" })))).GetProperty("connected").GetBoolean());
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode);
        }
        finally { Directory.Delete(temporary, true); }
    }
}

public sealed class LiveWirelessFactAttribute : FactAttribute
{
    public LiveWirelessFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAWBOX_WIRELESS_PROFILE"))) Skip = "Opt-in real Bluetooth test requires existing owner pairing."; }
}

public sealed class LiveDiscoveryFactAttribute : FactAttribute
{
    public LiveDiscoveryFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QUANTUM_BOX_BLUETOOTH_ADDRESS"))) Skip = "Opt-in real Bluetooth discovery test requires a box address."; }
}

[Collection("Bluetooth")]
public sealed class LiveDiscoveryTests
{
    [LiveDiscoveryFact]
    public async Task Native_discovery_finds_the_real_board_in_three_consecutive_searches()
    {
        string input = Environment.GetEnvironmentVariable("QUANTUM_BOX_BLUETOOTH_ADDRESS")!.Replace(":", "", StringComparison.Ordinal);
        ulong address = Convert.ToUInt64(input, 16);
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
        for (int round = 0; round < 3; round++)
        {
            NearbyBoard[] boards = await BluetoothNetwork.Discover(deadline.Token);
            Assert.Contains(boards, board => board.Address == address);
        }
    }
}

[Collection("Bluetooth")]
public sealed class LiveWirelessTests
{
    [LiveWirelessFact]
    public async Task Windows_native_bluetooth_authenticates_existing_owner_and_reads_wifi()
    {
        BoardConnectionProfile profile = BoardConnectionProfile.Load(Environment.GetEnvironmentVariable("CLAWBOX_WIRELESS_PROFILE")!);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        NearbyBoard[] boards = await BluetoothNetwork.Discover(deadline.Token); Assert.NotEmpty(boards);
        foreach (NearbyBoard board in boards)
        {
            using BluetoothNetwork link = await BluetoothNetwork.Connect(board.Address, deadline.Token);
            JsonElement info = await link.Info(deadline.Token);
            if (info.GetProperty("identity").GetString() != profile.HostKeyFingerprint) continue;
            using WirelessSession session = new(link, new(profile.IdentityFile, profile.HostKeyFingerprint, false));
            await session.Authenticate(deadline.Token);
            WirelessStatus status = await session.Status(deadline.Token); Assert.True(status.Connected); Assert.NotEmpty(status.Addresses);
            WirelessNetwork[] networks = await session.Scan(deadline.Token); Assert.NotEmpty(networks);
            if (Environment.GetEnvironmentVariable("CLAWBOX_WIRELESS_TEST_SAVED") == "1")
            {
                WirelessNetwork remembered = networks.Single(n => n.Ssid == status.Ssid);
                Assert.True(remembered.Saved);
                JsonElement reused = await session.Send(new { op = "connect_saved", ssid = remembered.Ssid, ssid_bytes = remembered.Bytes, security = remembered.Secured ? "wpa-psk" : "open" }, deadline.Token);
                Assert.True(reused.GetProperty("connected").GetBoolean());
            }
            string? password = Environment.GetEnvironmentVariable("CLAWBOX_WIRELESS_TEST_PSK");
            if (password is not null)
            {
                WirelessNetwork network = networks.Single(n => n.Ssid == status.Ssid);
                JsonElement configured = await session.Send(new { op = "configure", ssid = network.Ssid, ssid_bytes = network.Bytes, password }, deadline.Token);
                Assert.True(configured.GetProperty("connected").GetBoolean());
                string tokens = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox/executor/tokens.secrets.json");
                using JsonDocument credentials = JsonDocument.Parse(File.ReadAllText(tokens));
                JsonElement result = await session.Send(new { op = "desktop_setup", worker_token = credentials.RootElement.GetProperty("workerToken").GetString() }, deadline.Token);
                Assert.True(result.GetProperty("gateway_token").GetString()!.Length >= 32);
            }
            return;
        }
        Assert.Fail("The previously paired board was not found.");
    }
}
