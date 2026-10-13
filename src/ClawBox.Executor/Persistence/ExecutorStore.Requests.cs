using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Security;
using Microsoft.Data.Sqlite;

namespace ClawBox.Executor.Persistence;

public sealed partial class ExecutorStore
{
    public ToolRequest Propose(ToolCall call)
    {
        string hash = RequestFingerprint.Compute(call);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExpireRequests(connection, transaction);
        ToolRequest? existing = ReadRequest(connection, transaction, call.RequestId);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw Conflict();
            transaction.Commit();
            return existing;
        }
        using (SqliteCommand task = Query(connection, transaction, "SELECT COUNT(*) FROM tasks WHERE request_id=$id", ("$id", call.RequestId.ToString())))
            if ((long)task.ExecuteScalar()! != 0) throw Conflict();
        using (SqliteCommand count = Query(connection, transaction, "SELECT COUNT(*) FROM requests WHERE status='pending'"))
            if ((long)count.ExecuteScalar()! >= options.MaxPendingRequests)
                throw new ExecutorException("pending_capacity", 429, "Pending approval capacity has been reached.");
        DateTimeOffset created = DateTimeOffset.UtcNow, expires = created.AddMinutes(5);
        Execute(connection, transaction, "INSERT INTO requests(request_id,hash,call_json,status,created,expires) VALUES($id,$hash,$call,'pending',$created,$expires)",
            ("$id", call.RequestId.ToString()), ("$hash", hash), ("$call", JsonSerializer.Serialize(call, Json)),
            ("$created", created.ToString("O")), ("$expires", expires.ToString("O")));
        Audit(connection, transaction, "request_pending", null, call.Tool, hash, null);
        transaction.Commit();
        return new(call, hash, RequestStates.Pending, created, expires, null);
    }

    public ToolRequest? GetRequest(Guid id)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExpireRequests(connection, transaction);
        ToolRequest? request = ReadRequest(connection, transaction, id);
        transaction.Commit();
        return request;
    }

    public IReadOnlyList<PendingToolRequest> PendingRequests()
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExpireRequests(connection, transaction);
        List<PendingToolRequest> result = [];
        using (SqliteCommand command = Query(connection, transaction, "SELECT request_id,json_extract(call_json,'$.tool'),hash,created,expires FROM requests WHERE status='pending' ORDER BY created LIMIT 1000"))
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4))));
        transaction.Commit();
        return result;
    }

    public ToolRequest Decide(Guid id, RequestDecision decision)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExpireRequests(connection, transaction);
        ToolRequest request = ReadRequest(connection, transaction, id) ?? throw Missing();
        if (decision.RequestHash != request.RequestHash) throw Conflict();
        if (request.Status != RequestStates.Pending)
            throw new ExecutorException("request_not_pending", 409, "Only a pending, unexpired request can be decided.");
        ExecutorTask? task = null;
        if (decision.Approve)
        {
            Guid approvalId = Guid.NewGuid();
            Execute(connection, transaction, "INSERT INTO approvals(id,hash,expires) VALUES($id,$hash,$expires)",
                ("$id", approvalId.ToString()), ("$hash", request.RequestHash), ("$expires", request.ExpiresAt.ToString("O")));
            Audit(connection, transaction, "approval_created", null, request.Call.Tool, request.RequestHash, null);
            task = Submit(connection, transaction, new(approvalId, request.Call));
        }
        string state = decision.Approve ? RequestStates.Approved : RequestStates.Rejected;
        Execute(connection, transaction, "UPDATE requests SET status=$status,task_id=$task WHERE request_id=$id",
            ("$status", state), ("$task", task?.Id.ToString()), ("$id", id.ToString()));
        Audit(connection, transaction, "request_" + state, task?.Id, request.Call.Tool, request.RequestHash, null);
        transaction.Commit();
        return request with { Status = state, Task = task };
    }

    public ToolRequest CancelRequest(Guid id)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExpireRequests(connection, transaction);
        ToolRequest request = ReadRequest(connection, transaction, id) ?? throw Missing();
        if (request.Status == RequestStates.Pending)
        {
            Execute(connection, transaction, "UPDATE requests SET status='canceled' WHERE request_id=$id", ("$id", id.ToString()));
            Audit(connection, transaction, "request_canceled", null, request.Call.Tool, request.RequestHash, null);
        }
        else if (request.Task is not null)
        {
            Execute(connection, transaction, "UPDATE tasks SET cancel_requested=1,status=CASE WHEN status='queued' THEN 'canceled' ELSE status END,finished=CASE WHEN status='queued' THEN $at ELSE finished END WHERE id=$id AND status IN ('queued','running')",
                ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", request.Task.Id.ToString()));
            Audit(connection, transaction, "cancel_requested", request.Task.Id, request.Call.Tool, request.RequestHash, null);
        }
        ToolRequest result = ReadRequest(connection, transaction, id)!;
        transaction.Commit();
        return result;
    }

    private static void ExpireRequests(SqliteConnection connection, SqliteTransaction transaction)
    {
        int count = Execute(connection, transaction, "UPDATE requests SET status='expired' WHERE status='pending' AND expires <= $now", ("$now", DateTimeOffset.UtcNow.ToString("O")));
        if (count > 0) Audit(connection, transaction, "requests_expired", null, null, null, "approval_timeout");
    }

    private static ToolRequest? ReadRequest(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        ToolRequest request;
        string? taskId;
        using (SqliteCommand command = Query(connection, transaction, "SELECT call_json,hash,status,created,expires,task_id FROM requests WHERE request_id=$id", ("$id", id.ToString())))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            request = new(JsonSerializer.Deserialize<ToolCall>(reader.GetString(0), Json)!, reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), null);
            taskId = reader.IsDBNull(5) ? null : reader.GetString(5);
        }
        if (taskId is null) return request;
        using SqliteCommand task = Query(connection, transaction, "SELECT * FROM tasks WHERE id=$id", ("$id", taskId));
        using SqliteDataReader taskReader = task.ExecuteReader();
        return request with { Task = taskReader.Read() ? Map(taskReader) : null };
    }

    private static ExecutorException Conflict() => new("request_conflict", 409, "Request ID or digest does not match the original request.");
    private static ExecutorException Missing() => new("request_not_found", 404, "The tool request does not exist.");
}
