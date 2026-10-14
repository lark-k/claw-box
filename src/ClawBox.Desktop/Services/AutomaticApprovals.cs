using ClawBox.Contracts;

namespace ClawBox.Desktop.Services;

// A local user selects the mode. Approval is still submitted separately for each exact request.
public sealed class AutomaticApprovals
{
    private readonly HashSet<Guid> attempted = [];
    public void Reset() => attempted.Clear();
    public async Task Apply(string mode, IEnumerable<ToolRequest> requests, Func<ToolRequest, Task> approve)
    {
        if (mode != "fullAccess") return;
        foreach (ToolRequest request in requests)
            if (request.Status == RequestStates.Pending && request.ExpiresAt > DateTimeOffset.UtcNow && attempted.Add(request.Call.RequestId))
                await approve(request);
    }
}
