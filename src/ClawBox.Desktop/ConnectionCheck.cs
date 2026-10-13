using System.Text.Json;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop;

public static class ConnectionCheck
{
    // Diagnostic only: no conversation is sent, no tool request is approved or replayed.
    public static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath) || File.Exists(reportPath)) return 2;
        bool success = false;
        string? errorType = null;
        try
        {
            LocalState state = new();
            DesktopSettings settings = state.Load("settings.json", () => new DesktopSettings());
            Task.Run(async () =>
            {
                using RuntimeSession runtime = new();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(35));
                await runtime.Start(settings, timeout.Token);
                if (!runtime.Alive) throw new InvalidOperationException("后台连接未就绪。");
            }).GetAwaiter().GetResult();
            success = true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Do not log arbitrary exception text: subprocesses and credential paths are private.
            errorType = error.GetType().Name;
        }
        try
        {
            using FileStream file = new(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new { success, message = success ? "配对文件、执行器及 SSH 网关连接正常。未发送任务。" : "连接诊断失败，请在 APP 查看连接状态。", errorType });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
        return success ? 0 : 1;
    }
}
