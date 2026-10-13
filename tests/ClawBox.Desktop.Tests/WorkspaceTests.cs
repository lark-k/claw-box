using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class WorkspaceTests
{
    private sealed class Handler(List<string> workspaces, List<string> keys) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            keys.Add(request.Headers.GetValues("x-openclaw-session-key").Single());
            Assert.Equal(WorkspaceContext.AgentId, request.Headers.GetValues("x-openclaw-agent-id").Single());
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("openclaw:quantum-harness", body.RootElement.GetProperty("model").GetString());
            string context = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Contains("APP 当前授权的 Windows 工作区", body.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
            workspaces.Add(context);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") };
        }
    }
    [Fact]
    public async Task Selected_project_is_sent_on_every_turn_and_switching_it_changes_remote_session()
    {
        List<string> contexts = []; List<string> keys = []; Guid conversation = Guid.NewGuid();
        using Handler handler = new(contexts, keys); using GatewayClient client = new("fixture", handler: handler);
        await client.Send(conversation, "创建报告", @"D:\Desktop\test", _ => { }, CancellationToken.None);
        await client.Send(conversation, "再创建一个文件夹", @"D:\Desktop\test", _ => { }, CancellationToken.None);
        await client.Send(conversation, "创建另一份报告", @"D:\Desktop\other", _ => { }, CancellationToken.None);
        Assert.All(contexts.Take(2), text => Assert.Contains("D:\\\\Desktop\\\\test", text));
        Assert.Contains("D:\\\\Desktop\\\\other", contexts[2]); Assert.Equal(keys[0], keys[1]); Assert.NotEqual(keys[1], keys[2]);
        Assert.All(keys, key => Assert.StartsWith("agent:quantum-harness:", key));
        Assert.Equal(WorkspaceContext.Key(conversation, @"D:\Desktop\test"), WorkspaceContext.Key(conversation, @"d:\desktop\test\"));
    }
    [Fact]
    public void Missing_local_result_or_a_box_path_never_counts_as_a_confirmed_windows_file()
    {
        Assert.Contains("不能确认", ExecutionReceipt.Summarize([], @"D:\Desktop\test"));
        ExecutorTask task = new(Guid.NewGuid(), Guid.NewGuid(), "file.write", TaskStates.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { path = "/root/.openclaw/workspace/output/report.txt" }), null);
        Assert.DoesNotContain("本机已执行", ExecutionReceipt.Summarize([task], @"D:\Desktop\test"));
        task = task with { Result = JsonSerializer.SerializeToElement(new { path = @"D:\Desktop\test\report.txt" }) };
        Assert.Contains(@"本机已执行 → D:\Desktop\test\report.txt", ExecutionReceipt.Summarize([task], @"D:\Desktop\test"));
        task = task with { Result = JsonSerializer.SerializeToElement(new { path = @"D:\Desktop\test-other\report.txt" }) };
        Assert.DoesNotContain("本机已执行", ExecutionReceipt.Summarize([task], @"D:\Desktop\test"));
    }
    [Fact]
    public void Failed_and_failed_checks_are_not_presented_as_completed_work()
    {
        ExecutorTask task = new(Guid.NewGuid(), Guid.NewGuid(), "file.write", TaskStates.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "path_denied");
        Assert.Contains("path_denied", ExecutionReceipt.Summarize([task], @"D:\workspace"));
        task = task with { Tool = "project.check", Status = TaskStates.Succeeded, Result = JsonSerializer.SerializeToElement(new { checksPassed = false }) };
        Assert.Contains("检查未通过", ExecutionReceipt.Summarize([task], @"D:\workspace"));
    }
}
