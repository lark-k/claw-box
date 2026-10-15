using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed record SetupUpdate(string Step, string Status, string Detail);
public sealed record SetupRequest(string Host, int Port, string Fingerprint)
{
    public void Validate()
    {
        ValidateAddress(Host, Port);
        if (!Regex.IsMatch(Fingerprint, @"^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new SetupException("identity", "缺少可信设备指纹。请从设备交付卡或管理员获取 SHA256 指纹；不要使用未经核验的网络扫描结果。");
    }
    public static void ValidateAddress(string host, int port)
    {
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || port is < 1 or > 65535)
            throw new SetupException("network", "请输入有效设备 IP 或主机名，以及 1–65535 的 SSH 端口。默认端口为 22。");
    }
}
public sealed class SetupException(string step, string message) : Exception(message)
{
    public string Step { get; } = step;
}
public sealed record SetupResult(string ProfilePath, string GatewayTokenFile);
public sealed record BoardReadiness(string[] Issues)
{
    public BoardNetwork? Network { get; init; }
    public static string Guidance(string issue) => issue switch
    {
        "python" => "板子缺少 Python 3.11 或更新版本。请让设备管理员安装后重试。",
        "openclaw" or "environment" => "板子智能体运行环境未就绪。请让交付人员检查 OpenClaw 与 /root/.openclaw/env.sh。",
        "gateway" => "板子缺少可用的本机认证网关。请让管理员配置 loopback、token 认证和有效网关凭据后重试。",
        "policy" => "板子缺少明确的工具允许名单。请让管理员完成 tools.allow 配置；软件不会放宽安全策略。",
        "legacy" => "板子使用旧版智能体配置。请让管理员按已安装的 OpenClaw 版本迁移配置后重试。",
        "model" => "板子尚未配置默认模型。请让交付人员设置模型服务地址、模型和访问凭据，再重试。模型密钥保留在板子上。",
        "config" => "板子配置文件不存在或格式无效。请让交付人员修复 /root/.openclaw/openclaw.json。",
        "forwarding" => "板子 SSH 转发条件未满足。请让管理员确认 GatewayPorts no 与 AllowTcpForwarding yes。",
        _ => "板子返回了无法识别的检查结果。请联系交付人员核对设备镜像和软件版本。"
    };
    public void RequireReady()
    {
        if (Issues.Length > 0) throw new SetupException("board", string.Join("\n", Issues.Distinct().Select(Guidance)));
    }
}
public sealed record BoardNetwork(string Address, string Interface, string Method, string[] ConfiguredAddresses, bool Persistent, string Gateway, string ModelNetwork = "unknown")
{
    public SetupUpdate Describe()
    {
        bool fixedAddress = Persistent && Method == "manual" && ConfiguredAddresses.Any(a => a.Split('/')[0] == Address);
        return new("boardIp", fixedAddress ? "ready" : "missing", fixedAddress
            ? $"板子 {Interface} 的地址 {Address} 已在持久配置中设为固定 IP。换电脑时，将直连网卡配置到同网段即可。"
            : Method == "auto" ? $"板子当前地址为 {Address}，使用 DHCP，换电脑后地址可能变化。交付前请让管理员确认固定 IP 配置。"
            : $"板子当前地址为 {Address}。尚未确认它已持久固定，请让交付人员核验网络配置。");
    }
    public SetupUpdate DescribeModelNetwork() => ModelNetwork switch
    {
        "reachable" => new("internet", "ready", "板子已能连到模型服务的网络端口。真实模型回答由首次聊天验证。"),
        "unreachable" => new("internet", "missing", "板子无法连到模型服务。请确认电脑已上网，点击“设置网络共享”，将上网网卡共享给连接板子的以太网；也可能是服务暂不可用或网络限制。"),
        _ => new("internet", "waiting", "模型网络未自动确认。请开启网络共享，连接后发送一条聊天验证。")
    };
}
public interface IBoardSetupTransport : IDisposable
{
    Task<string> Connect(SetupRequest request, string password, CancellationToken cancellation);
    Task<BoardReadiness> Inspect(CancellationToken cancellation);
    Task Configure(string publicKey, string workerToken, IProgress<SetupUpdate> progress, CancellationToken cancellation);
    Task<string> ReadGatewaySecret(CancellationToken cancellation);
}

public sealed class BoardSetup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string dataRoot;
    private readonly Func<IBoardSetupTransport> createTransport;
    public BoardSetup(string? dataRoot = null, Func<IBoardSetupTransport>? createTransport = null)
    {
        this.dataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox");
        this.createTransport = createTransport ?? (() => new SshBoardSetupTransport());
    }
    public static string? OpenSshTool(string name)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", name);
        return File.Exists(path) ? path : null;
    }
    public static async Task<SetupUpdate[]> Diagnose(string host, int port, CancellationToken cancellation, bool includeNetwork = true)
    {
        List<SetupUpdate> checks = [];
        bool tools = OpenSshTool("ssh.exe") is not null && OpenSshTool("ssh-keygen.exe") is not null;
        checks.Add(new("local", tools ? "ready" : "missing", tools ? "Windows 连接组件已安装。" : "缺少 Windows OpenSSH 客户端。打开 Windows 设置 → 系统 → 可选功能，添加 OpenSSH 客户端，然后返回本窗口。无需安装 SDK。"));
        if (!includeNetwork) return checks.ToArray();
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || port is < 1 or > 65535)
        {
            checks.Add(new("network", "missing", "请输入设备 IP / 主机名和有效端口。地址请查看交付卡或路由器设备列表。"));
            return checks.ToArray();
        }
        try
        {
            using TcpClient tcp = new();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, port, timeout.Token);
            checks.Add(new("network", "ready", "设备 SSH 端口可达。还需核验设备身份和智能体服务。"));
        }
        catch (Exception error) when (error is SocketException || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            checks.Add(new("network", "missing", "设备端口暂不可达。请检查板子电源、网线与设备地址；直连电脑时请开启网络共享，并按交付说明配置板子地址。此状态不能确定具体是电源还是网络问题。"));
        }
        cancellation.ThrowIfCancellationRequested();
        return checks.ToArray();
    }
    public async Task Inspect(SetupRequest request, string password, IProgress<SetupUpdate> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        request.Validate(); RequirePassword(password);
        using IBoardSetupTransport transport = createTransport();
        progress.Report(new("identity", "checking", "正在核验设备指纹并验证本次登录…"));
        ValidateKnownHost(request, await transport.Connect(request, password, cancellation));
        progress.Report(new("identity", "ready", "设备身份已核验，登录成功。密码只用于本次配置。"));
        progress.Report(new("board", "checking", "正在读取板子运行环境、模型配置和安全策略…"));
        BoardReadiness readiness = await transport.Inspect(cancellation);
        if (readiness.Network is not null) { progress.Report(readiness.Network.Describe()); progress.Report(readiness.Network.DescribeModelNetwork()); }
        readiness.RequireReady();
        progress.Report(new("board", "ready", "板子配置条件已满足。远端模型实际可用性将在首次聊天时验证。"));
    }
    public async Task<SetupResult> Provision(SetupRequest request, string password, IProgress<SetupUpdate> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        request.Validate(); RequirePassword(password);
        if (OpenSshTool("ssh.exe") is null || OpenSshTool("ssh-keygen.exe") is null)
            throw new SetupException("local", "请先在 Windows 可选功能中安装 OpenSSH 客户端，返回后会自动复查。");
        string pairing = Path.Combine(dataRoot, "connections", request.Host + "-" + request.Port);
        string profilePath = Path.Combine(pairing, "connection.json");
        string identity = Path.Combine(pairing, "tunnel_ed25519");
        // Never silently replace a pinned identity, even when a prior pairing is incomplete.
        if (File.Exists(profilePath))
        {
            BoardConnectionProfile old = BoardConnectionProfile.Load(profilePath);
            if (old.BoxHost != request.Host || old.SshPort != request.Port || old.SshUser != "root" || old.HostKeyFingerprint != request.Fingerprint || old.RemotePort != 18791 || old.ExecutorPort != 18790 || old.GatewayLocalPort != 18889 || old.IdentityFile != identity)
                throw new SetupException("identity", "已有配对与本次输入不同。请使用原配对连接，或让管理员撤销旧配对后再绑定；软件不会替换设备身份或自定义端口。");
        }
        using IBoardSetupTransport transport = createTransport();
        progress.Report(new("identity", "checking", "正在核验设备身份和登录…"));
        string knownHost = await transport.Connect(request, password, cancellation);
        ValidateKnownHost(request, knownHost);
        progress.Report(new("identity", "ready", "设备身份已核验，登录成功。"));
        progress.Report(new("board", "checking", "正在检查板子配置；缺少部署条件时不会继续修改…"));
        BoardReadiness readiness = await transport.Inspect(cancellation);
        if (readiness.Network is not null) { progress.Report(readiness.Network.Describe()); progress.Report(readiness.Network.DescribeModelNetwork()); }
        readiness.RequireReady();
        progress.Report(new("board", "ready", "板子配置条件已满足。"));
        if (readiness.Network?.ModelNetwork == "unreachable") throw new SetupException("internet", readiness.Network.DescribeModelNetwork().Detail);
        cancellation.ThrowIfCancellationRequested();
        MakePrivate(pairing);
        string knownHosts = Path.Combine(pairing, "known_hosts");
        foreach (string file in new[] { knownHosts, identity, identity + ".pub", Path.Combine(pairing, "gateway.secrets.json"), profilePath }) RejectLinkedFile(file);
        if (File.Exists(knownHosts) && File.ReadAllText(knownHosts).Trim() != knownHost.Trim())
            throw new SetupException("identity", "已保存的设备主机密钥不同。请联系管理员核验，软件不会覆盖旧主机密钥。");
        if (!File.Exists(knownHosts)) WriteNew(knownHosts, knownHost + "\n");
        string worker = EnsureCredentials();
        progress.Report(new("deploy", "checking", "正在准备本机专用密钥…"));
        await GenerateKey(identity, cancellation);
        string publicKey = File.ReadAllText(identity + ".pub").Trim();
        if (!Regex.IsMatch(publicKey, @"^ssh-ed25519 [A-Za-z0-9+/=]+ clawbox-reverse-tunnel$"))
            throw new SetupException("deploy", "本机专用公钥无效。请联系管理员检查已有配对目录；不会自动删除或覆盖密钥。");
        await transport.Configure(publicKey, worker, progress, cancellation);
        string secret = await transport.ReadGatewaySecret(cancellation);
        ValidateGatewaySecret(secret);
        cancellation.ThrowIfCancellationRequested();
        string gatewayFile = Path.Combine(pairing, "gateway.secrets.json");
        if (!File.Exists(gatewayFile) || File.ReadAllText(gatewayFile) != secret) AtomicWrite(gatewayFile, secret);
        BoardConnectionProfile profile = new()
        {
            Version = 1,
            BoxHost = request.Host,
            SshUser = "root",
            SshPort = request.Port,
            ExecutorPort = 18790,
            RemotePort = 18791,
            GatewayLocalPort = 18889,
            KnownHostsFile = knownHosts,
            IdentityFile = identity,
            HostKeyFingerprint = request.Fingerprint
        };
        if (!File.Exists(profilePath)) WriteNew(profilePath, JsonSerializer.Serialize(profile, Json));
        progress.Report(new("deploy", "ready", "配对已确认；已有插件和聊天配置按需复用。正在连接验收。"));
        return new(profilePath, gatewayFile);
    }
    public static void ValidateKnownHost(SetupRequest request, string knownHost)
    {
        string[] parts = knownHost.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string label = request.Port == 22 ? request.Host : $"[{request.Host}]:{request.Port}";
        try
        {
            if (parts.Length != 3 || parts[0] != label || parts[1] != "ssh-ed25519" ||
                "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[2]))).TrimEnd('=') != request.Fingerprint)
                throw new SetupException("identity", "设备身份与已记录的板子不一致。请核对设备地址，确认仍是原来的板子；软件已停止配对。");
        }
        catch (FormatException) { throw new SetupException("identity", "设备主机密钥格式无效，配对已停止。"); }
    }
    public static void ValidateGatewaySecret(string secret)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(secret);
            if (secret.Length > 4096 || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("token", out JsonElement element) || element.ValueKind != JsonValueKind.String ||
                element.GetString() is not string token || token.Length < 32 || token.Any(char.IsControl))
                throw new SetupException("deploy", "板子返回的网关凭据无效。已有凭据保留，请让管理员检查网关配置。");
        }
        catch (JsonException) { throw new SetupException("deploy", "板子返回的网关凭据格式无效。请让管理员检查网关配置。"); }
    }
    private static void RequirePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new SetupException("identity", "请输入交付人员提供的设备首次登录密码。密码不会保存；后续连接使用专用密钥。");
    }
    private string EnsureCredentials()
    {
        string directory = Path.Combine(dataRoot, "executor");
        MakePrivate(directory);
        string path = Path.Combine(directory, "tokens.secrets.json");
        RejectLinkedFile(path);
        if (!File.Exists(path)) WriteNew(path, JsonSerializer.Serialize(new
        {
            adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            workerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        }, Json));
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            string? admin = document.RootElement.GetProperty("adminToken").GetString();
            string? worker = document.RootElement.GetProperty("workerToken").GetString();
            if (admin is null || worker is null || !Regex.IsMatch(admin, "^[A-Fa-f0-9]{64}$") || !Regex.IsMatch(worker, "^[A-Fa-f0-9]{64}$") || admin == worker)
                throw new SetupException("local", "已有执行器凭据无效。请联系管理员修复，软件不会重新生成或替换它们。");
            return worker;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new SetupException("local", "已有执行器凭据格式无效。请联系管理员修复，软件不会覆盖原文件。"); }
    }
    private static async Task GenerateKey(string path, CancellationToken cancellation)
    {
        if (File.Exists(path))
        {
            if (!File.Exists(path + ".pub")) throw new SetupException("local", "已有专用密钥缺少公钥文件。请让管理员修复；软件不会覆盖密钥。");
            return;
        }
        if (File.Exists(path + ".pub")) throw new SetupException("local", "已有公钥缺少私钥。请让管理员修复；软件不会覆盖公钥。");
        ProcessStartInfo start = new(OpenSshTool("ssh-keygen.exe")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "clawbox-reverse-tunnel", "-f", path }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        using CancellationTokenRegistration registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new SetupException("local", "专用密钥生成失败。请检查 Windows OpenSSH 安装和当前用户目录权限。");
    }
    private static void MakePrivate(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new SetupException("local", "配对目录包含目录链接。请让管理员选择普通用户目录，避免凭据写入其他位置。");
        LocalState.MakePrivate(path);
    }
    private static void RejectLinkedFile(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new SetupException("local", "配对或凭据文件包含文件链接。请让管理员修复，软件不会读取或覆盖链接目标。");
    }
    private static void WriteNew(string path, string text)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream); writer.Write(text);
    }
    private static void AtomicWrite(string path, string text)
    {
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteNew(stage, text); File.Move(stage, path, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
