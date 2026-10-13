using System.Security.Cryptography;
using System.Text;

namespace ClawBox.Desktop.Services;

public static class WorkspaceContext
{
    public const string AgentId = "quantum-harness";
    public static string Normalize(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace)) throw new InvalidOperationException("工作区必须是 Windows 绝对路径。");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new InvalidOperationException("请选择具体工作区，不能授权整个磁盘。");
        return full;
    }
    public static string Key(Guid conversationId, string workspace)
    {
        string normalized = Normalize(workspace);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"agent:{AgentId}:desktop:{conversationId:N}:workspace:{digest}";
    }
}
