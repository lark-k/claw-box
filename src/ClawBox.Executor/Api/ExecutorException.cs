namespace ClawBox.Executor.Api;

public sealed class ExecutorException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
