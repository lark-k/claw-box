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
        File.WriteAllText(stage, JsonSerializer.Serialize(value, Json));
        File.Move(stage, path, overwrite: true);
    }
}
