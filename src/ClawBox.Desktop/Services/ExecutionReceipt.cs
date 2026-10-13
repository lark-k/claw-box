using System.Text.Json;
using ClawBox.Contracts;

namespace ClawBox.Desktop.Services;

public static class ExecutionReceipt
{
    private static readonly HashSet<string> UserWrites = ["directory.create", "file.write", "file.copy", "file.move", "file.replace", "word.create", "word.replace", "word.format"];
    public static string Summarize(IReadOnlyList<ExecutorTask> tasks, string workspace)
    {
        if (tasks.Count == 0) return "尚无 Windows 执行器回执，不能确认本地文件已生成。";
        string root = WorkspaceContext.Normalize(workspace) + Path.DirectorySeparatorChar;
        List<string> lines = ["本机执行器回执（以这些记录确认实际执行，不以模型描述代替）："];
        foreach (ExecutorTask task in tasks.OrderBy(t => t.CreatedAt))
        {
            if (task.Status != TaskStates.Succeeded) { lines.Add($"{task.Tool}：{task.Status}，{task.ErrorCode ?? "结果尚未确认"}"); continue; }
            if (task.Result?.TryGetProperty("checksPassed", out JsonElement passed) == true && passed.ValueKind == JsonValueKind.False)
            { lines.Add($"{task.Tool}：检查未通过"); continue; }
            if (!UserWrites.Contains(task.Tool)) { lines.Add($"{task.Tool}：执行完成"); continue; }
            string? path = task.Result?.TryGetProperty("path", out JsonElement output) == true && output.ValueKind == JsonValueKind.String ? output.GetString() : null;
            if (path is null || !Path.IsPathFullyQualified(path) ||
                (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)))
            { lines.Add($"{task.Tool}：回执路径不属于当前 Windows 工作区，未确认文件保存"); continue; }
            lines.Add($"{task.Tool}：本机已执行 → {path}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
