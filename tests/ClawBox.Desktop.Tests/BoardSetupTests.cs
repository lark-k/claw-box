using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class BoardSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClawBox.Setup.Tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] HostKey = [0, 0, 0, 11, .. "ssh-ed25519"u8.ToArray(), 0, 0, 0, 32, .. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly string Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(HostKey)).TrimEnd('=');
    private static SetupRequest Request => new("127.0.0.1", 22, Fingerprint);
    private sealed class Updates : IProgress<SetupUpdate>
    {
        public List<SetupUpdate> Items { get; } = [];
        public void Report(SetupUpdate value) => Items.Add(value);
    }
    private sealed class Board : IBoardSetupTransport
    {
        public string[] Issues { get; set; } = [];
        public bool Installed { get; private set; }
        public string Worker { get; private set; } = "";
        public string Secret { get; set; } = JsonSerializer.Serialize(new { token = new string('g', 64) });
        public bool CancelInstallation { get; set; }
        public bool WrongKey { get; set; }
        public BoardNetwork? Network { get; set; }
        public Task<string> Connect(SetupRequest request, string password, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] key = WrongKey ? new byte[32] : HostKey;
            return Task.FromResult(request.Host + " ssh-ed25519 " + Convert.ToBase64String(key));
        }
        public Task<BoardReadiness> Inspect(CancellationToken cancellation) => Task.FromResult(new BoardReadiness(Issues) { Network = Network });
        public Task Configure(string publicKey, string workerToken, IProgress<SetupUpdate> progress, CancellationToken cancellation)
        {
            Installed = true; Worker = workerToken;
            if (CancelInstallation) throw new OperationCanceledException();
            return Task.CompletedTask;
        }
        public Task<string> ReadGatewaySecret(CancellationToken cancellation) => Task.FromResult(Secret);
        public void Dispose() { }
    }
    [Theory]
    [InlineData("bad host;exec", 22)]
    [InlineData("-option", 22)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public async Task Invalid_address_stops_before_authentication_or_disk_changes(string host, int port)
    {
        bool contacted = false;
        BoardSetup setup = new(root, () => { contacted = true; return new Board(); });
        Assert.Equal("network", (await Assert.ThrowsAsync<SetupException>(() => setup.Inspect(new(host, port, Fingerprint), "private password", new Updates(), CancellationToken.None))).Step);
        Assert.False(contacted); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Missing_identity_and_password_have_specific_guidance_before_contacting_board()
    {
        BoardSetup setup = new(root, () => throw new InvalidOperationException("Must not contact board."));
        Assert.Contains("交付卡", (await Assert.ThrowsAsync<SetupException>(() => setup.Inspect(Request with { Fingerprint = "" }, "password", new Updates(), CancellationToken.None))).Message);
        Assert.Contains("登录密码", (await Assert.ThrowsAsync<SetupException>(() => setup.Inspect(Request, "", new Updates(), CancellationToken.None))).Message);
    }
    [Fact]
    public async Task Wrong_host_key_never_installs_or_creates_local_state()
    {
        Board board = new() { WrongKey = true }; BoardSetup setup = new(root, () => board);
        Assert.Equal("identity", (await Assert.ThrowsAsync<SetupException>(() => setup.Provision(Request, "password", new Updates(), CancellationToken.None))).Step);
        Assert.False(board.Installed); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Multiple_missing_board_conditions_are_reported_without_mutating()
    {
        Board board = new() { Issues = ["model", "gateway", "forwarding"] }; BoardSetup setup = new(root, () => board);
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => setup.Provision(Request, "password", new Updates(), CancellationToken.None));
        Assert.Contains("默认模型", error.Message); Assert.Contains("认证网关", error.Message); Assert.Contains("AllowTcpForwarding", error.Message);
        Assert.False(board.Installed); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Read_only_inspection_does_not_provision_even_on_success()
    {
        Board board = new(); Updates updates = new();
        await new BoardSetup(root, () => board).Inspect(Request, "password", updates, CancellationToken.None);
        Assert.False(board.Installed); Assert.False(Directory.Exists(root));
        Assert.Contains(updates.Items, u => u.Step == "board" && u.Status == "ready");
    }
    [Fact]
    public async Task Board_without_model_network_guides_sharing_before_any_binding_mutation()
    {
        Board board = new() { Network = new("192.168.137.2", "eth0", "manual", ["192.168.137.2/24"], true, "192.168.137.1", "unreachable") };
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => new BoardSetup(root, () => board).Provision(Request, "password", new Updates(), CancellationToken.None));
        Assert.Equal("internet", error.Step); Assert.Contains("设置网络共享", error.Message);
        Assert.False(board.Installed); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Successful_pairing_sends_only_worker_and_preserves_existing_credentials()
    {
        Directory.CreateDirectory(Path.Combine(root, "executor"));
        string credentials = Path.Combine(root, "executor", "tokens.secrets.json");
        string original = JsonSerializer.Serialize(new { adminToken = new string('A', 64), workerToken = new string('B', 64) });
        File.WriteAllText(credentials, original);
        Board board = new(); Updates updates = new();
        SetupResult result = await new BoardSetup(root, () => board).Provision(Request, "never-persist-this-password", updates, CancellationToken.None);
        Assert.True(board.Installed); Assert.Equal(new string('B', 64), board.Worker);
        Assert.Equal(original, File.ReadAllText(credentials));
        BoardConnectionProfile profile = BoardConnectionProfile.Load(result.ProfilePath);
        Assert.Equal(Fingerprint, profile.HostKeyFingerprint);
        Assert.Equal(18791, profile.RemotePort); Assert.Equal(18889, profile.GatewayLocalPort);
        Assert.DoesNotContain("never-persist", string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        Assert.DoesNotContain(updates.Items, u => u.Detail.Contains(new string('A', 64)) || u.Detail.Contains(new string('B', 64)));
        Assert.DoesNotContain(updates.Items, u => u.Step == "verify" && u.Status == "ready");
    }
    [Fact]
    public async Task Invalid_secret_never_activates_pairing_or_replaces_previous_secret()
    {
        string pairing = Path.Combine(root, "connections", "127.0.0.1-22"); Directory.CreateDirectory(pairing);
        string secret = Path.Combine(pairing, "gateway.secrets.json"); File.WriteAllText(secret, "old private secret");
        Board board = new() { Secret = "{}" };
        await Assert.ThrowsAsync<SetupException>(() => new BoardSetup(root, () => board).Provision(Request, "password", new Updates(), CancellationToken.None));
        Assert.Equal("old private secret", File.ReadAllText(secret)); Assert.False(File.Exists(Path.Combine(pairing, "connection.json")));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"token\":42}")]
    [InlineData("{\"token\":\"short\"}")]
    [InlineData("not-json")]
    public void Invalid_gateway_content_has_safe_specific_guidance(string content)
    {
        SetupException error = Assert.Throws<SetupException>(() => BoardSetup.ValidateGatewaySecret(content));
        Assert.Equal("deploy", error.Step); Assert.Contains("网关凭据", error.Message); Assert.DoesNotContain(content, error.Message);
    }
    [Fact]
    public async Task Invalid_existing_executor_credentials_are_preserved_and_never_deployed()
    {
        Directory.CreateDirectory(Path.Combine(root, "executor"));
        string path = Path.Combine(root, "executor", "tokens.secrets.json");
        File.WriteAllText(path, "broken-private-content"); Board board = new();
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => new BoardSetup(root, () => board).Provision(Request, "password", new Updates(), CancellationToken.None));
        Assert.Equal("local", error.Step); Assert.False(board.Installed);
        Assert.Equal("broken-private-content", File.ReadAllText(path));
        Assert.DoesNotContain("broken-private-content", error.Message);
    }
    [Fact]
    public async Task Interrupted_installation_can_be_explicitly_resumed_with_the_same_key_and_tokens()
    {
        Board board = new() { CancelInstallation = true }; BoardSetup setup = new(root, () => board);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Provision(Request, "password", new Updates(), CancellationToken.None));
        string pairing = Path.Combine(root, "connections", "127.0.0.1-22");
        string key = File.ReadAllText(Path.Combine(pairing, "tunnel_ed25519")); string worker = board.Worker;
        Assert.False(File.Exists(Path.Combine(pairing, "connection.json")));
        board.CancelInstallation = false;
        SetupResult result = await setup.Provision(Request, "password", new Updates(), CancellationToken.None);
        Assert.True(File.Exists(result.ProfilePath)); Assert.Equal(key, File.ReadAllText(Path.Combine(pairing, "tunnel_ed25519"))); Assert.Equal(worker, board.Worker);
    }
    [Fact]
    public async Task Existing_pairing_cannot_be_silently_replaced_with_a_new_fingerprint()
    {
        Board board = new(); BoardSetup setup = new(root, () => board);
        SetupResult result = await setup.Provision(Request, "password", new Updates(), CancellationToken.None);
        string original = File.ReadAllText(result.ProfilePath);
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => setup.Provision(Request with { Fingerprint = "SHA256:" + new string('A', 43) }, "password", new Updates(), CancellationToken.None));
        Assert.Contains("已有配对", error.Message); Assert.Equal(original, File.ReadAllText(result.ProfilePath));
    }
    [Fact]
    public async Task Canceled_operation_never_starts_authentication()
    {
        BoardSetup setup = new(root, () => throw new InvalidOperationException("Must not contact board."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Provision(Request, "password", new Updates(), new CancellationToken(true)));
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Published_inspection_command_preserves_python_payload_through_both_shell_layers()
    {
        string command = (string)typeof(SshBoardSetupTransport).GetMethod("InspectionCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        // Parse the actual SSH command as bash would, then decode its Python payload.
        ProcessStartInfo start = new("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import ast,base64,hashlib,shlex,sys; outer=shlex.split(sys.argv[1]); assert outer[:2]==['bash','-lc']; inner=shlex.split(outer[2].split('; ',1)[1]); assert inner[:2]==['python3','-c']; tree=ast.parse(inner[2]); value=tree.body[1].value.args[0].args[0].value; print(hashlib.sha256(base64.b64decode(value)).hexdigest())");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        string result = await process.StandardOutput.ReadToEndAsync(); string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        Assembly assembly = typeof(SshBoardSetupTransport).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".SetupAssets.inspect-desktop.py", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), result.Trim());
        foreach (string name in new[] { "pair-desktop.py", "install-plugin.py", "configure-desktop.py", "package.json", "openclaw.plugin.json", "index.js", "client.js" })
            Assert.Contains(assembly.GetManifestResourceNames(), n => n.EndsWith(".SetupAssets." + name, StringComparison.Ordinal));
    }
    [Fact]
    public async Task Live_network_check_observes_device_becoming_available_without_authentication()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(await BoardSetup.Diagnose("127.0.0.1", port, CancellationToken.None), c => c.Step == "network" && c.Status == "ready");
        listener.Stop();
        Assert.Contains(await BoardSetup.Diagnose("127.0.0.1", port, CancellationToken.None), c => c.Step == "network" && c.Status == "missing" && c.Detail.Contains("电源"));
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(await BoardSetup.Diagnose("127.0.0.1", port, CancellationToken.None), c => c.Step == "network" && c.Status == "ready");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
