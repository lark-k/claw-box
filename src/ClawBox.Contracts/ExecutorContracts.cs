using System.Text.Json;

namespace ClawBox.Contracts;

public sealed record ToolCall(Guid RequestId, string Tool, JsonElement Arguments);
public sealed record ApprovalResponse(Guid ApprovalId, DateTimeOffset ExpiresAt, string RequestHash);
public sealed record SubmitTaskRequest(Guid ApprovalId, ToolCall Call);
public sealed record ExecutorTask(Guid Id, Guid RequestId, string Tool, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, JsonElement? Result, string? ErrorCode);
public sealed record ToolDescription(string Name, string Description, JsonElement InputSchema, bool MutatesFiles);
public sealed record ToolRequest(ToolCall Call, string RequestHash, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ExecutorTask? Task);
public sealed record RequestDecision(string RequestHash, bool Approve);
public sealed record PendingToolRequest(Guid RequestId, string Tool, string RequestHash,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public static class RequestStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Canceled = "canceled";
    public const string Interrupted = "interrupted";
}

public static class TaskStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public const string Interrupted = "interrupted";
}
