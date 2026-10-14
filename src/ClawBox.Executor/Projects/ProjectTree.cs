using System.Text;
using System.Text.Json;
using ClawBox.Executor.Api;
using ClawBox.Executor.Files;

namespace ClawBox.Executor.Projects;

public sealed record SourceFile(string RelativePath, byte[] Bytes, string Sha256);
public sealed record ProjectSnapshot(string Root, SourceFile[] Files, string Sha256);

public sealed class ProjectTree(PathPolicy policy)
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".codegraph", ".vs", ".idea", "node_modules", "bin", "obj", "target", "build", "dist", "coverage", "artifacts", ".gradle", ".mvn-cache", ".nuget" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".html", ".css", ".scss", ".json", ".java", ".xml", ".gradle", ".kts", ".properties", ".md", ".txt", ".yml", ".yaml", ".py", ".toml", ".config", ".bat", ".cmd", ".lock", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".woff", ".woff2", ".ttf" };
    public ProjectSnapshot Read(string path, CancellationToken cancellation)
    {
        string root = policy.ResolveDirectory(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        List<SourceFile> files = []; long bytes = 0;
        Stack<(string Path, int Depth)> pending = new(); pending.Push((root, 0));
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (directory.Depth > 20) throw TooLarge();
            foreach (string item in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                string name = Path.GetFileName(item);
                if (Ignored.Contains(name) || name.StartsWith(".qh-dependencies-", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name is "known_hosts" or ".npmrc" or "settings.xml") continue;
                string safe = policy.Resolve(item);
                if (Directory.Exists(safe)) { pending.Push((safe, directory.Depth + 1)); continue; }
                if (!Extensions.Contains(Path.GetExtension(safe)) && name != "Dockerfile" && name != ".gitignore") continue;
                using FileStream input = new(safe, FileMode.Open, FileAccess.Read, FileShare.Read);
                bytes += input.Length;
                if (files.Count >= 2000 || input.Length > 4 * 1024 * 1024 || bytes > 32 * 1024 * 1024) throw TooLarge();
                using MemoryStream content = new(); input.CopyTo(content);
                byte[] data = content.ToArray();
                if (data.Length != input.Length) throw new IOException("Source changed during snapshot.");
                files.Add(new(Path.GetRelativePath(root, safe).Replace('\\', '/'), data, AtomicFiles.Hash(data)));
            }
        }
        SourceFile[] ordered = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
        string digest = AtomicFiles.Hash(Encoding.UTF8.GetBytes(string.Concat(ordered.Select(f => f.RelativePath + "\0" + f.Sha256 + "\n"))));
        return new(root, ordered, digest);
    }
    public JsonElement List(string path, CancellationToken cancellation)
    {
        ProjectSnapshot snapshot = Read(path, cancellation);
        return JsonSerializer.SerializeToElement(new
        {
            path = snapshot.Root,
            snapshotSha256 = snapshot.Sha256,
            files = snapshot.Files.Select(f => new { path = Path.Combine(snapshot.Root, f.RelativePath), relativePath = f.RelativePath, bytes = f.Bytes.Length, sha256 = f.Sha256 })
        });
    }
    private static ExecutorException TooLarge() => new("project_too_large", 413, "Project snapshot exceeds the bounded source limits.");
}
