using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClawBox.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ClawBox.Executor.Tests;

public sealed class ApiTests
{
    private sealed class Factory(TestWorkspace temp) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Executor:DataDirectory"] = temp.Options.DataDirectory,
                ["Executor:AllowedRoots:0"] = temp.Workspace,
                ["Executor:AdminToken"] = temp.Options.AdminToken,
                ["Executor:WorkerToken"] = temp.Options.WorkerToken,
            }));
    }

    private static HttpClient Client(Factory factory, string token)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Authentication_and_approval_roles_are_separated()
    {
        using TestWorkspace temp = new();
        using Factory factory = new(temp);
        using HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tools")).StatusCode);
        using HttpClient worker = Client(factory, temp.Options.WorkerToken);
        ToolCall call = new(Guid.NewGuid(), "file.read", JsonSerializer.SerializeToElement(new { path = temp.FilePath("a.txt") }));
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsJsonAsync("/api/v1/approvals", call)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsJsonAsync("/api/v1/tasks", new SubmitTaskRequest(Guid.NewGuid(), call))).StatusCode);
        worker.DefaultRequestHeaders.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/tools")).StatusCode);
    }

    [Fact]
    public async Task Authorized_write_runs_once_and_read_returns_actual_content()
    {
        using TestWorkspace temp = new();
        using Factory factory = new(temp);
        using HttpClient worker = Client(factory, temp.Options.WorkerToken);
        using HttpClient admin = Client(factory, temp.Options.AdminToken);
        ToolCall call = new(Guid.NewGuid(), "file.write", JsonSerializer.SerializeToElement(new { path = temp.FilePath("a.txt"), content = "企业级执行器验证" }));
        ApprovalResponse approval = (await (await admin.PostAsJsonAsync("/api/v1/approvals", call)).Content.ReadFromJsonAsync<ApprovalResponse>())!;
        SubmitTaskRequest request = new(approval.ApprovalId, call);
        HttpResponseMessage response = await worker.PostAsJsonAsync("/api/v1/tasks", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        ExecutorTask task = (await response.Content.ReadFromJsonAsync<ExecutorTask>())!;
        ExecutorTask completed = await AwaitTask(worker, task.Id);
        Assert.Equal(TaskStates.Succeeded, completed.Status);
        Assert.Equal("企业级执行器验证", await File.ReadAllTextAsync(temp.FilePath("a.txt")));
        ExecutorTask retry = (await (await worker.PostAsJsonAsync("/api/v1/tasks", request)).Content.ReadFromJsonAsync<ExecutorTask>())!;
        Assert.Equal(task.Id, retry.Id);
        ToolCall read = new(Guid.NewGuid(), "file.read", JsonSerializer.SerializeToElement(new { path = temp.FilePath("a.txt") }));
        ApprovalResponse readApproval = (await (await admin.PostAsJsonAsync("/api/v1/approvals", read)).Content.ReadFromJsonAsync<ApprovalResponse>())!;
        ExecutorTask readTask = (await (await worker.PostAsJsonAsync("/api/v1/tasks", new SubmitTaskRequest(readApproval.ApprovalId, read))).Content.ReadFromJsonAsync<ExecutorTask>())!;
        ExecutorTask readDone = await AwaitTask(worker, readTask.Id);
        Assert.Equal("企业级执行器验证", readDone.Result!.Value.GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Invalid_paths_return_explicit_problem_details()
    {
        using TestWorkspace temp = new();
        using Factory factory = new(temp);
        using HttpClient admin = Client(factory, temp.Options.AdminToken);
        ToolCall call = new(Guid.NewGuid(), "file.read", JsonSerializer.SerializeToElement(new { path = Path.Combine(temp.Root, "outside.txt") }));
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/v1/approvals", call);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("path_denied", problem.GetProperty("code").GetString());
    }

    private static async Task<ExecutorTask> AwaitTask(HttpClient client, Guid id)
    {
        for (int i = 0; i < 100; i++)
        {
            ExecutorTask task = (await client.GetFromJsonAsync<ExecutorTask>($"/api/v1/tasks/{id}"))!;
            if (task.Status is not (TaskStates.Queued or TaskStates.Running)) return task;
            await Task.Delay(50);
        }
        throw new TimeoutException("Executor task did not finish.");
    }
}
