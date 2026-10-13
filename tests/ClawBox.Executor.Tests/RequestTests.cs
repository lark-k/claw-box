using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Persistence;
using Microsoft.Data.Sqlite;

namespace ClawBox.Executor.Tests;

public sealed class RequestTests
{
    private static ToolCall Call(Guid id, string content = "sample") => new(id, "file.write",
        JsonSerializer.SerializeToElement(new { path = "unused", content }));

    [Fact]
    public void Proposal_is_inert_until_exact_local_decision_and_retries_never_replay()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid());
        ToolRequest request = store.Propose(call);
        Assert.Empty(store.List());
        Assert.Null(store.ClaimNext());
        Assert.Equal(request.RequestHash, store.Propose(call).RequestHash);
        Assert.Equal("request_conflict", Assert.Throws<ExecutorException>(() => store.Propose(Call(call.RequestId, "changed"))).Code);
        Assert.Throws<ExecutorException>(() => store.Decide(call.RequestId, new("wrong", true)));
        Assert.Empty(store.List());
        ToolRequest approved = store.Decide(call.RequestId, new(request.RequestHash, true));
        Assert.Equal(RequestStates.Approved, approved.Status);
        Assert.Equal(approved.Task!.Id, store.Propose(call).Task!.Id);
        Assert.Throws<ExecutorException>(() => store.Decide(call.RequestId, new(request.RequestHash, true)));
        Assert.Single(store.List());
        Assert.Empty(store.PendingRequests());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("canceled")]
    [InlineData("expired")]
    [InlineData("interrupted")]
    public void Terminal_requests_cannot_be_approved_or_recreated(string state)
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid());
        ToolRequest request = store.Propose(call);
        if (state == "rejected") store.Decide(call.RequestId, new(request.RequestHash, false));
        if (state == "canceled") store.CancelRequest(call.RequestId);
        if (state == "interrupted") store.Recover();
        if (state == "expired")
        {
            using SqliteConnection connection = new("Data Source=" + Path.Combine(temp.Options.DataDirectory, "executor.sqlite"));
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE requests SET expires='2000-01-01T00:00:00+00:00'";
            command.ExecuteNonQuery();
        }
        Assert.Equal(state, store.GetRequest(call.RequestId)!.Status);
        Assert.Throws<ExecutorException>(() => store.Decide(call.RequestId, new(request.RequestHash, true)));
        Assert.Equal(state, store.Propose(call).Status);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Capacity_and_concurrent_decisions_preserve_atomicity()
    {
        using TestWorkspace temp = new();
        temp.Options.MaxPendingRequests = temp.Options.MaxQueuedTasks = 1;
        ExecutorStore store = new(temp.Options);
        ToolRequest first = store.Propose(Call(Guid.NewGuid()));
        Assert.Equal("pending_capacity", Assert.Throws<ExecutorException>(() => store.Propose(Call(Guid.NewGuid()))).Code);
        int decisions = 0;
        Parallel.For(0, 8, _ =>
        {
            try { store.Decide(first.Call.RequestId, new(first.RequestHash, true)); Interlocked.Increment(ref decisions); }
            catch (ExecutorException error) when (error.Code == "request_not_pending") { }
        });
        Assert.Equal(1, decisions);
        ToolRequest second = store.Propose(Call(Guid.NewGuid()));
        Assert.Equal("queue_full", Assert.Throws<ExecutorException>(() => store.Decide(second.Call.RequestId, new(second.RequestHash, true))).Code);
        Assert.Equal(RequestStates.Pending, store.GetRequest(second.Call.RequestId)!.Status);
        store.CancelRequest(first.Call.RequestId);
        Assert.Equal(TaskStates.Canceled, store.GetRequest(first.Call.RequestId)!.Task!.Status);
        Assert.NotNull(store.Decide(second.Call.RequestId, new(second.RequestHash, true)).Task);
    }

    [Fact]
    public void Version_one_migrates_preserving_tasks_and_restart_does_not_requeue_approved_work()
    {
        using TestWorkspace temp = new();
        ExecutorStore store = new(temp.Options);
        ToolCall call = Call(Guid.NewGuid());
        ExecutorTask legacy = store.Submit(new(store.Approve(call).ApprovalId, call));
        using (SqliteConnection connection = new("Data Source=" + Path.Combine(temp.Options.DataDirectory, "executor.sqlite")))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE requests; PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        store = new(temp.Options);
        Assert.Equal(legacy.Id, store.Get(legacy.Id)!.Id);
        Assert.Throws<ExecutorException>(() => store.Propose(call));
        ToolRequest request = store.Propose(Call(Guid.NewGuid()));
        store.Decide(request.Call.RequestId, new(request.RequestHash, true));
        store.Recover();
        Assert.Equal(TaskStates.Interrupted, store.GetRequest(request.Call.RequestId)!.Task!.Status);
        Assert.Equal(TaskStates.Interrupted, store.Propose(request.Call).Task!.Status);
        Assert.Null(store.ClaimNext());
    }
}
