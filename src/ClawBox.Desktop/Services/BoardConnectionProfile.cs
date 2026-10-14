using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClawBox.Desktop.Services;

public sealed class BoardConnectionProfile
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
    public static BoardConnectionProfile Load(string path)
    {
        BoardConnectionProfile profile = JsonSerializer.Deserialize<BoardConnectionProfile>(ConnectionFiles.Read(path, "盒子配对文件"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("配对文件无效。");
        profile.Validate(); return profile;
    }
    public void Validate()
    {
        if (Version != 1 || !Regex.IsMatch(BoxHost, @"^[A-Za-z0-9][A-Za-z0-9.-]*$") || !Regex.IsMatch(SshUser, @"^[a-z_][a-z0-9_-]*$") ||
            SshPort is < 1 or > 65535 || ExecutorPort is < 1024 or > 65535 || RemotePort is < 1024 or > 65535 ||
            GatewayLocalPort is < 1024 or > 65535 || GatewayLocalPort == ExecutorPort ||
            !Path.IsPathFullyQualified(KnownHostsFile) || !Path.IsPathFullyQualified(IdentityFile) || !File.Exists(KnownHostsFile) || !File.Exists(IdentityFile) ||
            !Regex.IsMatch(HostKeyFingerprint, @"^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new InvalidOperationException("盒子配对配置不完整，请打开连接引导检查。");
    }
}
