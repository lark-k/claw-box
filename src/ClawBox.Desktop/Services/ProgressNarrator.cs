using ClawBox.Contracts;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public static class ProgressNarrator
{
    public static string Describe(ToolCall call, string state, string workspace, ExecutorTask? task = null)
    {
        string action = call.Tool switch
        {
            "file.read" or "word.read" => "读取文件",
            "file.info" => "查看文件信息",
            "file.search" => "搜索文本",
            "file.list" or "directory.list" => "浏览工作区",
            "directory.create" => "创建目录",
            "file.write" => "写入文件",
            "file.replace" or "word.replace" => "替换文件文字",
            "file.copy" => "复制文件",
            "file.move" => "重命名或移动文件",
            "word.create" => "生成 Word 文档",
            "word.format" => "调整文档排版",
            "project.check" => "检查项目",
            "project.command" => "执行 " + (call.Arguments.TryGetProperty("operation", out var operation) ? operation.GetString()!.Replace('.', ' ') : "项目命令"),
            _ => "调用工具"
        };
        string path = FileChanges.Target(call);
        string name;
        try { name = Path.GetRelativePath(workspace, path); } catch (ArgumentException) { name = "当前文件"; }
        if (name == ".") name = "当前工作区";
        string target = action + "：" + ProgressEntry.Concise(name);
        if (task?.Result?.TryGetProperty("checksPassed", out var passed) == true && passed.ValueKind == System.Text.Json.JsonValueKind.False)
            return "项目检查未通过，请查看执行日志。";
        if (task?.Result?.TryGetProperty("commandPassed", out var commandPassed) == true && commandPassed.ValueKind == System.Text.Json.JsonValueKind.False)
            return "项目命令未成功，请查看执行日志。";
        return state switch
        {
            "pending" => "准备" + target + "，等待批准。",
            "approved" => "已批准" + target + "，等待执行。",
            "running" => "正在" + target + "。",
            "queued" => "已排队" + target + "。",
            "succeeded" => "已完成" + target + "。",
            "failed" => "操作失败：" + target + "。",
            "rejected" => "已拒绝" + target + "。",
            "canceled" => "已取消" + target + "。",
            _ => "操作已中断：" + target + "。"
        };
    }
}
