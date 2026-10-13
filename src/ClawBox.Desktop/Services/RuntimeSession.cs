using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class RuntimeSession : IDisposable
{
    private readonly List<Process> owned = [];
    public ExecutorClient? Executor { get; private set; }
    public GatewayClient? Gateway { get; private set; }
    public bool Alive => owned.Count == 2 && owned.All(p => !p.HasExited);
    public async Task Start(DesktopSettings settings, CancellationToken cancellation)
    {
        Dispose();
        try
        {
            Profile profile = JsonSerializer.Deserialize<Profile>(ConnectionFiles.Read(settings.ProfilePath, "盒子配对文件"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("配对文件无效。");
            profile.Validate();
            using JsonDocument gatewaySecret = JsonDocument.Parse(ConnectionFiles.Read(settings.GatewayTokenFile, "网关凭据文件"));
            string gatewayToken = Secret(gatewaySecret.RootElement, "token");
            if (gatewayToken.Length < 32 || gatewayToken.Any(char.IsControl)) throw new InvalidOperationException("网关凭据无效。");
            string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "executor");
            LocalState.MakePrivate(state);
            string tokensFile = Path.Combine(state, "tokens.secrets.json");
            if (!File.Exists(tokensFile)) throw new InvalidOperationException("请先配置本地执行器凭据，见运行指南。");
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
            using (HttpClient probe = CreateHealthClient())
            {
                try
                {
                    using HttpResponseMessage response = await probe.GetAsync($"http://127.0.0.1:{profile.ExecutorPort}/healthz", cancellation);
                    throw new InvalidOperationException("本地执行器端口已被占用，请关闭之前手动启动的执行器后连接。");
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
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
        Process process = Process.Start(info) ?? throw new InvalidOperationException("后台服务启动失败。");
        owned.Add(process);
        // Drain output without recording credentials, documents or arbitrary process diagnostics.
        _ = Drain(process.StandardOutput);
        _ = Drain(process.StandardError);
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
        foreach (Process process in owned.AsEnumerable().Reverse())
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        owned.Clear();
    }
    private sealed class Profile
    {
        public int Version { get; set; }
        public string BoxHost { get; set; } = "";
        public string SshUser { get; set; } = "";
        public int SshPort { get; set; }
        public int ExecutorPort { get; set; }
        public int RemotePort { get; set; }
        public int GatewayLocalPort { get; set; } = 18889;
        public string KnownHostsFile { get; set; } = "";
        public string IdentityFile { get; set; } = "";
        public string HostKeyFingerprint { get; set; } = "";
        public void Validate()
        {
            if (Version != 1 || !Regex.IsMatch(BoxHost, @"^[A-Za-z0-9][A-Za-z0-9.-]*$") || !Regex.IsMatch(SshUser, @"^[a-z_][a-z0-9_-]*$") ||
                SshPort is < 1 or > 65535 || ExecutorPort is < 1024 or > 65535 || RemotePort is < 1024 or > 65535 ||
                GatewayLocalPort is < 1024 or > 65535 || GatewayLocalPort == ExecutorPort ||
                !Path.IsPathFullyQualified(KnownHostsFile) || !Path.IsPathFullyQualified(IdentityFile) || !File.Exists(KnownHostsFile) || !File.Exists(IdentityFile) ||
                !Regex.IsMatch(HostKeyFingerprint, @"^SHA256:[A-Za-z0-9+/]{43}$"))
                throw new InvalidOperationException("盒子配对配置不完整，见设置中的运行指南。");
        }
    }
}
