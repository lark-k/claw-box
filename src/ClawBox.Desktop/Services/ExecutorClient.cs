using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClawBox.Contracts;

namespace ClawBox.Desktop.Services;

public sealed class ExecutorClient : IDisposable
{
    private readonly HttpClient admin;
    private readonly HttpClient worker;
    public ExecutorClient(string adminToken, string workerToken, int port = 18790, HttpMessageHandler? handler = null)
    {
        admin = Make(adminToken); worker = Make(workerToken);
        HttpClient Make(string token)
        {
            HttpClient client = handler is null ? new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) : new(handler, disposeHandler: false);
            client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
    public async Task<ToolRequest[]> Pending(CancellationToken cancellation)
    {
        PendingToolRequest[] summaries = await admin.GetFromJsonAsync<PendingToolRequest[]>("api/v1/requests", cancellation) ?? [];
        List<ToolRequest> requests = [];
        foreach (PendingToolRequest summary in summaries)
        {
            ToolRequest? request = await admin.GetFromJsonAsync<ToolRequest>($"api/v1/requests/{summary.RequestId}", cancellation);
            if (request?.Status == RequestStates.Pending) requests.Add(request);
        }
        return requests.ToArray();
    }
    public async Task Decide(ToolRequest request, bool approve, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await admin.PostAsJsonAsync($"api/v1/requests/{request.Call.RequestId}/decision",
            new RequestDecision(request.RequestHash, approve), cancellation);
        response.EnsureSuccessStatusCode();
    }
    public async Task<ExecutorTask[]> Tasks(CancellationToken cancellation) =>
        await worker.GetFromJsonAsync<ExecutorTask[]>("api/v1/tasks", cancellation) ?? [];
    public async Task Cancel(CancellationToken cancellation)
    {
        foreach (ToolRequest request in await Pending(cancellation))
            using (HttpResponseMessage response = await worker.PostAsJsonAsync($"api/v1/requests/{request.Call.RequestId}/cancel", new { }, cancellation)) response.EnsureSuccessStatusCode();
        foreach (ExecutorTask task in await Tasks(cancellation))
            if (task.Status is TaskStates.Queued or TaskStates.Running)
                using (HttpResponseMessage response = await worker.PostAsJsonAsync($"api/v1/tasks/{task.Id}/cancel", new { }, cancellation)) response.EnsureSuccessStatusCode();
    }
    public void Dispose() { admin.Dispose(); worker.Dispose(); }
}
