using System.Text;
using System.Text.Json;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Projects;

namespace ClawBox.Executor.Files;

public sealed class WorkspaceFiles(PathPolicy policy, AtomicFiles files, ExecutorOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public JsonElement CreateDirectory(string path, CancellationToken cancellation)
    {
        path = policy.ResolveDirectory(path);
        if (File.Exists(path)) throw new ExecutorException("path_type_conflict", 409, "A file occupies this directory path.");
        bool exists = Directory.Exists(path);
        // Limit implicit parent creation; every parent remains within the same approved workspace.
        int parents = 0;
        for (string? current = path; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            policy.ResolveDirectory(current);
            if (++parents > 20) throw new ExecutorException("directory_depth_exceeded", 413, "Too many missing parent directories.");
        }
        cancellation.ThrowIfCancellationRequested();
        policy.ResolveDirectory(path);
        Directory.CreateDirectory(path);
        return JsonSerializer.SerializeToElement(new { path, created = !exists });
    }
    public JsonElement ListDirectory(string path, CancellationToken cancellation)
    {
        path = policy.ResolveDirectory(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
        List<object> entries = []; int scanned = 0; bool truncated = false;
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++scanned > 10000 || entries.Count >= 2000) { truncated = true; break; }
            string name = Path.GetFileName(entry);
            if (PrivateName(name)) continue;
            // Report links without following or accessing their target.
            FileAttributes attributes = File.GetAttributes(entry);
            bool link = (attributes & FileAttributes.ReparsePoint) != 0;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if (!link) policy.Resolve(entry);
            entries.Add(new { path = entry, name, kind = link ? "link" : directory ? "directory" : "file", bytes = link || directory ? (long?)null : new FileInfo(entry).Length });
        }
        return JsonSerializer.SerializeToElement(new { path, entries, truncated });
    }
    private static bool PrivateName(string name) => name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Equals("known_hosts", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) || name.Equals("settings.xml", StringComparison.OrdinalIgnoreCase) ||
        new[] { ".key", ".pem", ".pfx" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    public JsonElement Info(string path)
    {
        path = policy.Resolve(path);
        byte[] bytes = Read(path, options.MaxDocumentBytes);
        return JsonSerializer.SerializeToElement(new { path, bytes = bytes.Length, sha256 = AtomicFiles.Hash(bytes), lastWriteUtc = File.GetLastWriteTimeUtc(path) });
    }
    public JsonElement Search(string path, string query, CancellationToken cancellation)
    {
        ProjectSnapshot snapshot = new ProjectTree(policy).Read(path, cancellation);
        List<object> matches = []; bool truncated = false;
        foreach (SourceFile source in snapshot.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (source.Bytes.Contains((byte)0)) continue;
            string content;
            try { content = Utf8.GetString(source.Bytes); } catch (DecoderFallbackException) { continue; }
            using StringReader reader = new(content); string? line; int lineNumber = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellation.ThrowIfCancellationRequested(); lineNumber++;
                if (!line.Contains(query, StringComparison.Ordinal)) continue;
                if (matches.Count == 100) { truncated = true; break; }
                int offset = Math.Max(0, line.IndexOf(query, StringComparison.Ordinal) - 60);
                string excerpt = line.Substring(offset, Math.Min(240, line.Length - offset));
                matches.Add(new { path = Path.Combine(snapshot.Root, source.RelativePath), line = lineNumber, excerpt, sha256 = source.Sha256 });
            }
            if (truncated) break;
        }
        return JsonSerializer.SerializeToElement(new { path = snapshot.Root, query, matches, truncated });
    }
    public JsonElement CopyOrMove(string path, string outputPath, string expected, bool move, CancellationToken cancellation)
    {
        path = policy.Resolve(path); outputPath = policy.Resolve(outputPath);
        byte[] bytes = Read(path, options.MaxDocumentBytes);
        string hash = AtomicFiles.Hash(bytes);
        RequireHash(hash, expected);
        if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new ExecutorException("file_conflict", 409, "Destination already exists; overwriting is forbidden.");
        if (!Directory.Exists(Path.GetDirectoryName(outputPath))) throw new ExecutorException("directory_missing", 422, "Create the destination directory first.");
        cancellation.ThrowIfCancellationRequested();
        if (move)
        {
            if (!string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(outputPath), StringComparison.OrdinalIgnoreCase))
                throw new ExecutorException("cross_volume_move_unsupported", 422, "Use copy for a destination on another volume.");
            policy.Resolve(path); policy.Resolve(outputPath);
            RequireHash(AtomicFiles.Hash(Read(path, options.MaxDocumentBytes)), expected);
            File.Move(path, outputPath, overwrite: false);
        }
        else files.Write(outputPath, bytes);
        return JsonSerializer.SerializeToElement(new { path = outputPath, sourcePath = path, bytes = bytes.Length, sha256 = hash, moved = move });
    }
    public JsonElement Replace(string path, string search, string replacement, string expected, CancellationToken cancellation)
    {
        path = policy.Resolve(path);
        byte[] bytes = Read(path, options.MaxInputBytes); RequireHash(AtomicFiles.Hash(bytes), expected);
        string content = Utf8.GetString(bytes); int count = 0;
        for (int start = 0; ;)
        {
            cancellation.ThrowIfCancellationRequested();
            int found = content.IndexOf(search, start, StringComparison.Ordinal);
            if (found < 0) break;
            if (++count > 10000) throw new ExecutorException("output_too_large", 413, "Too many replacement matches.");
            start = found + search.Length;
        }
        if (count == 0) throw new ExecutorException("search_not_found", 422, "Literal search was not found; the file was not changed.");
        if ((long)bytes.Length + (long)count * (Utf8.GetByteCount(replacement) - Utf8.GetByteCount(search)) > options.MaxInputBytes)
            throw new ExecutorException("output_too_large", 413, "Replacement output exceeds the size limit.");
        byte[] output = Utf8.GetBytes(content.Replace(search, replacement, StringComparison.Ordinal));
        cancellation.ThrowIfCancellationRequested(); files.Write(path, output, expected);
        return JsonSerializer.SerializeToElement(new { path, replacements = count, bytes = output.Length, sha256 = AtomicFiles.Hash(output) });
    }
    public static void RequireHash(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new ExecutorException("file_conflict", 409, "Source changed; read the current file/hash and obtain a new approval.");
    }
    public static byte[] Read(string path, int limit)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new ExecutorException("file_too_large", 413, "File exceeds the size limit.");
        using MemoryStream bytes = new(); stream.CopyTo(bytes);
        if (bytes.Length > limit) throw new ExecutorException("file_too_large", 413, "File exceeds the size limit.");
        return bytes.ToArray();
    }
}
