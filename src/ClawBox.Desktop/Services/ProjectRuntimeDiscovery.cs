using System.Text.Json;

namespace ClawBox.Desktop.Services;

public static class ProjectRuntimeDiscovery
{
    public static string Ensure()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox");
        Directory.CreateDirectory(root);
        string configuration = Path.Combine(root, "project-runtime.json");
        if (File.Exists(configuration)) return configuration;
        string privateDotnet = Path.Combine(root, "sdk", "dotnet.exe");
        string dotnet = File.Exists(privateDotnet) ? privateDotnet : Find("dotnet.exe");
        string node = Find("node.exe"), java = Find("java.exe"), maven = Find("mvn.cmd"), gradle = Find("gradle.bat");
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runtimes = new
        {
            dotnetRoot = Parent(dotnet),
            nodeRoot = Parent(node),
            javaHome = Parent(java, 2),
            mavenHome = Parent(maven, 2),
            gradleHome = Parent(gradle, 2),
            nugetCache = Existing(Path.Combine(user, ".nuget", "packages")),
            mavenCache = Existing(Path.Combine(user, ".m2", "repository")),
            gradleCache = Existing(Path.Combine(user, ".gradle"))
        };
        string stage = configuration + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(stage, JsonSerializer.Serialize(runtimes, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(stage, configuration, overwrite: false);
        return configuration;
    }
    private static string Find(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(path)) return path;
        }
        return "";
    }
    private static string Existing(string path) => Directory.Exists(path) ? path : "";
    private static string Parent(string file, int levels = 1)
    {
        if (string.IsNullOrWhiteSpace(file)) return "";
        FileInfo info = new(file);
        string path = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
        for (int i = 0; i < levels; i++) path = Path.GetDirectoryName(path)!;
        string original = path;
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            FileSystemInfo? target = directory.ResolveLinkTarget(true);
            if (target is not null) return Path.GetFullPath(Path.Combine(target.FullName, Path.GetRelativePath(directory.FullName, original)));
        }
        return path;
    }
}
