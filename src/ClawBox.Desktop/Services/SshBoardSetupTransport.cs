using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ClawBox.Desktop.Services;

// This is a fixed deployment client, never registered as an agent tool.
public sealed class SshBoardSetupTransport : IBoardSetupTransport
{
    // SSH.NET interprets the decimal digits as an octal chmod mode, not a bitmask.
    private const short PrivateFileMode = 600;
    public static async Task<string> ObserveHostKey(string host, int port, CancellationToken cancellation)
    {
        SetupRequest.ValidateAddress(host, port);
        // Refuse key exchange before authentication. No user password is supplied.
        ConnectionInfo info = Connection(host, port, "");
        using SshClient client = new(info);
        string? known = null;
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = false;
            if (args.HostKeyName == "ssh-ed25519") known = (port == 22 ? host : $"[{host}]:{port}") + " ssh-ed25519 " + Convert.ToBase64String(args.HostKey);
        };
        try { await client.ConnectAsync(cancellation); }
        catch (Exception error) when (error is SshException or SocketException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            if (known is not null) return known;
            throw new SetupException("network", "无法取得设备身份。请检查设备地址、端口和 SSH 服务是否支持 Ed25519。");
        }
        throw new SetupException("identity", "设备身份探测未按预期停止，连接已关闭。");
    }
    private SshClient? ssh;
    private SetupRequest? request;
    private string password = "";
    private bool mismatch;
    public async Task<string> Connect(SetupRequest request, string password, CancellationToken cancellation)
    {
        this.request = request; this.password = password;
        ssh = new(CreateInfo());
        string knownHost = "";
        ssh.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = Trusted(args);
            if (args.CanTrust)
                knownHost = (request.Port == 22 ? request.Host : $"[{request.Host}]:{request.Port}") + " ssh-ed25519 " + Convert.ToBase64String(args.HostKey);
        };
        try { await ssh.ConnectAsync(cancellation); }
        catch (Exception error) when (error is SshException or SocketException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            if (mismatch) throw new SetupException("identity", "设备身份与原配对不一致。请核对是否仍是同一块板子；软件已停止连接，未发送登录密码。");
            if (error is SshAuthenticationException) throw new SetupException("identity", "设备登录失败。请核对交付人员提供的 root 密码；本向导目前支持 root 管理的标准板子镜像。");
            throw new SetupException("network", "SSH 连接未建立。请检查地址、端口、板子 SSH 服务及网络；若密码登录被禁用，请联系设备管理员。");
        }
        return knownHost;
    }
    private ConnectionInfo CreateInfo()
        => Connection(request!.Host, request.Port, password);
    private static ConnectionInfo Connection(string host, int port, string password)
    {
        ConnectionInfo info = new(host, port, "root", new PasswordAuthenticationMethod("root", password))
        { Timeout = TimeSpan.FromSeconds(10) };
        foreach (string algorithm in info.HostKeyAlgorithms.Keys.Where(k => k != "ssh-ed25519").ToArray()) info.HostKeyAlgorithms.Remove(algorithm);
        return info;
    }
    private bool Trusted(HostKeyEventArgs args)
    {
        bool trusted = args.HostKeyName == "ssh-ed25519" &&
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=') == request!.Fingerprint;
        if (!trusted) mismatch = true;
        return trusted;
    }
    public async Task<BoardReadiness> Inspect(CancellationToken cancellation)
    {
        string result = await Execute(InspectionCommand(), "board", "板子检查无法运行。请让交付人员检查 Python、OpenClaw 环境和标准镜像。", cancellation);
        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            if (!document.RootElement.TryGetProperty("issues", out JsonElement issues) || issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() > 16)
                throw new SetupException("board", "板子检查返回了无效结果。请联系交付人员。");
            string[] missing = issues.EnumerateArray().Select(i => i.GetString() ?? "unknown").ToArray();
            string networkPayload = Convert.ToBase64String(Asset("inspect-network.py"));
            string networkResult = await Execute(PythonCommand(networkPayload), "board", "板子网络检查未完成，请让交付人员核验固定 IP。", cancellation);
            BoardNetwork? network = JsonSerializer.Deserialize<BoardNetwork>(networkResult, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new(missing) { Network = network };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new SetupException("board", "板子检查结果无法识别。请联系交付人员核对软件与设备版本。"); }
    }
    internal static string InspectionCommand()
    {
        string payload = Convert.ToBase64String(Asset("inspect-desktop.py"));
        return PythonCommand(payload);
    }
    private static string PythonCommand(string payload) => "bash -lc 'source /root/.openclaw/env.sh 2>/dev/null; python3 -c \"import base64;exec(base64.b64decode(\\\"" + payload + "\\\"))\"'";
    public async Task Configure(string publicKey, string workerToken, IProgress<SetupUpdate> progress, CancellationToken cancellation)
    {
        // Stage names contain generated hex only; user input and secrets never enter shell commands.
        string stage = "/root/.openclaw/deployment/clawbox-windows/" + Guid.NewGuid().ToString("N");
        await Execute($"umask 077; mkdir -p {stage}; chmod 700 {stage}", "deploy", "板子私有配置目录无法创建。请检查磁盘空间与权限。", cancellation);
        progress.Report(new("deploy", "checking", "正在准备配对检查，仅传输工作凭据；已有插件和配置将优先复用…"));
        using (SftpClient sftp = await OpenSftp(cancellation))
        using (CancellationTokenRegistration registration = cancellation.Register(sftp.Dispose))
        {
            await Task.Run(() =>
            {
                foreach (string name in new[] { "pair-desktop.py", "recover-tunnel.py", "install-plugin.py", "configure-desktop.py", "package.json", "openclaw.plugin.json", "index.js", "client.js" })
                {
                    cancellation.ThrowIfCancellationRequested();
                    using MemoryStream file = new(Asset(name));
                    sftp.UploadFile(file, stage + "/" + name);
                    sftp.ChangePermissions(stage + "/" + name, PrivateFileMode);
                }
                Upload("tunnel_ed25519.pub", publicKey);
                Upload("worker-token", workerToken);
                void Upload(string name, string contents)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using MemoryStream file = new(Encoding.UTF8.GetBytes(contents));
                    sftp.UploadFile(file, stage + "/" + name);
                    sftp.ChangePermissions(stage + "/" + name, PrivateFileMode);
                }
            }, cancellation);
        }
        progress.Report(new("deploy", "checking", "正在检查当前电脑的受限配对密钥，已有密钥不会重复添加…"));
        await Execute($"python3 {stage}/pair-desktop.py {stage}/tunnel_ed25519.pub", "deploy", "受限密钥配置失败。请让管理员检查 SSH 转发条件或已有密钥权限。", cancellation);
        progress.Report(new("deploy", "checking", "正在检查已有 Windows 插件，仅补齐缺项或更新当前电脑配对…"));
        string plugin = Outcome(await Execute($"bash -lc 'source /root/.openclaw/env.sh && python3 {stage}/install-plugin.py {stage} --port 18791'", "deploy", "插件配置未通过校验。请让管理员检查 OpenClaw 版本与工具策略；安装器会保留或恢复原配置。", cancellation));
        progress.Report(new("deploy", "checking", plugin == "reused" ? "Windows 插件和配对已存在，跳过重复安装。正在检查聊天入口…" : plugin == "paired" ? "已复用 Windows 插件，仅更新当前电脑配对。正在检查聊天入口…" : "Windows 插件缺失或不匹配，已更新。正在检查聊天入口…"));
        string desktop = Outcome(await Execute($"bash -lc 'source /root/.openclaw/env.sh && python3 {stage}/configure-desktop.py {stage}/tunnel_ed25519.pub'", "deploy", "聊天入口检查或补齐失败。请让管理员检查网关认证和配置校验。已完成步骤会保留，重试需再次点击绑定。", cancellation));
        bool reload = NeedsRestart(plugin, desktop);
        progress.Report(new("deploy", "checking", reload ? "必要配置已更新，正在重启板子网关…" : "已有插件和聊天配置已复用，跳过重装与重启。正在检查网关服务…"));
        string service = reload ? "systemctl --user restart openclaw-gateway.service" : "systemctl --user is-active --quiet openclaw-gateway.service || systemctl --user start openclaw-gateway.service";
        await Execute("bash -lc 'export XDG_RUNTIME_DIR=/run/user/0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/0/bus; " + service + "'", "deploy", "板子网关服务未启动。请让管理员检查服务日志；已有配置保留，重试不会重装匹配的插件。", cancellation);
        progress.Report(new("deploy", "checking", "正在检查旧连接，仅回收已确认断开的本机隧道…"));
        System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(request!.Host, cancellation);
        int[] ports = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(t => t.State == System.Net.NetworkInformation.TcpState.Established && t.RemoteEndPoint.Port == request.Port && addresses.Contains(t.RemoteEndPoint.Address))
            .Select(t => t.LocalEndPoint.Port).Distinct().ToArray();
        using (SftpClient sftp = await OpenSftp(cancellation))
        using (MemoryStream data = new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), sshPort = request.Port, ports }))))
            sftp.UploadFile(data, stage + "/active-ports.json");
        string recovery = await Execute($"python3 {stage}/recover-tunnel.py {stage}/active-ports.json", "verify", "旧连接无法安全核验，未终止占用者。请关闭其他电脑的连接后重试。", cancellation);
        if (!recovery.Contains("CLAWBOX_RECOVERY=free", StringComparison.Ordinal) && !recovery.Contains("CLAWBOX_RECOVERY=released", StringComparison.Ordinal))
            throw new SetupException("verify", "板子端已有连接，尚不能证明它已失效，软件未终止占用者。请关闭其他电脑的 APP 后使用已有配对连接。");
    }
    private static string Outcome(string result)
    {
        string last = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        if (last is "CLAWBOX_RESULT=reused" or "CLAWBOX_RESULT=paired" or "CLAWBOX_RESULT=installed" or "CLAWBOX_RESULT=configured") return last[15..];
        throw new SetupException("deploy", "板子未返回明确的配置结果，已停止继续操作。请核对设备与 APP 版本。");
    }
    internal static bool NeedsRestart(string plugin, string desktop) => plugin != "reused" || desktop == "configured";
    public async Task<string> ReadGatewaySecret(CancellationToken cancellation)
    {
        using SftpClient sftp = await OpenSftp(cancellation);
        using CancellationTokenRegistration registration = cancellation.Register(sftp.Dispose);
        return await Task.Run(() =>
        {
            const string path = "/root/.openclaw/deployment/quantum-harness-gateway.secrets.json";
            if (sftp.GetAttributes(path).Size > 4096) throw new SetupException("deploy", "板子网关凭据文件大小异常，请联系管理员。");
            using Stream file = sftp.OpenRead(path);
            byte[] buffer = new byte[4097]; int length = 0;
            while (length < buffer.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = file.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > 4096) throw new SetupException("deploy", "板子网关凭据文件大小异常，请联系管理员。");
            cancellation.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetString(buffer, 0, length);
        }, cancellation);
    }
    private async Task<SftpClient> OpenSftp(CancellationToken cancellation)
    {
        SftpClient client = new(CreateInfo()) { OperationTimeout = TimeSpan.FromSeconds(20) };
        client.HostKeyReceived += (_, args) => args.CanTrust = Trusted(args);
        try { await client.ConnectAsync(cancellation); return client; }
        catch
        {
            client.Dispose(); cancellation.ThrowIfCancellationRequested();
            if (mismatch) throw new SetupException("identity", "传输时设备身份发生变化，配置已停止。请让管理员核验设备指纹。");
            throw;
        }
    }
    private async Task<string> Execute(string command, string step, string guidance, CancellationToken cancellation)
    {
        using SshCommand remote = ssh!.CreateCommand(command);
        remote.CommandTimeout = TimeSpan.FromSeconds(150);
        await remote.ExecuteAsync(cancellation);
        // Do not expose arbitrary remote output; it may include credential values.
        if (remote.ExitStatus != 0) throw new SetupException(step, guidance);
        return remote.Result;
    }
    private static byte[] Asset(string name)
    {
        Assembly assembly = typeof(SshBoardSetupTransport).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".SetupAssets." + name, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using MemoryStream contents = new(); stream.CopyTo(contents); return contents.ToArray();
    }
    public void Dispose() { ssh?.Dispose(); ssh = null; password = ""; request = null; }
}
