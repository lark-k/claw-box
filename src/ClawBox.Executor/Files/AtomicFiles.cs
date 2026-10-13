using System.Security.Cryptography;
using ClawBox.Executor.Api;

namespace ClawBox.Executor.Files;

public sealed class AtomicFiles(PathPolicy policy)
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Write(string path, byte[] bytes, string? expectedHash = null)
    {
        path = policy.Resolve(path);
        bool exists = File.Exists(path);
        if (exists && (expectedHash is null || !string.Equals(HashFile(path), expectedHash, StringComparison.OrdinalIgnoreCase)))
            throw new ExecutorException("file_conflict", 409, "An existing file requires its current SHA-256 before replacement.");
        if (!exists && expectedHash is not null)
            throw new ExecutorException("file_conflict", 409, "The expected file no longer exists.");
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            throw new ExecutorException("directory_missing", 422, "The destination directory must already exist.");
        string temporary = Path.Combine(directory, ".clawbox-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            policy.Resolve(path);
            if (exists && !string.Equals(HashFile(path), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ExecutorException("file_conflict", 409, "The file changed before replacement.");
            File.Move(temporary, path, overwrite: exists);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
