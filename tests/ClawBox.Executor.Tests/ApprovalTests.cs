using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Persistence;
using Microsoft.Data.Sqlite;

namespace ClawBox.Executor.Tests;

public sealed class ApprovalTests
{
    private static ToolCall Call(Guid id, string content) => new(id, "file.write", JsonSerializer.SerializeToElement(new { path = "unused", content }));

    [Fact]
    public void Approval_cannot_authorize_changed_arguments_and_retry_is_idempotent()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid(), "approved");
        ApprovalResponse approval = store.Approve(call);
        Assert.Throws<ExecutorException>(() => store.Submit(new(approval.ApprovalId, Call(call.RequestId, "tampered"))));
        ExecutorTask first = store.Submit(new(approval.ApprovalId, call));
        Assert.Equal(first.Id, store.Submit(new(approval.ApprovalId, call)).Id);
        Assert.Single(store.List());
        Assert.Throws<ExecutorException>(() => store.Submit(new(approval.ApprovalId, Call(Guid.NewGuid(), "approved"))));
    }

    [Fact]
    public void Recovery_preserves_completed_work_and_does_not_replay_pending_mutations()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid(), "one");
        ExecutorTask completed = store.Submit(new(store.Approve(call).ApprovalId, call));
        Assert.Equal(completed.Id, store.ClaimNext()!.Value.Id);
        store.Finish(completed.Id, TaskStates.Succeeded, JsonSerializer.SerializeToElement(new { bytes = 3 }));
        ToolCall pending = Call(Guid.NewGuid(), "two");
        ExecutorTask queued = store.Submit(new(store.Approve(pending).ApprovalId, pending));
        new ExecutorStore(temp.Options).Recover();
        Assert.Equal(TaskStates.Succeeded, store.Get(completed.Id)!.Status);
        Assert.Equal(TaskStates.Interrupted, store.Get(queued.Id)!.Status);
        Assert.Null(store.ClaimNext());
    }

    [Fact]
    public void Queued_cancellation_prevents_claim_and_capacity_does_not_consume_approval()
    {
        using TestWorkspace temp = new();
        temp.Options.MaxQueuedTasks = 1;
        ExecutorStore store = new(temp.Options);
        ToolCall a = Call(Guid.NewGuid(), "one"), b = Call(Guid.NewGuid(), "two");
        ExecutorTask first = store.Submit(new(store.Approve(a).ApprovalId, a));
        ApprovalResponse second = store.Approve(b);
        Assert.Equal("queue_full", Assert.Throws<ExecutorException>(() => store.Submit(new(second.ApprovalId, b))).Code);
        Assert.True(store.Cancel(first.Id));
        Assert.Equal(TaskStates.Canceled, store.Get(first.Id)!.Status);
        Assert.Equal(TaskStates.Queued, store.Submit(new(second.ApprovalId, b)).Status);
    }

    [Fact]
    public void Concurrent_retries_create_only_one_task()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid(), "one");
        ApprovalResponse approval = store.Approve(call);
        Guid[] ids = new Guid[12];
        Parallel.For(0, ids.Length, i => ids[i] = store.Submit(new(approval.ApprovalId, call)).Id);
        Assert.Single(ids.Distinct());
        Assert.Single(store.List());
    }

    [Fact]
    public void Expired_approval_is_rejected_and_newer_schema_is_not_overwritten()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid(), "one");
        ApprovalResponse approval = store.Approve(call);
        using SqliteConnection connection = new("Data Source=" + Path.Combine(temp.Options.DataDirectory, "executor.sqlite"));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE approvals SET expires='2000-01-01T00:00:00+00:00' WHERE id=$id";
        command.Parameters.AddWithValue("$id", approval.ApprovalId.ToString());
        command.ExecuteNonQuery();
        Assert.Throws<ExecutorException>(() => store.Submit(new(approval.ApprovalId, call)));
        command.CommandText = "PRAGMA user_version=3";
        command.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => new ExecutorStore(temp.Options));
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, command.ExecuteScalar());
    }
}
