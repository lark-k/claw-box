using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class RuntimeSession : IDisposable
{
    private readonly List<Process> owned = [];
    private OwnedProcessJob? job;
    public ExecutorClient? Executor { get; private set; }
    public GatewayClient? Gateway { get; private set; }
    public bool Alive => owned.Count == 2 && owned.All(p => !p.HasExited);
    public async Task Start(DesktopSettings settings, CancellationToken cancellation)
    {
        Dispose();
        try
        {
            BoardConnectionProfile profile = BoardConnectionProfile.Load(settings.ProfilePath);
            using JsonDocument gatewaySecret = JsonDocument.Parse(ConnectionFiles.Read(settings.GatewayTokenFile, "网关凭据文件"));
            string gatewayToken = Secret(gatewaySecret.RootElement, "token");
            if (gatewayToken.Length < 32 || gatewayToken.Any(char.IsControl)) throw new InvalidOperationException("网关凭据无效。");
            string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "executor");
            LocalState.MakePrivate(state);
            string tokensFile = Path.Combine(state, "tokens.secrets.json");
            if (!File.Exists(tokensFile)) throw new InvalidOperationException("缺少本地执行器凭据。新设备请在连接引导中完成绑定；已有设备请让管理员恢复原凭据，避免与板子配置不一致。");
            using JsonDocument tokens = JsonDocument.Parse(File.ReadAllText(tokensFile));
            string admin = Secret(tokens.RootElement, "adminToken");
            string worker = Secret(tokens.RootElement, "workerToken");
            if (admin.Length < 32 || worker.Length < 32 || admin == worker) throw new InvalidOperationException("本地凭据无效。");
            if (!Path.IsPathFullyQualified(settings.Workspace) || Path.TrimEndingDirectorySeparator(settings.Workspace) == Path.GetPathRoot(settings.Workspace))
                throw new InvalidOperationException("请选择具体工作目录。");
            Directory.CreateDirectory(settings.Workspace);
            string executable = FindExecutor();
            ProcessStartInfo keyCheck = Hidden("ssh-keygen.exe", "-lf", profile.KnownHostsFile, "-E", "sha256");
            using (Process check = Process.Start(keyCheck)!)
            {
                string fingerprint = await check.StandardOutput.ReadToEndAsync(cancellation);
                await check.WaitForExitAsync(cancellation);
                if (check.ExitCode != 0 || fingerprint.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) != profile.HostKeyFingerprint)
                    throw new InvalidOperationException("盒子主机密钥与配对记录不一致，连接已停止。");
            }
            EnsurePortAvailable(profile.ExecutorPort);
            EnsurePortAvailable(profile.GatewayLocalPort);
            ProcessStartInfo executor = Hidden(executable);
            executor.WorkingDirectory = Path.GetDirectoryName(executable)!;
            foreach (string name in executor.Environment.Keys.Where(n => n.StartsWith("Executor__", StringComparison.Ordinal)).ToArray()) executor.Environment.Remove(name);
            executor.Environment["Executor__AdminToken"] = admin;
            executor.Environment["Executor__WorkerToken"] = worker;
            executor.Environment["Executor__AllowedRoots__0"] = settings.Workspace;
            executor.Environment["Executor__DataDirectory"] = state;
            executor.Environment["Executor__Port"] = profile.ExecutorPort.ToString();
            executor.Environment["Executor__ProjectRuntimeFile"] = ProjectRuntimeDiscovery.Ensure();
            Launch(executor);
            using HttpClient health = CreateHealthClient();
            await Ready(health, $"http://127.0.0.1:{profile.ExecutorPort}/healthz", cancellation);
            Launch(Hidden("ssh.exe", "-N", "-T", "-p", profile.SshPort.ToString(), "-i", profile.IdentityFile,
                "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "UserKnownHostsFile=" + profile.KnownHostsFile,
                "-o", "StrictHostKeyChecking=yes", "-o", "HostKeyAlgorithms=ssh-ed25519", "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ConnectTimeout=10",
                "-R", $"127.0.0.1:{profile.RemotePort}:127.0.0.1:{profile.ExecutorPort}",
                "-L", $"127.0.0.1:{profile.GatewayLocalPort}:127.0.0.1:18789", $"{profile.SshUser}@{profile.BoxHost}"));
            health.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gatewayToken);
            await Ready(health, $"http://127.0.0.1:{profile.GatewayLocalPort}/v1/models", cancellation);
            Executor = new(admin, worker, profile.ExecutorPort);
            Gateway = new(gatewayToken, profile.GatewayLocalPort);
        }
        catch { Dispose(); throw; }
    }
    private static string Secret(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("凭据文件格式无效，请重新配置连接。");
        string token = value.GetString()!;
        if (token.Length < 32 || token.Any(char.IsControl)) throw new InvalidOperationException("凭据文件格式无效，请重新配置连接。");
        return token;
    }
    public static HttpClient CreateHealthClient() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private async Task Ready(HttpClient client, string url, CancellationToken cancellation)
    {
        for (int i = 0; i < 30; i++)
        {
            if (owned.Any(p => p.HasExited)) throw new InvalidOperationException("后台连接启动失败。请检查端口占用、配对密钥或盒子网关配置。");
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, cancellation);
                if (response.IsSuccessStatusCode) return;
                if ((int)response.StatusCode == 401) throw new InvalidOperationException("盒子网关凭据无效，请更新配置。");
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { }
            await Task.Delay(300, cancellation);
        }
        throw new InvalidOperationException("连接盒子超时。请检查电源、网线、网络共享和网关服务。");
    }
    private void Launch(ProcessStartInfo info)
    {
        job ??= new OwnedProcessJob();
        Process process = Process.Start(info) ?? throw new InvalidOperationException("后台服务启动失败。");
        try { job.Add(process); }
        catch
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
            finally { process.Dispose(); }
            throw;
        }
        owned.Add(process);
        // Drain output without recording credentials, documents or arbitrary process diagnostics.
        _ = Drain(process.StandardOutput);
        _ = Drain(process.StandardError);
    }
    internal static void EnsurePortAvailable(int port)
    {
        using TcpListener probe = new(IPAddress.Loopback, port);
        try { probe.Start(); }
        catch (SocketException error)
        { throw new InvalidOperationException($"本地端口 {port} 无法使用，可能被旧版 APP、手动执行器或其他程序占用。请关闭对应程序后点击“使用已有配对连接”；无需重新绑定板子。", error); }
    }
    private static async Task Drain(StreamReader stream)
    {
        char[] buffer = new char[2048];
        try { while (await stream.ReadAsync(buffer) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private static ProcessStartInfo Hidden(string executable, params string[] arguments)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    private static string FindExecutor()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "executor", "ClawBox.Executor.exe");
        if (File.Exists(path)) return path;
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClawBox.sln")))
            {
                path = Path.Combine(current.FullName, "artifacts", "publish", "win-x64", "ClawBox.Executor.exe");
                if (File.Exists(path)) return path;
                break;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("未找到执行器，请使用完整应用包或运行 Start-Desktop.ps1。");
    }
    public void Dispose()
    {
        Gateway?.Dispose(); Executor?.Dispose(); Gateway = null; Executor = null;
        job?.Dispose(); job = null;
        foreach (Process process in owned.AsEnumerable().Reverse())
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        owned.Clear();
    }
}
