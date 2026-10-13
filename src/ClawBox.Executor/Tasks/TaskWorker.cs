using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Persistence;
using ClawBox.Executor.Tools;

namespace ClawBox.Executor.Tasks;

public sealed class TaskWorker(ExecutorStore store, ToolRegistry tools, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            (Guid Id, ToolCall Call)? job = store.ClaimNext();
            if (job is null)
            {
                await Task.Delay(100, stoppingToken);
                continue;
            }
            try
            {
                if (store.IsCancellationRequested(job.Value.Id))
                {
                    store.Finish(job.Value.Id, TaskStates.Canceled);
                    continue;
                }
                var result = tools.Execute(job.Value.Call, stoppingToken);
                // Once committed, report success even if a late cancel arrived: cancel does not undo writes.
                store.Finish(job.Value.Id, TaskStates.Succeeded, result);
            }
            catch (OperationCanceledException) { store.Finish(job.Value.Id, TaskStates.Interrupted, error: "executor_stopping"); }
            catch (Exception error)
            {
                string code = error switch
                {
                    ExecutorException known => known.Code,
                    FileNotFoundException => "file_not_found",
                    DirectoryNotFoundException => "directory_missing",
                    UnauthorizedAccessException => "access_denied",
                    IOException => "file_io_error",
                    _ => "tool_failed"
                };
                store.Finish(job.Value.Id, TaskStates.Failed, error: code);
                logger.LogWarning("Task {TaskId} failed with {ErrorCode}", job.Value.Id, code);
            }
        }
    }
}
