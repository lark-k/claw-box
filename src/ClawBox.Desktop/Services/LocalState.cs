using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class LocalState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; }
    public LocalState(string? root = null)
    {
        DirectoryPath = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "desktop");
        MakePrivate(DirectoryPath);
    }
    public static void MakePrivate(string path)
    {
        DirectoryInfo directory = Directory.CreateDirectory(path);
        DirectorySecurity acl = directory.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(true, false);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier))) acl.RemoveAccessRuleAll(rule);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    public T Load<T>(string name, Func<T> fallback)
    {
        string path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return fallback();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? fallback(); }
        catch (JsonException) { throw new InvalidOperationException("本地记录格式损坏。原文件已保留，请检查用户数据目录。"); }
    }
    public void Save<T>(string name, T value)
    {
        string path = Path.Combine(DirectoryPath, name), stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backup = path + ".bak";
        CheckPath(path); CheckPath(stage); CheckPath(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (FileStream stream = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            if (!File.Exists(path)) { File.Move(stage, path); return; }
            bool valid;
            try { using JsonDocument previous = JsonDocument.Parse(File.ReadAllText(path)); valid = true; }
            catch (JsonException) { valid = false; }
            if (valid) File.Replace(stage, path, backup);
            else
            {
                // Never rotate corrupt bytes over the last valid backup.
                string damaged = path + ".corrupt-" + Guid.NewGuid().ToString("N");
                CheckPath(damaged); File.Copy(path, damaged);
                File.Move(stage, path, overwrite: true);
            }
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private void CheckPath(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DirectoryPath)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("本地记录路径超出应用目录。");
        for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("本地记录路径含链接，操作已停止。");
    }
}
