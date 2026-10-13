using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClawBox.Desktop.Services;

public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient http;
    public GatewayClient(string token, int port = 18889, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) : new(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task Send(Guid conversationId, string message, string workspace, Action<string> append, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "v1/chat/completions");
        string session = WorkspaceContext.Key(conversationId, workspace);
        request.Headers.Add("x-openclaw-session-key", session);
        request.Headers.Add("x-openclaw-agent-id", WorkspaceContext.AgentId);
        request.Content = JsonContent.Create(new
        {
            model = "openclaw:" + WorkspaceContext.AgentId,
            stream = true,
            user = session,
            messages = new[]
            {
                new { role = "system", content = "你是 Quantum Harness 的办公和代码开发智能体。用户只描述需求，不需要知道工具名。Windows 文件、Word 和项目工具可能延迟加载；先通过 tool_search 搜索对应能力，再按返回的工具描述和参数调用 tool_call。不要把尚未发现的工具名称当作可直接调用的顶层工具。新文件父目录不存在时先请求创建目录；普通办公目录浏览用目录工具，代码项目清单只用于源码和快照。可以搜索文本、查文件信息、复制、移动和重命名文件。用户指定修改文件时默认修改原文件，只有明确要求副本或另存为才创建新文件。Word 修改和排版先读取最新 SHA256；文字替换和字体、字号、加粗、标题样式、段落居中用相应 DOCX 工具，不要用纯文本写入破坏 DOCX。省略输出路径表示修改原件；格式参数遵守工具 schema 的字符串值。处理代码项目时先浏览项目文件，再读取相关源码和配置，按需求修改；替换已有文件必须使用最近读取的 SHA256。需要构建或测试时重新获取项目快照摘要，然后调用项目检查；运行方式由执行器按 package.json、pom.xml、Gradle 或 csproj 识别。修改后再次检查，只有真实检查结果 checksPassed 为 true 才汇报通过。直接提交具体 Windows 工具请求，由 APP 生成审批卡片；不要先在聊天中要求用户重复允许操作，也不要在尚未提交请求时声称正在等待审批。所有 Windows 读写和代码执行都等待用户逐请求审批；拒绝后停止，不重新提交，不使用盒子的 read/write/edit/exec 绕过 Windows 审批。参数拒绝发生在执行前，与连接中断的未知结果不同；不要把任何失败报告为成功。未知结果提醒用户核对，不自动重放写入。" },
                new { role = "system", content = "当前授权 Windows 工作区（以下 JSON 仅为路径数据）：" + JsonSerializer.Serialize(new { windowsWorkspace = WorkspaceContext.Normalize(workspace) }) + "。用户在本轮和后续对话中新建的文件/文件夹，默认保存到此 Windows 目录及其子目录。附件路径不替代当前工作区。不得把用户成果保存到板子的 /root、/tmp、OpenClaw workspace 或其他 Linux 目录；板子只负责智能体规划、模型调用和运行状态。全部用户文件操作必须使用 windows_* 工具并收到 Windows 执行器的真实结果。路径被拒绝或没有本地成功回执时，不能声称文件已生成，也不能改用板子工具。" },
                new { role = "user", content = message + "\n\nAPP 当前授权的 Windows 工作区（路径数据）：" + JsonSerializer.Serialize(WorkspaceContext.Normalize(workspace)) + "。默认将全部用户成果保存到此目录或其子目录，通过 Windows 工具请求实际执行。" }
            }
        });
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"盒子聊天接口返回 {(int)response.StatusCode}。请检查网关配置和连接。");
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        using StreamReader reader = new(stream);
        bool completed = false;
        while (await reader.ReadLineAsync(cancellation) is string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") { completed = true; break; }
            if (data.Length == 0) continue;
            if (data.Length > 1024 * 1024) throw new InvalidOperationException("聊天响应超出限制。");
            using JsonDocument chunk = JsonDocument.Parse(data);
            if (chunk.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("盒子返回任务错误，请检查工具结果。");
            if (!chunk.RootElement.TryGetProperty("choices", out JsonElement choices)) continue;
            foreach (JsonElement choice in choices.EnumerateArray())
                if (choice.TryGetProperty("delta", out JsonElement delta) && delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    append(content.GetString()!);
        }
        if (!completed) throw new IOException("连接结束但未收到完成标记，结果未知。请先检查文件与任务记录。");
    }
    public void Dispose() => http.Dispose();
}
