using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

public sealed class ClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    [Fact]
    public async Task Streaming_preserves_session_and_uses_gateway_secret_only()
    {
        Guid conversation = Guid.NewGuid();
        int sends = 0;
        using Handler handler = new(request =>
        {
            sends++;
            Assert.Equal("http://127.0.0.1:18889/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("gateway-fixture", request.Headers.Authorization!.Parameter);
            Assert.Equal(WorkspaceContext.Key(conversation, @"C:\workspace"), request.Headers.GetValues("x-openclaw-session-key").Single());
            using JsonDocument body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("openclaw:quantum-harness", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Contains("tool_search", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("user", body.RootElement.GetProperty("messages")[2].GetProperty("role").GetString());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"真实\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"结果\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        using GatewayClient client = new("gateway-fixture", handler: handler);
        StringBuilder answer = new();
        await client.Send(conversation, "读取已选文件", @"C:\workspace", text => answer.Append(text), CancellationToken.None);
        Assert.Equal("真实结果", answer.ToString());
        Assert.Equal(1, sends);
    }
    [Fact]
    public async Task Missing_completion_is_unknown_and_never_retried()
    {
        int sends = 0;
        using Handler handler = new(_ => { sends++; return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[]}\n\n") }; });
        using GatewayClient client = new("fixture", handler: handler);
        await Assert.ThrowsAsync<IOException>(() => client.Send(Guid.NewGuid(), "任务", @"C:\workspace", _ => { }, CancellationToken.None));
        Assert.Equal(1, sends);
    }
    [Fact]
    public async Task Http_error_body_never_enters_chat_or_exception()
    {
        using Handler handler = new(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("private-secret-do-not-display") });
        using GatewayClient client = new("fixture", handler: handler);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.Send(Guid.NewGuid(), "任务", @"C:\workspace", _ => { }, CancellationToken.None));
        Assert.DoesNotContain("private-secret", error.Message);
        Assert.Contains("401", error.Message);
    }
    [Fact]
    public async Task Approval_submits_the_displayed_hash_with_admin_credential()
    {
        ToolRequest request = new(new(Guid.NewGuid(), "file.write", JsonSerializer.SerializeToElement(new { path = "C:\\workspace\\a.txt", content = "正文" })),
            new string('F', 64), "pending", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), null);
        using Handler handler = new(message =>
        {
            Assert.Equal("admin-fixture", message.Headers.Authorization!.Parameter);
            Assert.EndsWith($"/requests/{request.Call.RequestId}/decision", message.RequestUri!.AbsolutePath);
            using JsonDocument body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(request.RequestHash, body.RootElement.GetProperty("requestHash").GetString());
            Assert.True(body.RootElement.GetProperty("approve").GetBoolean());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            return new(HttpStatusCode.OK);
        });
        using ExecutorClient client = new("admin-fixture", "worker-fixture", handler: handler);
        await client.Decide(request, true, CancellationToken.None);
    }
    [Fact]
    public void Approval_display_preserves_Chinese_and_escapes_bidi_controls()
    {
        ToolRequest request = new(new(Guid.NewGuid(), "file.read", JsonSerializer.SerializeToElement(new { path = "C:\\工作区\\a\u202Etxt.docx" })),
            new string('A', 64), "pending", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), null);
        ApprovalCard card = new(request);
        Assert.Contains("工作区", card.Parameters);
        Assert.Contains("\\u202e", card.Parameters);
        Assert.False(card.Summary.Contains('\u202E'));
        Assert.Same(request, card.Request);
        card.Deciding = true;
        Assert.False(card.CanDecide);
    }
    [Fact]
    public void Corrupt_local_history_is_preserved_and_not_silently_replaced()
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            LocalState state = new(root);
            state.Save("history.json", new[] { "saved" });
            Assert.Equal("saved", state.Load("history.json", () => Array.Empty<string>()).Single());
            File.WriteAllText(Path.Combine(root, "history.json"), "{bad}");
            Assert.Throws<InvalidOperationException>(() => state.Load("history.json", () => Array.Empty<string>()));
            Assert.Equal("{bad}", File.ReadAllText(Path.Combine(root, "history.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
