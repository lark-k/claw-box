using System.Security.Cryptography;
using System.Text.Json;

namespace ClawBox.Desktop.Services;

public sealed class BoardIdentity
{
    private readonly Func<string, int, CancellationToken, Task<string>> observe;
    public BoardIdentity(Func<string, int, CancellationToken, Task<string>>? observe = null)
        => this.observe = observe ?? SshBoardSetupTransport.ObserveHostKey;
    public SetupRequest? Saved(string host, int port, string profilePath)
    {
        SetupRequest.ValidateAddress(host, port);
        if (!Path.IsPathFullyQualified(profilePath)) throw new SetupException("identity", "配对文件路径无效，请从高级入口导入原配对文件。");
        if (File.Exists(profilePath))
        {
            try
            {
                BoardConnectionProfile profile = JsonSerializer.Deserialize<BoardConnectionProfile>(File.ReadAllText(profilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
                SetupRequest original = new(profile.BoxHost, profile.SshPort, profile.HostKeyFingerprint); original.Validate();
                if (profile.Version != 1) throw new JsonException();
                if (File.Exists(profile.KnownHostsFile)) BoardSetup.ValidateKnownHost(original, File.ReadAllText(profile.KnownHostsFile));
                return new(host, port, profile.HostKeyFingerprint);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { throw new SetupException("identity", "已保存的配对记录损坏。请恢复原记录；软件不会重新接受其他设备身份。"); }
        }
        string pending = Path.Combine(Path.GetDirectoryName(profilePath)!, "known_hosts");
        if (!File.Exists(pending)) return null;
        string known = File.ReadAllText(pending).Trim();
        string[] parts = known.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[1] != "ssh-ed25519") throw new SetupException("identity", "已有设备身份记录无效，请联系管理员修复。");
        try { return new(host, port, "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[2]))).TrimEnd('=')); }
        catch (FormatException) { throw new SetupException("identity", "已有设备身份记录无效，请联系管理员修复。"); }
    }
    public async Task<SetupRequest> Resolve(string host, int port, string profilePath, bool trustFirstDevice, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        SetupRequest? saved = Saved(host, port, profilePath);
        if (saved is not null) return saved;
        if (!trustFirstDevice) throw new SetupException("identity", "请先确认当前连接的是自己的板子，软件才会获取首次设备身份并登录。");
        string known = await observe(host, port, cancellation);
        string[] parts = known.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (parts.Length != 3) throw new FormatException();
            SetupRequest request = new(host, port, "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[2]))).TrimEnd('='));
            BoardSetup.ValidateKnownHost(request, known); return request;
        }
        catch (FormatException) { throw new SetupException("identity", "设备没有返回有效的主机公钥。请检查 SSH 服务。"); }
    }
    public async Task UpdateAddress(string profilePath, string host, int port, CancellationToken cancellation)
    {
        SetupRequest.ValidateAddress(host, port);
        BoardConnectionProfile profile = BoardConnectionProfile.Load(profilePath);
        if (profile.BoxHost == host && profile.SshPort == port) return;
        SetupRequest target = Saved(host, port, profilePath)!;
        string known = await observe(host, port, cancellation);
        BoardSetup.ValidateKnownHost(target, known);
        cancellation.ThrowIfCancellationRequested();
        CheckPlainPath(profilePath); CheckPlainPath(profile.KnownHostsFile);
        string originalKnown = File.ReadAllText(profile.KnownHostsFile);
        profile.BoxHost = host; profile.SshPort = port;
        AtomicWrite(profile.KnownHostsFile, known.Trim() + "\n");
        try { AtomicWrite(profilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); }
        catch { AtomicWrite(profile.KnownHostsFile, originalKnown); throw; }
    }
    private static void CheckPlainPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new SetupException("local", "配对路径包含链接，请联系管理员修复后更新地址。");
    }
    private static void AtomicWrite(string path, string text)
    {
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(stage, text); File.Move(stage, path, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
