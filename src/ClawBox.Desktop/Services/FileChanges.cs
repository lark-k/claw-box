using System.Security.Cryptography;
using System.Text;
using ClawBox.Contracts;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public static class FileChanges
{
    public sealed record Snapshot(bool Exists, string? Text);
    public static bool IsMutation(string tool) => tool is "file.write" or "file.replace" or "file.copy" or "file.move" or "word.create" or "word.replace" or "word.format";
    public static bool CanApprove(string mode, string tool) => mode != "readOnly" || tool is "file.read" or "file.info" or "file.search" or "file.list" or "word.read" or "directory.list";
    public static string Target(ToolCall call) =>
        call.Arguments.TryGetProperty("outputPath", out var output) ? output.GetString() ?? "" :
        call.Arguments.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "";
    public static Snapshot Capture(string path, string workspace, string? expectedHash = null)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path), root = WorkspaceContext.Normalize(workspace);
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(true, null);
            for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) return new(true, null);
            if (!File.Exists(full)) return new(false, "");
            if (new FileInfo(full).Length > 256 * 1024 || System.IO.Path.GetExtension(full).Equals(".docx", StringComparison.OrdinalIgnoreCase)) return new(true, null);
            byte[] bytes = File.ReadAllBytes(full);
            if (bytes.Length > 256 * 1024 || bytes.Contains((byte)0)) return new(true, null);
            if (expectedHash is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return new(true, null);
            return new(true, new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return new(true, null); }
    }
    public static FileChange Compare(string path, string workspace, Snapshot before, Snapshot after)
    {
        FileChange result = new() { Path = path, DisplayPath = System.IO.Path.GetRelativePath(workspace, path) };
        if (before.Text is null || after.Text is null)
        {
            result.Note = "文件已更新 · 内容无法预览";
            return result;
        }
        string[] oldLines = Split(before.Text), newLines = Split(after.Text);
        if (oldLines.Length > 1500 || newLines.Length > 1500)
        {
            result.Note = "文件已更新 · 超出差异预览范围";
            return result;
        }
        int[,] lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (int i = oldLines.Length - 1; i >= 0; i--)
            for (int j = newLines.Length - 1; j >= 0; j--)
                lengths[i, j] = oldLines[i] == newLines[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        int a = 0, b = 0;
        while (a < oldLines.Length || b < newLines.Length)
        {
            if (a < oldLines.Length && b < newLines.Length && oldLines[a] == newLines[b])
            { result.Lines.Add(new((b + 1).ToString(), "  " + newLines[b], "context")); a++; b++; }
            else if (a < oldLines.Length && (b == newLines.Length || lengths[a + 1, b] >= lengths[a, b + 1]))
            { result.Lines.Add(new((a + 1).ToString(), "− " + oldLines[a], "removed")); a++; }
            else { result.Lines.Add(new((b + 1).ToString(), "+ " + newLines[b], "added")); b++; }
        }
        result.HasDiff = true;
        result.Note = !before.Exists ? "新建文件" : !after.Exists ? "已移走原路径" : "已修改";
        return result;
    }
    private static string[] Split(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
