using System.Net;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Files;
using ClawBox.Executor.Persistence;
using ClawBox.Executor.Security;
using ClawBox.Executor.Tasks;
using ClawBox.Executor.Tools;
using ClawBox.Executor.Word;
using Microsoft.AspNetCore.Authentication;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.WebHost.ConfigureKestrel((context, server) =>
{
    ExecutorOptions options = context.Configuration.GetSection(ExecutorOptions.SectionName).Get<ExecutorOptions>() ?? new();
    options.Validate();
    server.Listen(IPAddress.Loopback, options.Port);
    server.Limits.MaxRequestBodySize = options.MaxInputBytes * 6L + 4096;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(provider =>
{
    ExecutorOptions options = provider.GetRequiredService<IConfiguration>().GetSection(ExecutorOptions.SectionName).Get<ExecutorOptions>() ?? new();
    options.Validate();
    return options;
});
builder.Services.AddSingleton<PathPolicy>();
builder.Services.AddSingleton<AtomicFiles>();
builder.Services.AddSingleton<WordDocuments>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ExecutorStore>();
builder.Services.AddHostedService<TaskWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(TokenAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthentication>(TokenAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("worker", policy => policy.RequireRole("worker"))
    .AddPolicy("admin", policy => policy.RequireRole("admin"));

WebApplication app = builder.Build();
using FileStream instanceLease = InstanceLease.Acquire(app.Services.GetRequiredService<ExecutorOptions>().DataDirectory);
app.Services.GetRequiredService<ExecutorStore>().Recover();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    // Browser origins are not an API client. No CORS configuration can relax this boundary.
    if (context.Request.Headers.ContainsKey("Origin"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    try { await next(context); }
    catch (ExecutorException error)
    {
        context.RequestServices.GetRequiredService<ExecutorStore>().RecordRejection(error.Code);
        await Results.Problem(statusCode: error.Status, title: error.Code, detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code }).ExecuteAsync(context);
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "claw-box-executor", version = "0.1.0" }));
RouteGroupBuilder worker = app.MapGroup("/api/v1").RequireAuthorization("worker");
worker.MapGet("/tools", () => ToolRegistry.Catalog);
worker.MapPost("/tasks", (SubmitTaskRequest request, ToolRegistry tools, ExecutorStore store) =>
{
    tools.Validate(request.Call);
    ExecutorTask task = store.Submit(request);
    return Results.Accepted($"/api/v1/tasks/{task.Id}", task);
});
worker.MapGet("/tasks", (ExecutorStore store) => store.List());
worker.MapGet("/tasks/{id:guid}", (Guid id, ExecutorStore store) => store.Get(id) is ExecutorTask task ? Results.Ok(task) : Results.NotFound());
worker.MapPost("/tasks/{id:guid}/cancel", (Guid id, ExecutorStore store) => store.Get(id) is null ? Results.NotFound() : Results.Ok(new { requested = store.Cancel(id) }));
RouteGroupBuilder admin = app.MapGroup("/api/v1").RequireAuthorization("admin");
admin.MapPost("/approvals", (ToolCall call, ToolRegistry tools, ExecutorStore store) => { tools.Validate(call); return Results.Ok(store.Approve(call)); });
admin.MapGet("/audit", (ExecutorStore store) => store.AuditRecords());
app.Run();

public partial class Program;
