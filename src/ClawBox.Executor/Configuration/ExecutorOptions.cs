namespace ClawBox.Executor.Configuration;

public sealed class ExecutorOptions
{
    public const string SectionName = "Executor";
    public int Port { get; set; } = 18790;
    public string WorkerToken { get; set; } = "";
    public string AdminToken { get; set; } = "";
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "executor");
    public string[] AllowedRoots { get; set; } = [];
    public int MaxInputBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxDocumentBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxExpandedDocumentBytes { get; set; } = 64 * 1024 * 1024;
    public int MaxQueuedTasks { get; set; } = 100;
    public int MaxPendingRequests { get; set; } = 100;

    public void Validate()
    {
        if (Port is < 1024 or > 65535 || WorkerToken.Length < 32 || AdminToken.Length < 32 ||
            WorkerToken == AdminToken || AllowedRoots.Length == 0 || MaxInputBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaxDocumentBytes is < 1024 or > 64 * 1024 * 1024 || MaxExpandedDocumentBytes < MaxDocumentBytes ||
            MaxQueuedTasks is < 1 or > 1000 || MaxPendingRequests is < 1 or > 1000)
            throw new InvalidOperationException("Executor configuration is invalid; run scripts/dev/Start-Executor.ps1.");
        if (!Path.IsPathFullyQualified(DataDirectory) || AllowedRoots.Any(p => !Path.IsPathFullyQualified(p)))
            throw new InvalidOperationException("Data directory and allowed roots must be absolute paths.");
        foreach (string root in AllowedRoots)
            if (string.Equals(Path.GetPathRoot(root), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Whole-drive access is not allowed.");
    }
}
