using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Security;
using Microsoft.Data.Sqlite;

namespace ClawBox.Executor.Persistence;

public sealed partial class ExecutorStore
{
    private readonly string connectionString;
    private readonly ExecutorOptions options;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ExecutorStore(ExecutorOptions options)
    {
        this.options = options;
        Directory.CreateDirectory(options.DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, "executor.sqlite"),
            DefaultTimeout = 10,
            Pooling = true
        }.ToString();
        using SqliteConnection connection = Open();
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > 2)
                throw new InvalidOperationException("Database schema is newer than this executor. Use a compatible release.");
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        command.ExecuteNonQuery();
        using SqliteTransaction migration = connection.BeginTransaction(deferred: false);
        command.Transaction = migration;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS approvals(id TEXT PRIMARY KEY, hash TEXT NOT NULL, expires TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, hash TEXT NOT NULL,
                tool TEXT NOT NULL, call_json TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL,
                finished TEXT, result_json TEXT, error_code TEXT, cancel_requested INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, event TEXT NOT NULL,
                task_id TEXT, tool TEXT, request_hash TEXT, error_code TEXT);
            CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status,created);
            CREATE TABLE IF NOT EXISTS requests(request_id TEXT PRIMARY KEY, hash TEXT NOT NULL,
                call_json TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL, expires TEXT NOT NULL, task_id TEXT);
            CREATE INDEX IF NOT EXISTS ix_requests_status ON requests(status,created);
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
        migration.Commit();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        return connection;
    }

    public ApprovalResponse Approve(ToolCall call)
    {
        Guid id = Guid.NewGuid();
        string hash = RequestFingerprint.Compute(call);
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(5);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "INSERT INTO approvals(id,hash,expires) VALUES($id,$hash,$expires)",
            ("$id", id.ToString()), ("$hash", hash), ("$expires", expires.ToString("O")));
        Audit(connection, transaction, "approval_created", null, call.Tool, hash, null);
        transaction.Commit();
        return new(id, expires, hash);
    }

    public ExecutorTask Submit(SubmitTaskRequest request)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExecutorTask task = Submit(connection, transaction, request);
        transaction.Commit();
        return task;
    }

    private ExecutorTask Submit(SqliteConnection connection, SqliteTransaction transaction, SubmitTaskRequest request)
    {
        string hash = RequestFingerprint.Compute(request.Call);
        using (SqliteCommand existing = Query(connection, transaction, "SELECT * FROM tasks WHERE request_id=$id", ("$id", request.Call.RequestId.ToString())))
        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(reader.GetOrdinal("hash")) != hash)
                    throw new ExecutorException("request_conflict", 409, "Request ID was already used with different arguments.");
                return Map(reader);
            }
        }
        using (SqliteCommand grant = Query(connection, transaction, "SELECT * FROM approvals WHERE id=$id", ("$id", request.ApprovalId.ToString())))
        using (SqliteDataReader reader = grant.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(1) != hash || reader.GetInt32(3) != 0 || DateTimeOffset.Parse(reader.GetString(2)) <= DateTimeOffset.UtcNow)
                throw new ExecutorException("approval_required", 403, "An unexpired approval for this exact request is required.");
        }
        using (SqliteCommand count = Query(connection, transaction, "SELECT COUNT(*) FROM tasks WHERE status IN ('queued','running')"))
            if ((long)count.ExecuteScalar()! >= options.MaxQueuedTasks)
                throw new ExecutorException("queue_full", 429, "Task capacity has been reached.");
        Guid id = Guid.NewGuid();
        DateTimeOffset created = DateTimeOffset.UtcNow;
        Execute(connection, transaction, "INSERT INTO tasks(id,request_id,hash,tool,call_json,status,created) VALUES($id,$request,$hash,$tool,$call,'queued',$created)",
            ("$id", id.ToString()), ("$request", request.Call.RequestId.ToString()), ("$hash", hash), ("$tool", request.Call.Tool),
            ("$call", JsonSerializer.Serialize(request.Call, Json)), ("$created", created.ToString("O")));
        Execute(connection, transaction, "UPDATE approvals SET used=1 WHERE id=$id", ("$id", request.ApprovalId.ToString()));
        Audit(connection, transaction, "task_queued", id, request.Call.Tool, hash, null);
        return new(id, request.Call.RequestId, request.Call.Tool, TaskStates.Queued, created, null, null, null);
    }

    public ExecutorTask? Get(Guid id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Query(connection, null, "SELECT * FROM tasks WHERE id=$id", ("$id", id.ToString()));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ExecutorTask> List()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Query(connection, null, "SELECT * FROM tasks ORDER BY created DESC LIMIT 100");
        using SqliteDataReader reader = command.ExecuteReader();
        List<ExecutorTask> tasks = [];
        while (reader.Read()) tasks.Add(Map(reader));
        return tasks;
    }

    public (Guid Id, ToolCall Call)? ClaimNext()
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Guid id;
        ToolCall call;
        using (SqliteCommand command = Query(connection, transaction, "SELECT id,call_json FROM tasks WHERE status='queued' ORDER BY created LIMIT 1"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            id = Guid.Parse(reader.GetString(0));
            call = JsonSerializer.Deserialize<ToolCall>(reader.GetString(1), Json)!;
        }
        Execute(connection, transaction, "UPDATE tasks SET status='running' WHERE id=$id", ("$id", id.ToString()));
        Audit(connection, transaction, "task_running", id, call.Tool, RequestFingerprint.Compute(call), null);
        transaction.Commit();
        return (id, call);
    }

    public void Finish(Guid id, string state, JsonElement? result = null, string? error = null)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "UPDATE tasks SET status=$state,finished=$at,result_json=$result,error_code=$error WHERE id=$id AND status='running'",
            ("$state", state), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$result", result?.GetRawText()), ("$error", error), ("$id", id.ToString()));
        Audit(connection, transaction, "task_" + state, id, null, null, error);
        transaction.Commit();
    }

    public bool Cancel(Guid id)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        int changed = Execute(connection, transaction, "UPDATE tasks SET cancel_requested=1, status=CASE WHEN status='queued' THEN 'canceled' ELSE status END, finished=CASE WHEN status='queued' THEN $at ELSE finished END WHERE id=$id AND status IN ('queued','running')",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", id.ToString()));
        if (changed != 0) Audit(connection, transaction, "cancel_requested", id, null, null, null);
        transaction.Commit();
        return changed != 0;
    }

    public bool IsCancellationRequested(Guid id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Query(connection, null, "SELECT cancel_requested FROM tasks WHERE id=$id", ("$id", id.ToString()));
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public void Recover()
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        int requests = Execute(connection, transaction, "UPDATE requests SET status='interrupted' WHERE status='pending'");
        if (requests > 0) Audit(connection, transaction, "requests_interrupted", null, null, null, "executor_restarted");
        int count = Execute(connection, transaction, "UPDATE tasks SET status='interrupted',finished=$at,error_code='executor_restarted' WHERE status IN ('running','queued')", ("$at", DateTimeOffset.UtcNow.ToString("O")));
        if (count > 0) Audit(connection, transaction, "restart_recovery", null, null, null, "executor_restarted");
        transaction.Commit();
    }

    public void RecordRejection(string code)
    {
        using SqliteConnection connection = Open();
        Audit(connection, null, "request_rejected", null, null, null, code);
    }

    public IReadOnlyList<object> AuditRecords()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Query(connection, null, "SELECT sequence,at,event,task_id,tool,error_code FROM audit ORDER BY sequence DESC LIMIT 200");
        using SqliteDataReader reader = command.ExecuteReader();
        List<object> records = [];
        while (reader.Read()) records.Add(new
        {
            sequence = reader.GetInt64(0),
            at = reader.GetString(1),
            eventName = reader.GetString(2),
            taskId = reader.IsDBNull(3) ? null : reader.GetString(3),
            tool = reader.IsDBNull(4) ? null : reader.GetString(4),
            errorCode = reader.IsDBNull(5) ? null : reader.GetString(5)
        });
        return records;
    }

    private static ExecutorTask Map(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("request_id"))), reader.GetString(reader.GetOrdinal("tool")), reader.GetString(reader.GetOrdinal("status")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created"))),
        reader.IsDBNull(reader.GetOrdinal("finished")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("finished"))),
        reader.IsDBNull(reader.GetOrdinal("result_json")) ? null : JsonDocument.Parse(reader.GetString(reader.GetOrdinal("result_json"))).RootElement.Clone(),
        reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")));

    private static void Audit(SqliteConnection connection, SqliteTransaction? transaction, string name, Guid? task, string? tool, string? hash, string? code) =>
        Execute(connection, transaction, "INSERT INTO audit(at,event,task_id,tool,request_hash,error_code) VALUES($at,$event,$task,$tool,$hash,$code)",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$event", name), ("$task", task?.ToString()), ("$tool", tool), ("$hash", hash), ("$code", code));

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Query(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static SqliteCommand Query(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
