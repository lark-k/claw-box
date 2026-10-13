using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;

namespace ClawBox.Executor.Files;

public sealed class PathPolicy(ExecutorOptions options)
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('\0') || (OperatingSystem.IsWindows() && path[2..].Contains(':')))
            throw Denied();
        string full = Path.GetFullPath(path);
        if (!options.AllowedRoots.Any(root => IsWithin(full, Path.GetFullPath(root)))) throw Denied();
        if (OperatingSystem.IsWindows())
        {
            foreach (string segment in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar))
            {
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.') ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                     stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]))) throw Denied();
            }
        }
        // Refuse links/junctions in every existing ancestor, including configured roots.
        string? current = full;
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Denied();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
        return full;
    }

    public string ResolveDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Denied();
        string full = Path.GetFullPath(path);
        // The synthetic child preserves every ancestor/link check without enabling file writes to a root.
        if (options.AllowedRoots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(full), Comparison)))
            return Path.GetDirectoryName(Resolve(Path.Combine(full, ".clawbox-directory-check")))!;
        return Resolve(full);
    }

    private static bool IsWithin(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    }

    private static ExecutorException Denied() => new("path_denied", 403, "The requested path is outside the approved workspace or uses an unsupported path type.");
}
